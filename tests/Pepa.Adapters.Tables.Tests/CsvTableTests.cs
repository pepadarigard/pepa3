using System.Text;
using System.Text.Json.Nodes;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Tables.Tests;

/// <summary>CSV: кодировка и разделитель как у файла, нетронутые строки — байт в байт, данные не становятся формулами.</summary>
public sealed class CsvTableTests : TableTestKit
{
    private static readonly Encoding Windows1251 = GetWindows1251();

    private static Encoding GetWindows1251()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251);
    }

    [Fact]
    public async Task Excel_csv_in_windows_1251_with_semicolons_and_quotes_is_read()
    {
        await File.WriteAllBytesAsync(P("клиенты.csv"), Windows1251.GetBytes(
            "Имя;Комментарий;Сумма\r\nАнна;\"Сказала \"\"да\"\"; позже\";91,5\r\n;;\r\nБорис;\"две\r\nстроки\";\r\n"));

        var rows = await ReadRows("клиенты.csv");

        Assert.Equal(2, rows.Count);
        Assert.Equal("Сказала \"да\"; позже", rows[0]!["Комментарий"]!.GetValue<string>());
        Assert.Equal("91,5", rows[0]!["Сумма"]!.GetValue<string>());
        Assert.Equal("две\r\nстроки", rows[1]!["Комментарий"]!.GetValue<string>());
        Assert.Null(rows[1]!["Сумма"]);
    }

    [Fact]
    public async Task Append_keeps_the_encoding_and_every_existing_byte()
    {
        var original = Windows1251.GetBytes("Имя;Сумма;Заметка\r\nАнна;\"1\";x\r\n");
        await File.WriteAllBytesAsync(P("учёт.csv"), original);

        var (result, context) = await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "учёт.csv",
            ["values"] = new JsonObject { ["Имя"] = "Вера", ["Сумма"] = 91.5, ["Заметка"] = "с; точкой с запятой" },
        });

        Assert.Equal(3, Ok(result).Output!["row"]!.GetValue<int>());
        var bytes = await File.ReadAllBytesAsync(P("учёт.csv"));
        Assert.Equal(original, bytes[..original.Length]);
        Assert.Equal("Вера;91,5;\"с; точкой с запятой\"\r\n", Windows1251.GetString(bytes[original.Length..]));
        Assert.Equal(VerificationOutcome.Done, (await new AppendRowNode(Workspace).VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Comma_separated_utf8_file_without_final_newline_gets_a_proper_line()
    {
        await File.WriteAllTextAsync(P("data.csv"), "name,price\nTea,1.5", new UTF8Encoding(false));

        await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "data.csv", ["values"] = new JsonObject { ["name"] = "Кофе, чёрный", ["price"] = 2.25 },
        });

        Assert.Equal("name,price\nTea,1.5\n\"Кофе, чёрный\",2.25\n", await File.ReadAllTextAsync(P("data.csv")));
    }

    [Fact]
    public async Task Text_that_excel_would_run_as_a_formula_is_written_with_an_apostrophe()
    {
        await File.WriteAllTextAsync(P("ввод.csv"), "Текст;Число\r\n", new UTF8Encoding(true));

        await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "ввод.csv", ["values"] = new JsonObject { ["Текст"] = "=HYPERLINK(\"http://x\")", ["Число"] = "-5" },
        });
        await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "ввод.csv", ["values"] = new JsonObject { ["Текст"] = "@SUM(A1)", ["Число"] = "+7 916" },
        });

        var rows = await ReadRows("ввод.csv");
        Assert.Equal("'=HYPERLINK(\"http://x\")", rows[0]!["Текст"]!.GetValue<string>());
        Assert.Equal("-5", rows[0]!["Число"]!.GetValue<string>());
        Assert.Equal("'@SUM(A1)", rows[1]!["Текст"]!.GetValue<string>());
        Assert.Equal("'+7 916", rows[1]!["Число"]!.GetValue<string>());
    }

    [Fact]
    public async Task Text_the_file_encoding_cannot_hold_is_an_error_and_nothing_is_written()
    {
        var original = Windows1251.GetBytes("Имя\r\nАнна\r\n");
        await File.WriteAllBytesAsync(P("старый.csv"), original);

        var error = await Assert.ThrowsAsync<NodeInputException>(() => Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "старый.csv", ["values"] = new JsonObject { ["Имя"] = "Zoë 😀" },
        }));

        Assert.Contains("Windows-1251", error.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(P("старый.csv")));
    }

    [Fact]
    public async Task Update_rewrites_only_the_found_row()
    {
        var text = "Логин;Статус;Заметка\r\nanna;новый;\"как есть \"\"в кавычках\"\"\"\r\nboris;новый;x\r\n";
        await File.WriteAllTextAsync(P("аккаунты.csv"), text, new UTF8Encoding(false));

        var (result, context) = await Run(new UpdateRowNode(Workspace), new JsonObject
        {
            ["path"] = "аккаунты.csv", ["column"] = "Логин", ["value"] = "Boris", ["values"] = new JsonObject { ["Статус"] = "готово" },
        });

        Assert.Equal(3, Ok(result).Output!["row"]!.GetValue<int>());
        Assert.Equal(text.Replace("boris;новый;x", "boris;готово;x"), await File.ReadAllTextAsync(P("аккаунты.csv")));
        Assert.Equal(VerificationOutcome.Done, (await new UpdateRowNode(Workspace).VerifyAsync(context, CancellationToken.None)).Outcome);

        // Повтор с теми же значениями файл не трогает.
        var written = File.GetLastWriteTimeUtc(P("аккаунты.csv"));
        var (again, _) = await Run(new UpdateRowNode(Workspace), context.Input.DeepClone().AsObject());
        Assert.Contains("уже эти значения", Ok(again).Message);
        Assert.Equal(written, File.GetLastWriteTimeUtc(P("аккаунты.csv")));
    }

    [Fact]
    public async Task Update_verification_tells_old_values_from_someone_elses()
    {
        await File.WriteAllTextAsync(P("a.csv"), "Логин;Статус\r\nanna;новый\r\n");
        var node = new UpdateRowNode(Workspace);
        var context = Context(new JsonObject
        {
            ["path"] = "a.csv", ["column"] = "Логин", ["value"] = "anna", ["values"] = new JsonObject { ["Статус"] = "готово" },
        });
        context = context with { Checkpoint = await node.PrepareAsync(context, CancellationToken.None) };

        Assert.Equal(VerificationOutcome.NotDone, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        await File.WriteAllTextAsync(P("a.csv"), "Логин;Статус\r\nanna;в работе\r\n");
        Assert.Equal(VerificationOutcome.Inconclusive, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Row_that_moved_between_preparing_and_acting_is_not_changed()
    {
        await File.WriteAllTextAsync(P("a.csv"), "Логин;Статус\r\nanna;новый\r\n");
        var node = new UpdateRowNode(Workspace);
        var context = Context(new JsonObject
        {
            ["path"] = "a.csv", ["column"] = "Логин", ["value"] = "anna", ["values"] = new JsonObject { ["Статус"] = "готово" },
        });
        context = context with { Checkpoint = await node.PrepareAsync(context, CancellationToken.None) };
        await File.WriteAllTextAsync(P("a.csv"), "Логин;Статус\r\nboris;новый\r\nanna;новый\r\n");

        var failure = Assert.IsType<NodeResult.Failure>(await node.ExecuteAsync(context, CancellationToken.None));
        Assert.True(failure.Retryable);
        Assert.Equal("Логин;Статус\r\nboris;новый\r\nanna;новый\r\n", await File.ReadAllTextAsync(P("a.csv")));
    }

    [Fact]
    public async Task New_csv_is_utf8_with_bom_and_semicolons_for_excel()
    {
        await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "новые/итог.csv", ["values"] = new JsonObject { ["Имя"] = "Анна", ["Сумма"] = 1.5, ["Готово"] = false },
        });

        var bytes = await File.ReadAllBytesAsync(P("новые/итог.csv"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("Имя;Сумма;Готово\r\nАнна;1,5;нет\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task List_values_go_by_column_order_and_rows_without_header_by_letters()
    {
        await File.WriteAllTextAsync(P("a.csv"), "1;2\r\n");
        await Run(new AppendRowNode(Workspace), new JsonObject { ["path"] = "a.csv", ["header"] = false, ["values"] = new JsonArray("x", "y") });
        await Run(new AppendRowNode(Workspace), new JsonObject
        {
            ["path"] = "a.csv", ["header"] = false, ["values"] = new JsonObject { ["c"] = "z" },
        });

        var rows = await ReadRows("a.csv", header: false);
        Assert.Equal(3, rows.Count);
        Assert.Equal("y", rows[1]!["B"]!.GetValue<string>());
        Assert.Equal("z", rows[2]!["C"]!.GetValue<string>());
        Assert.Equal("1;2\r\nx;y\r\n;;z\r\n", await File.ReadAllTextAsync(P("a.csv")));
    }
}
