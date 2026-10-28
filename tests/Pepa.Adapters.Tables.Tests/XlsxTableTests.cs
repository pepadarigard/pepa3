using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Pepa.Adapters.Tables.Tests;

/// <summary>Таблицы Excel: чтение как в Excel, изменение без потери оформления, формул и других листов.</summary>
public sealed class XlsxTableTests : TableTestKit
{
    private const string Book = "таблицы/клиенты.xlsx";

    [Fact]
    public async Task Rows_come_by_column_names_with_numbers_dates_and_formula_results()
    {
        CreateBook(Book);
        var rows = await ReadRows(Book);

        Assert.Equal(2, rows.Count);
        var anna = rows[0]!.AsObject();
        Assert.Equal("Анна", anna["Имя"]!.GetValue<string>());
        Assert.Equal(79161234567, anna["Телефон"]!.GetValue<long>());
        Assert.Equal("2023-03-15", anna["Дата"]!.GetValue<string>());
        Assert.Equal(100.5, anna["Сумма"]!.GetValue<double>());
        // Колонка без названия (формула в F) не теряется: она называется буквой.
        Assert.Equal(201, anna["F"]!.GetValue<long>());
        var boris = rows[1]!.AsObject();
        Assert.Equal("8 916 000-00-00", boris["Телефон"]!.GetValue<string>());
        Assert.Equal("2023-03-15T12:00:00", boris["Дата"]!.GetValue<string>());
        Assert.Null(boris["F"]);
    }

