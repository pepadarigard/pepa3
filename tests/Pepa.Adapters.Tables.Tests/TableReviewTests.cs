using System.Text;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Pepa.Adapters.Tables.Tests;

/// <summary>Замечания независимой проверки блоков таблиц: каждое — сценарий, который раньше портил данные или сверку.</summary>
public sealed class TableReviewTests : TableTestKit
{
    private const string Book = "клиенты.xlsx";

    private async Task<NodeResult> Find(string path, string column, string value) =>
        await new FindRowNode(Workspace).ExecuteAsync(Context(new { path, column, value, saveTo = "row" }), CancellationToken.None);

    private void Edit(string relative, Action<WorksheetPart> change)
    {
        using var document = SpreadsheetDocument.Open(P(relative), true);
        var workbook = document.WorkbookPart!;
        change((WorksheetPart)workbook.GetPartById(workbook.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!.Value!));
    }

    [Fact]
    public async Task Long_account_numbers_and_leading_zeros_are_never_equal_to_other_values()
    {
        await File.WriteAllTextAsync(P("счета.csv"), "Счёт;Код;Статус\r\n40817810099910004312;0012;новый\r\n40817810099910004313;12;новый\r\n");

        var row = Ok(await Find("счета.csv", "Счёт", "40817810099910004313")).VariableUpdates["row"]!;
        Assert.Equal("12", row["Код"]!.GetValue<string>());
        Assert.Equal("новый", Ok(await Find("счета.csv", "Код", "0012")).VariableUpdates["row"]!["Статус"]!.GetValue<string>());
        Assert.Equal("40817810099910004313", Ok(await Find("счета.csv", "Код", "12")).VariableUpdates["row"]!["Счёт"]!.GetValue<string>());
        // Дробная часть через запятую или точку — одно число, но «1.10» и «1.1» в тексте — разное.
        await File.WriteAllTextAsync(P("цены.csv"), "Товар;Цена\r\nчай;91,5\r\nверсия;1.10\r\n");
        Assert.Equal("чай", Ok(await Find("цены.csv", "Цена", "91.5")).VariableUpdates["row"]!["Товар"]!.GetValue<string>());
        Assert.Equal(Ports.False, Ok(await Find("цены.csv", "Цена", "1.1")).Port);
    }

    [Fact]
    public async Task Update_with_different_case_or_digits_is_written_not_reported_as_already_there()
    {
        await File.WriteAllTextAsync(P("a.csv"), "Логин;Статус;Код\r\nanna;готово;7\r\n");

        var (result, _) = await Run(new UpdateRowNode(Workspace), new JsonObject
        {
            ["path"] = "a.csv", ["column"] = "Логин", ["value"] = "anna", ["values"] = new JsonObject { ["Статус"] = "Готово", ["Код"] = "007" },
        });

        Assert.DoesNotContain("уже", Ok(result).Message);
        Assert.Equal("Логин;Статус;Код\r\nanna;Готово;007\r\n", await File.ReadAllTextAsync(P("a.csv")));
    }

