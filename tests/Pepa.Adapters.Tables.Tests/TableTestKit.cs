using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Pepa.Adapters.FileSystem;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Serialization;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace Pepa.Adapters.Tables.Tests;

/// <summary>Рабочая папка во временном каталоге и вызов блоков таблиц так, как их вызывает движок.</summary>
public abstract class TableTestKit : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "pepa-table-tests", Guid.NewGuid().ToString("N"));
    protected readonly Workspace Workspace;

    protected TableTestKit()
    {
        Directory.CreateDirectory(Root);
        Workspace = new Workspace(Root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    protected string P(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    protected static NodeExecutionContext Context(object input, JsonObject? checkpoint = null) => new()
    {
        ProcessId = "p",
        Node = new NodeDefinition { Id = "n", Type = "test" },
        Input = input as JsonObject ?? (JsonObject)JsonSerializer.SerializeToNode(input, PepaJson.Options)!,
        Checkpoint = checkpoint,
        Variables = new JsonObject(),
        Attempt = 1,
        Time = TimeProvider.System,
    };

    /// <summary>Подготовка, затем действие — с тем же снимком, что сохранил бы движок.</summary>
    protected static async Task<(NodeResult Result, NodeExecutionContext Context)> Run(INodeExecutor node, object input)
    {
        var context = Context(input);
        var checkpoint = await node.PrepareAsync(context, CancellationToken.None);
        context = context with { Checkpoint = checkpoint };
        return (await node.ExecuteAsync(context, CancellationToken.None), context);
    }

    protected static NodeResult.Success Ok(NodeResult result) =>
        Assert.IsType<NodeResult.Success>(result, exactMatch: true);

    protected async Task<JsonArray> ReadRows(string path, string? sheet = null, bool header = true)
    {
        var result = Ok(await new ReadTableNode(Workspace).ExecuteAsync(
            Context(new JsonObject { ["path"] = path, ["sheet"] = sheet, ["header"] = header, ["saveTo"] = "rows" }), CancellationToken.None));
        return result.VariableUpdates["rows"]!.AsArray();
    }

    /// <summary>Ошибки схемы Open XML: файл, который Excel открыл бы с «Обнаружено содержимое, которое не удалось прочитать».</summary>
    protected static List<string> SchemaErrors(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        return new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document)
            .Select(e => $"{e.Path?.XPath}: {e.Description}").ToList();
    }

    /// <summary>
    /// Книга, как из Excel: общие строки, формат даты и денег, формула вне таблицы, второй лист с формулой и «умная таблица»
    /// Clients на A1:D3 листа «Клиенты».
    /// </summary>
    protected void CreateBook(string relative)
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new S.Workbook();

        var styles = workbook.AddNewPart<WorkbookStylesPart>();
        styles.Stylesheet = new S.Stylesheet(
            new S.NumberingFormats(new S.NumberingFormat { NumberFormatId = 164, FormatCode = "#,##0.00\\ \"₽\"" }) { Count = 1 },
            new S.Fonts(new S.Font()) { Count = 1 },
            new S.Fills(new S.Fill(new S.PatternFill { PatternType = S.PatternValues.None }),
                new S.Fill(new S.PatternFill { PatternType = S.PatternValues.Gray125 })) { Count = 2 },
            new S.Borders(new S.Border()) { Count = 1 },
            new S.CellFormats(
                new S.CellFormat(),
                new S.CellFormat { NumberFormatId = 14, ApplyNumberFormat = true },
                new S.CellFormat { NumberFormatId = 164, ApplyNumberFormat = true }) { Count = 3 });

        var strings = new[] { "Имя", "Телефон", "Дата", "Сумма", "Анна", "Борис" };
        var shared = workbook.AddNewPart<SharedStringTablePart>();
        shared.SharedStringTable = new S.SharedStringTable(strings.Select(s => new S.SharedStringItem(new S.Text(s)))) { Count = 6, UniqueCount = 6 };

        var clients = workbook.AddNewPart<WorksheetPart>();
        var table = clients.AddNewPart<TableDefinitionPart>();
        table.Table = new S.Table(
            new S.AutoFilter { Reference = "A1:D3" },
            new S.TableColumns(strings.Take(4).Select((name, i) => new S.TableColumn { Id = (uint)(i + 1), Name = name })) { Count = 4 },
            new S.TableStyleInfo { Name = "TableStyleMedium2", ShowRowStripes = true })
        {
            Id = 1, Name = "Clients", DisplayName = "Clients", Reference = "A1:D3",
        };
        clients.Worksheet = new S.Worksheet(
            new S.SheetDimension { Reference = "A1:F3" },
            new S.SheetData(
                new S.Row(Shared("A1", 0), Shared("B1", 1), Shared("C1", 2), Shared("D1", 3)) { RowIndex = 1, Spans = new ListValue<StringValue>(["1:4"]) },
                new S.Row(Shared("A2", 4), Number("B2", "79161234567"), Number("C2", "45000", 1), Number("D2", "100.5", 2),
                    new S.Cell(new S.CellFormula("D2*2"), new S.CellValue("201")) { CellReference = "F2" }) { RowIndex = 2 },
                new S.Row(Shared("A3", 5), Inline("B3", "8 916 000-00-00"), Number("C3", "45000.5", 1), Number("D3", "99", 2)) { RowIndex = 3 }),
            new S.TableParts(new S.TablePart { Id = clients.GetIdOfPart(table) }) { Count = 1 });

        var totals = workbook.AddNewPart<WorksheetPart>();
        totals.Worksheet = new S.Worksheet(new S.SheetData(
            new S.Row(new S.Cell(new S.CellFormula("SUM(Клиенты!D:D)"), new S.CellValue("199.5")) { CellReference = "A1" }) { RowIndex = 1 }));

        workbook.Workbook.AppendChild(new S.Sheets(
            new S.Sheet { Id = workbook.GetIdOfPart(clients), SheetId = 1, Name = "Клиенты" },
            new S.Sheet { Id = workbook.GetIdOfPart(totals), SheetId = 2, Name = "Итоги" }));
    }

    private static S.Cell Shared(string reference, int index) =>
        new(new S.CellValue(index.ToString(System.Globalization.CultureInfo.InvariantCulture))) { CellReference = reference, DataType = S.CellValues.SharedString };

    private static S.Cell Number(string reference, string value, uint style = 0) =>
        new(new S.CellValue(value)) { CellReference = reference, StyleIndex = style == 0 ? null : style };

    private static S.Cell Inline(string reference, string value) =>
        new(new S.InlineString(new S.Text(value))) { CellReference = reference, DataType = S.CellValues.InlineString };

    /// <summary>Ячейка листа по адресу (A1); null — её нет.</summary>
    protected static S.Cell? CellOf(string path, string sheet, string reference)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbook = document.WorkbookPart!;
        var id = workbook.Workbook!.Sheets!.Elements<S.Sheet>().First(s => s.Name == sheet).Id!.Value!;
        var part = (WorksheetPart)workbook.GetPartById(id);
        return (S.Cell?)part.Worksheet!.Descendants<S.Cell>().FirstOrDefault(c => c.CellReference == reference)?.CloneNode(true);
    }
}