    [Fact]
    public async Task Without_header_columns_are_letters_and_another_sheet_is_read_by_name()
    {
        CreateBook(Book);
        var rows = await ReadRows(Book, header: false);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Имя", rows[0]!["A"]!.GetValue<string>());

        var totals = await ReadRows(Book, sheet: "итоги", header: false);
        Assert.Equal(199.5, Assert.Single(totals)!["A"]!.GetValue<double>());

        var missing = Assert.Throws<NodeInputException>(() => new ReadTableNode(Workspace).ExecuteAsync(
            Context(new { path = Book, sheet = "Заказы", saveTo = "rows" }), CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.Contains("«Клиенты», «Итоги»", missing.Message);
    }

    [Fact]
    public async Task Appended_row_keeps_formatting_formulas_other_sheets_and_grows_the_smart_table()
    {
        CreateBook(Book);
        var (result, context) = await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = Book,
            ["values"] = new JsonObject { ["Имя"] = "Вера", ["Сумма"] = 50, ["Телефон"] = "=1+1" },
        });

        Assert.Equal(4, Ok(result).Output!["row"]!.GetValue<int>());
        var path = P(Book);
        Assert.Empty(SchemaErrors(path));
        // Оформление денег перешло из строки выше, текст с «=» — текст, а не формула.
        Assert.Equal(2u, CellOf(path, "Клиенты", "D4")!.StyleIndex!.Value);
        Assert.Equal(S.CellValues.InlineString, CellOf(path, "Клиенты", "B4")!.DataType!.Value);
        Assert.Null(CellOf(path, "Клиенты", "B4")!.CellFormula);
        Assert.Null(CellOf(path, "Клиенты", "C4"));
        Assert.Equal("D2*2", CellOf(path, "Клиенты", "F2")!.CellFormula!.Text);
        Assert.Equal("SUM(Клиенты!D:D)", CellOf(path, "Итоги", "A1")!.CellFormula!.Text);
        using (var document = SpreadsheetDocument.Open(path, false))
        {
            var clients = (WorksheetPart)document.WorkbookPart!.GetPartById(
                document.WorkbookPart.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!.Value!);
            var table = clients.TableDefinitionParts.Single().Table!;
            Assert.Equal("A1:D4", table.Reference!.Value);
            Assert.Equal("A1:D4", table.AutoFilter!.Reference!.Value);
            Assert.Equal("A1:F4", clients.Worksheet!.SheetDimension!.Reference!.Value);
            Assert.True(document.WorkbookPart.Workbook.CalculationProperties!.FullCalculationOnLoad!.Value);
        }

        var rows = await ReadRows(Book);
        Assert.Equal("Вера", rows[2]!["Имя"]!.GetValue<string>());
        Assert.Equal("=1+1", rows[2]!["Телефон"]!.GetValue<string>());
        Assert.Equal(VerificationOutcome.Done, (await new AppendRowNode(Workspace).VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Unknown_column_or_list_value_is_an_error_and_the_file_stays_the_same()
    {
        CreateBook(Book);
        var before = SHA256.HashData(await File.ReadAllBytesAsync(P(Book)));
        var node = new AppendRowNode(Workspace);

        var unknown = await Assert.ThrowsAsync<NodeInputException>(() =>
            Run(node, new JsonObject { ["path"] = Book, ["values"] = new JsonObject { ["Почта"] = "a@b.c" } }));
        Assert.Contains("нет колонки «Почта»", unknown.Message);
        Assert.Contains("«Имя»", unknown.Message);
        var list = await Assert.ThrowsAsync<NodeInputException>(() =>
            Run(node, new JsonObject { ["path"] = Book, ["values"] = new JsonObject { ["Имя"] = new JsonArray("a") } }));
        Assert.Contains("список", list.Message);
        await Assert.ThrowsAsync<NodeInputException>(() =>
            Run(node, new JsonObject { ["path"] = Book, ["values"] = new JsonObject { ["Имя"] = null } }));

        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(P(Book))));
    }

    [Fact]
    public async Task Verification_finds_the_row_only_among_rows_added_after_the_step_started()
    {
        CreateBook(Book);
        var node = new AppendRowNode(Workspace);
        var input = new JsonObject { ["path"] = Book, ["values"] = new JsonObject { ["Имя"] = "Анна", ["Сумма"] = 100.5 } };
        var context = Context(input);
        context = context with { Checkpoint = await node.PrepareAsync(context, CancellationToken.None) };

        // Такая же строка уже есть выше (строка 2) — но она была до шага: строка не добавлена.
        Assert.Equal(VerificationOutcome.NotDone, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        Ok(await node.ExecuteAsync(context, CancellationToken.None));
        Assert.Equal(VerificationOutcome.Done, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        // Кто-то добавил такую же строку ещё раз — чья из них, неясно.
        Ok(await node.ExecuteAsync(context with { Checkpoint = new JsonObject { ["exists"] = true, ["lastRow"] = 4 } }, CancellationToken.None));
        Assert.Equal(VerificationOutcome.Inconclusive, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Update_changes_only_given_cells_of_the_found_row()
    {
        CreateBook(Book);
        var (result, context) = await Run(new UpdateRowNode(Workspace), new JsonObject
        {
            ["path"] = Book, ["column"] = "имя", ["value"] = " БОРИС ", ["values"] = new JsonObject { ["Сумма"] = 120, ["Телефон"] = null },
        });

        Assert.Equal(Ports.Out, Ok(result).Port);
        Assert.Equal(3, Ok(result).Output!["row"]!.GetValue<int>());
        var path = P(Book);
        Assert.Empty(SchemaErrors(path));
        Assert.Equal("120", CellOf(path, "Клиенты", "D3")!.CellValue!.Text);
        Assert.Equal(2u, CellOf(path, "Клиенты", "D3")!.StyleIndex!.Value);
        Assert.Null(CellOf(path, "Клиенты", "B3")!.CellValue);
        Assert.Null(CellOf(path, "Клиенты", "B3")!.InlineString);
        var rows = await ReadRows(Book);
        Assert.Equal(100.5, rows[0]!["Сумма"]!.GetValue<double>());
        Assert.Equal("2023-03-15T12:00:00", rows[1]!["Дата"]!.GetValue<string>());
        Assert.Equal(VerificationOutcome.Done, (await new UpdateRowNode(Workspace).VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Update_does_not_replace_a_formula_and_not_found_goes_to_its_port()
    {
        CreateBook(Book);
        var node = new UpdateRowNode(Workspace);
        var formula = await Assert.ThrowsAsync<NodeInputException>(() => Run(node, new JsonObject
        {
            ["path"] = Book, ["column"] = "Имя", ["value"] = "Анна", ["values"] = new JsonObject { ["F"] = 1 },
        }));
        Assert.Contains("формула", formula.Message);

        var (missing, _) = await Run(node, new JsonObject
        {
            ["path"] = Book, ["column"] = "Имя", ["value"] = "Вера", ["values"] = new JsonObject { ["Сумма"] = 1 },
        });
        Assert.Equal(Ports.False, Ok(missing).Port);
    }

    [Fact]
    public async Task Several_matching_rows_are_an_error_not_a_guess()
    {
        CreateBook(Book);
        await Run(new AppendRowNode(Workspace), new JsonObject { ["path"] = Book, ["values"] = new JsonObject { ["Имя"] = "Анна" } });
        var before = SHA256.HashData(await File.ReadAllBytesAsync(P(Book)));

        var (update, _) = await Run(new UpdateRowNode(Workspace), new JsonObject
        {
            ["path"] = Book, ["column"] = "Имя", ["value"] = "Анна", ["values"] = new JsonObject { ["Сумма"] = 1 },
        });
        var failure = Assert.IsType<NodeResult.Failure>(update);
        Assert.False(failure.Retryable);
        Assert.Contains("строки 2, 4", failure.Error);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(P(Book))));

        var find = new FindRowNode(Workspace);
        Assert.IsType<NodeResult.Failure>(await find.ExecuteAsync(
            Context(new { path = Book, column = "Имя", value = "анна", saveTo = "row" }), CancellationToken.None));
        var all = Ok(await find.ExecuteAsync(
            Context(new { path = Book, column = "Имя", value = "анна", which = "all", saveTo = "row" }), CancellationToken.None));
        Assert.Equal(2, all.VariableUpdates["row"]!.AsArray().Count);
        var first = Ok(await find.ExecuteAsync(
            Context(new { path = Book, column = "Телефон", value = "79161234567", which = "first", saveTo = "row" }), CancellationToken.None));
        Assert.Equal(100.5, first.VariableUpdates["row"]!["Сумма"]!.GetValue<double>());
    }

    [Fact]
    public async Task Table_open_in_excel_is_not_changed()
    {
        CreateBook(Book);
        await File.WriteAllTextAsync(P("таблицы/~$клиенты.xlsx"), "owner");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(P(Book)));

        var (result, _) = await Run(new AppendRowNode(Workspace), new JsonObject { ["path"] = Book, ["values"] = new JsonObject { ["Имя"] = "Вера" } });

        var failure = Assert.IsType<NodeResult.Failure>(result);
        Assert.True(failure.Retryable);
        Assert.Contains("открыта в Excel", failure.Error);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(P(Book))));
        Assert.DoesNotContain(Directory.GetFiles(P("таблицы")), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task New_book_is_created_with_a_header_and_long_numbers_stay_exact()
    {
        var (result, _) = await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "отчёт.xlsx", ["sheet"] = "Итоги",
            ["values"] = new JsonObject { ["Карта"] = JsonValue.Create(4276123456789012345L), ["Готово"] = true, ["Сумма"] = 10.25 },
        });

        Assert.True(Ok(result).Output!["created"]!.GetValue<bool>());
        Assert.Empty(SchemaErrors(P("отчёт.xlsx")));
        var row = Assert.Single(await ReadRows("отчёт.xlsx"))!;
        Assert.Equal("4276123456789012345", row["Карта"]!.GetValue<string>());
        Assert.True(row["Готово"]!.GetValue<bool>());
        Assert.Equal(10.25, row["Сумма"]!.GetValue<double>());
        Assert.Equal("Итоги", Ok(result).Output!["sheet"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("старая.xls", "старый формат")]
    [InlineData("заметки.txt", "не таблица")]
    public async Task Unsupported_files_are_explained(string path, string expected)
    {
        await File.WriteAllTextAsync(P(path), "x");
        var error = Assert.Throws<NodeInputException>(() => new ReadTableNode(Workspace).ExecuteAsync(
            Context(new { path, saveTo = "rows" }), CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Damaged_book_is_an_error_not_a_crash()
    {
        await File.WriteAllTextAsync(P("битая.xlsx"), "не zip");
        var error = Assert.Throws<NodeInputException>(() => new ReadTableNode(Workspace).ExecuteAsync(
            Context(new { path = "битая.xlsx", saveTo = "rows" }), CancellationToken.None).AsTask().GetAwaiter().GetResult());
        Assert.Contains("не открывается", error.Message);
    }
}