    [Fact]
    public async Task Xlsx_values_that_excel_stores_differently_still_verify_as_written()
    {
        CreateBook(Book);
        var node = new AppendRowNode(Workspace);
        // Перевод строки «\r\n», число в ячейке с форматом даты (оформление из строки выше), число с порядком.
        var (result, context) = await Run(node, new JsonObject
        {
            ["path"] = Book,
            ["values"] = new JsonObject { ["Имя"] = "две\r\nстроки", ["Дата"] = 46000, ["Сумма"] = 0.0000001 },
        });

        Ok(result);
        Assert.Equal(VerificationOutcome.Done, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        var row = (await ReadRows(Book))[2]!;
        Assert.Equal("две\nстроки", row["Имя"]!.GetValue<string>());
        Assert.Equal("2025-12-09", row["Дата"]!.GetValue<string>());

        // Повтор «Изменить» с теми же значениями файл не переписывает.
        var update = new JsonObject { ["path"] = Book, ["column"] = "Имя", ["value"] = "Анна", ["values"] = new JsonObject { ["Имя"] = "Анна\r\nК." } };
        var (first, updated) = await Run(new UpdateRowNode(Workspace), update);
        Ok(first);
        Assert.Equal(VerificationOutcome.Done, (await new UpdateRowNode(Workspace).VerifyAsync(updated, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Update_verification_notices_that_the_planned_row_is_another_record_now()
    {
        await File.WriteAllTextAsync(P("a.csv"), "Логин;Статус\r\nboris;новый\r\nanna;новый\r\n");
        var node = new UpdateRowNode(Workspace);
        var context = Context(new JsonObject
        {
            ["path"] = "a.csv", ["column"] = "Логин", ["value"] = "anna", ["values"] = new JsonObject { ["Статус"] = "готово" },
        });
        context = context with { Checkpoint = await node.PrepareAsync(context, CancellationToken.None) };

        // Сбой до действия, потом таблицу отсортировали: в строке 3 теперь другая запись, уже «готово».
        await File.WriteAllTextAsync(P("a.csv"), "Логин;Статус\r\nanna;новый\r\nzoe;готово\r\n");

        Assert.Equal(VerificationOutcome.Inconclusive, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Empty_search_value_is_an_error_not_a_match_with_an_empty_cell()
    {
        await File.WriteAllTextAsync(P("a.csv"), "Логин;Статус\r\nanna;новый\r\n;Итого\r\n");

        var update = await Assert.ThrowsAsync<NodeInputException>(() => Run(new UpdateRowNode(Workspace), new JsonObject
        {
            ["path"] = "a.csv", ["column"] = "Логин", ["value"] = " ", ["values"] = new JsonObject { ["Статус"] = "x" },
        }));
        Assert.Contains("пустое", update.Message);
        await Assert.ThrowsAsync<NodeInputException>(() => Find("a.csv", "Логин", ""));
        Assert.Equal("Логин;Статус\r\nanna;новый\r\n;Итого\r\n", await File.ReadAllTextAsync(P("a.csv")));
    }

    [Fact]
    public async Task New_xlsm_is_a_macro_enabled_book()
    {
        await Run(new AppendRowNode(Workspace), new JsonObject { ["path"] = "макросы.xlsm", ["values"] = new JsonObject { ["A"] = 1 } });

        using var document = SpreadsheetDocument.Open(P("макросы.xlsm"), false);
        Assert.Equal(DocumentFormat.OpenXml.SpreadsheetDocumentType.MacroEnabledWorkbook, document.DocumentType);
    }

    [Fact]
    public async Task Repeated_column_names_are_ambiguous_and_generated_names_do_not_collide()
    {
        await File.WriteAllTextAsync(P("a.csv"), "Имя;Имя;Имя (2);Статус;статус\r\nа;б;в;г;д\r\n");

        var row = (await ReadRows("a.csv"))[0]!.AsObject();
        Assert.Equal("а", row["Имя (A)"]!.GetValue<string>());
        Assert.Equal("б", row["Имя (B)"]!.GetValue<string>());
        Assert.Equal("в", row["Имя (2)"]!.GetValue<string>());
        var error = await Assert.ThrowsAsync<NodeInputException>(() => Find("a.csv", "Имя", "а"));
        Assert.Contains("несколько колонок «Имя»", error.Message);
        await Assert.ThrowsAsync<NodeInputException>(() => Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "a.csv", ["values"] = new JsonObject { ["статус"] = "x" },
        }));

        await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "a.csv", ["values"] = new JsonObject { ["Имя (2)"] = "C", ["Имя (B)"] = "B" },
        });
        Assert.EndsWith("\r\n;B;C\r\n", await File.ReadAllTextAsync(P("a.csv")));
    }

    [Fact]
    public async Task Csv_with_an_unclosed_quote_is_not_written()
    {
        const string text = "Имя;Заметка\r\nАнна;\"незакрытая\r\n";
        await File.WriteAllTextAsync(P("a.csv"), text);

        var error = await Assert.ThrowsAsync<NodeInputException>(() => Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "a.csv", ["values"] = new JsonObject { ["Имя"] = "Вера", ["Заметка"] = "x" },
        }));

        Assert.Contains("кавычка не закрыта", error.Message);
        Assert.Equal(text, await File.ReadAllTextAsync(P("a.csv")));
    }

    [Fact]
    public async Task Update_right_below_a_smart_table_does_not_pull_the_row_into_it()
    {
        CreateBook(Book);
        Edit(Book, part =>
        {
            var data = part.Worksheet!.GetFirstChild<S.SheetData>()!;
            data.AppendChild(new S.Row(new S.Cell(new S.InlineString(new S.Text("Итого"))) { CellReference = "A4", DataType = S.CellValues.InlineString })
            {
                RowIndex = 4,
            });
        });

        Ok((await Run(new UpdateRowNode(Workspace), new JsonObject
        {
            ["path"] = Book, ["column"] = "Имя", ["value"] = "Итого", ["values"] = new JsonObject { ["Сумма"] = 199.5 },
        })).Result);

        using var document = SpreadsheetDocument.Open(P(Book), false);
        var part = (WorksheetPart)document.WorkbookPart!.GetPartById(document.WorkbookPart.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!.Value!);
        Assert.Equal("A1:D3", part.TableDefinitionParts.Single().Table!.Reference!.Value);
    }

    [Fact]
    public async Task Characters_that_xml_cannot_hold_are_rejected_before_writing()
    {
        CreateBook(Book);
        var error = await Assert.ThrowsAsync<NodeInputException>(() => Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = Book, ["values"] = new JsonObject { ["Имя"] = "a￾b" },
        }));
        Assert.Contains("служебные знаки", error.Message);
    }

    [Fact]
    public async Task Excel_escapes_are_read_as_characters_and_text_that_looks_like_one_stays_text()
    {
        CreateBook(Book);
        Edit(Book, part => part.Worksheet!.Descendants<S.Cell>().First(c => c.CellReference == "B3").InlineString =
            new S.InlineString(new S.Text("строка1_x000D_\nстрока2")));

        Assert.Equal("строка1\r\nстрока2", (await ReadRows(Book))[1]!["Телефон"]!.GetValue<string>());

        var (result, context) = await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = Book, ["values"] = new JsonObject { ["Имя"] = "код _x0041_" },
        });
        Ok(result);
        Assert.Equal("код _x0041_", (await ReadRows(Book))[2]!["Имя"]!.GetValue<string>());
        Assert.Equal("код _x005F_x0041_", CellOf(P(Book), "Клиенты", "A4")!.InlineString!.Text!.Text);
        Assert.Equal(VerificationOutcome.Done, (await new AppendRowNode(Workspace).VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Cells_of_an_array_formula_and_protected_sheets_are_not_written()
    {
        CreateBook(Book);
        Edit(Book, part =>
        {
            var b2 = part.Worksheet!.Descendants<S.Cell>().First(c => c.CellReference == "B2");
            b2.CellFormula = new S.CellFormula("A2:A3") { FormulaType = S.CellFormulaValues.Array, Reference = "B2:B3" };
        });

        var array = await Assert.ThrowsAsync<NodeInputException>(() => Run(new UpdateRowNode(Workspace), new JsonObject
        {
            ["path"] = Book, ["column"] = "Имя", ["value"] = "Борис", ["values"] = new JsonObject { ["Телефон"] = "x" },
        }));
        Assert.Contains("формула", array.Message);

        Edit(Book, part => part.Worksheet!.InsertAfter(new S.SheetProtection { Sheet = true }, part.Worksheet.GetFirstChild<S.SheetData>()));
        var protectedSheet = await Assert.ThrowsAsync<NodeInputException>(() => Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = Book, ["values"] = new JsonObject { ["Имя"] = "Вера" },
        }));
        Assert.Contains("защищён", protectedSheet.Message);
    }

    [Fact]
    public async Task Excel_sep_line_sets_the_delimiter_and_is_not_a_row()
    {
        await File.WriteAllTextAsync(P("a.csv"), "sep=,\r\nИмя,Город\r\nАнна,Москва\r\n");

        var row = Assert.Single(await ReadRows("a.csv"))!;
        Assert.Equal("Москва", row["Город"]!.GetValue<string>());
        await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "a.csv", ["values"] = new JsonObject { ["Имя"] = "Вера", ["Город"] = "Тверь" },
        });
        Assert.Equal("sep=,\r\nИмя,Город\r\nАнна,Москва\r\nВера,Тверь\r\n", await File.ReadAllTextAsync(P("a.csv")));
    }

    [Fact]
    public async Task Early_1900_dates_and_durations_are_read_as_excel_shows_them()
    {
        CreateBook(Book);
        Edit(Book, part =>
        {
            var cells = part.Worksheet!.Descendants<S.Cell>().ToList();
            cells.First(c => c.CellReference == "C2").CellValue = new S.CellValue("1");
            cells.First(c => c.CellReference == "C3").CellValue = new S.CellValue("59");
        });
        using (var document = SpreadsheetDocument.Open(P(Book), true))
        {
            // Формат [ч]:мм:сс (встроенный 46) у суммы Анны: 1,5 суток — «36:00:00», а не дата.
            var formats = document.WorkbookPart!.WorkbookStylesPart!.Stylesheet!.CellFormats!;
            formats.AppendChild(new S.CellFormat { NumberFormatId = 46, ApplyNumberFormat = true });
            formats.Count = 4;
            var part = (WorksheetPart)document.WorkbookPart.GetPartById(document.WorkbookPart.Workbook!.Sheets!.Elements<S.Sheet>().First().Id!.Value!);
            var d2 = part.Worksheet!.Descendants<S.Cell>().First(c => c.CellReference == "D2");
            d2.StyleIndex = 3;
            d2.CellValue = new S.CellValue("1.5");
        }

        var rows = await ReadRows(Book);
        Assert.Equal("1900-01-01", rows[0]!["Дата"]!.GetValue<string>());
        Assert.Equal("1900-02-28", rows[1]!["Дата"]!.GetValue<string>());
        Assert.Equal("36:00:00", rows[0]!["Сумма"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ascii_only_csv_without_bom_gets_cyrillic_in_utf8_off_windows()
    {
        await File.WriteAllBytesAsync(P("a.csv"), Encoding.ASCII.GetBytes("login;status\r\nanna;new\r\n"));

        await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "a.csv", ["values"] = new JsonObject { ["login"] = "вера", ["status"] = "новый" },
        });

        var bytes = await File.ReadAllBytesAsync(P("a.csv"));
        var expected = OperatingSystem.IsWindows() ? Encoding.GetEncoding(0) : Encoding.UTF8;
        Assert.Equal("login;status\r\nanna;new\r\nвера;новый\r\n", expected.GetString(bytes));
    }
}
