using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Tables;

/// <summary>
/// Таблицы Excel (.xlsx, .xlsm) через Open XML SDK. Изменение касается только ячеек одного листа: оформление,
/// формулы, диаграммы, другие листы и макросы остаются как были (файл не пересобирается заново).
/// </summary>
internal static partial class XlsxFormat
{
    /// <summary>Целое длиннее 15 цифр Excel хранит неточно (номер карты, длинный код) — оно пишется текстом.</summary>
    private const int MaxExactDigits = 15;

    public static JsonNode? Stored(JsonNode? value)
    {
        if (TableValues.IsEmpty(value))
        {
            return null;
        }

        if (value!.GetValueKind() == JsonValueKind.Number && TableValues.Text(value) is var digits &&
            digits.TrimStart('-').All(char.IsAsciiDigit) && digits.TrimStart('-').Length > MaxExactDigits)
        {
            return JsonValue.Create(digits);
        }

        return value.DeepClone();
    }

    public static string NewSheetName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            return "Лист1";
        }

        if (trimmed.Length > 31 || trimmed.IndexOfAny([':', '\\', '/', '?', '*', '[', ']']) >= 0 || trimmed.StartsWith('\'') || trimmed.EndsWith('\''))
        {
            throw new NodeInputException($"«{trimmed}» не годится как имя листа Excel: до 31 знака, без : \\ / ? * [ ] и апострофа по краям.");
        }

        return trimmed;
    }

    public static TableSheet Read(byte[] bytes, string? sheetName, string relative)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var document = OpenDocument(stream, editable: false, relative);
        var (workbook, sheet, part) = FindSheet(document, sheetName, relative);
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(ItemText).ToArray() ?? [];
        var stylesheet = workbook.WorkbookStylesPart?.Stylesheet;
        var formats = stylesheet?.CellFormats?.Elements<CellFormat>().Select(f => f.NumberFormatId?.Value ?? 0).ToArray() ?? [];
        var custom = new Dictionary<uint, string>();
        foreach (var format in stylesheet?.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } id)
            {
                custom.TryAdd(id, format.FormatCode?.Value ?? "");
            }
        }

        var date1904 = workbook.Workbook?.WorkbookProperties?.Date1904?.Value == true;
        var result = new TableSheet { Name = sheet.Name?.Value ?? "" };
        var rowIndex = 0;
        foreach (var row in part.Worksheet?.GetFirstChild<SheetData>()?.Elements<Row>() ?? [])
        {
            rowIndex = row.RowIndex?.Value is { } r ? (int)r : rowIndex + 1;
            var column = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                column = Column(cell) ?? column + 1;
                var style = cell.StyleIndex?.Value is { } s && s < formats.Length ? formats[s] : 0u;
                result.Set(rowIndex, column, Value(cell, shared, IsDate(style, custom), date1904));
            }
        }

        return result;
    }

    /// <summary>
    /// Книга с изменёнными ячейками. <paramref name="original"/> null — новая книга с одним листом. Новая ячейка под
    /// строкой данных берёт её оформление (формат даты, границы), под заголовком — нет. Заменить формулу нельзя.
    /// </summary>
    public static byte[] Write(byte[]? original, string? sheetName, IReadOnlyDictionary<int, IReadOnlyDictionary<int, JsonNode?>> changes,
        int headerRow, string relative)
    {
        using var stream = new MemoryStream();
        if (original is null)
        {
            CreateBook(stream, NewSheetName(sheetName));
        }
        else
        {
            stream.Write(original);
        }

        stream.Position = 0;
        using (var document = OpenDocument(stream, editable: true, relative))
        {
            var (workbook, _, part) = FindSheet(document, sheetName, relative);
            var worksheet = part.Worksheet ?? throw Broken(relative);
            var data = worksheet.GetFirstChild<SheetData>() ?? throw Broken(relative);
            NumberRowsAndCells(data);
            foreach (var (row, cells) in changes.OrderBy(c => c.Key))
            {
                if (row is < 1 or > TableValues.MaxRow)
                {
                    throw new NodeInputException($"В листе Excel не больше {TableValues.MaxRow} строк.");
                }

                var rowElement = RowAt(data, (uint)row);
                rowElement.Spans = null;
                foreach (var (column, value) in cells.OrderBy(c => c.Key))
                {
                    var cell = CellAt(rowElement, column, out var created);
                    if (cell.CellFormula is not null)
                    {
                        throw new NodeInputException($"В ячейке {cell.CellReference} таблицы «{relative}» формула — Pepa не заменяет формулы.");
                    }

                    if (created && row - 1 > headerRow && StyleAbove(data, row, column) is { } style)
                    {
                        cell.StyleIndex = style;
                    }

                    SetValue(cell, Stored(value));
                }
            }

            UpdateDimension(worksheet, data);
            ExtendTables(part, changes.Keys);
            // Формулы, которые считают по этим ячейкам (итог, счётчик), Excel пересчитает при открытии.
            var book = workbook.Workbook ?? throw Broken(relative);
            book.CalculationProperties ??= new CalculationProperties();
            book.CalculationProperties.FullCalculationOnLoad = true;
        }

        return stream.ToArray();
    }

    private static SpreadsheetDocument OpenDocument(Stream stream, bool editable, string relative)
    {
        try
        {
            return SpreadsheetDocument.Open(stream, editable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new NodeInputException($"«{relative}» не открывается как таблица Excel: файл повреждён, защищён паролем или это другой формат.");
        }
    }

    private static NodeInputException Broken(string relative) => new($"Таблица «{relative}» повреждена: в ней нет данных листа.");

    private static (WorkbookPart Workbook, Sheet Sheet, WorksheetPart Part) FindSheet(SpreadsheetDocument document, string? name, string relative)
    {
        var workbook = document.WorkbookPart ?? throw Broken(relative);
        var sheets = workbook.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? [];
        var wanted = name?.Trim() ?? "";
        var sheet = wanted.Length == 0
            ? sheets.FirstOrDefault(s => s.State is null || s.State.Value == SheetStateValues.Visible) ?? sheets.FirstOrDefault()
            : sheets.FirstOrDefault(s => s.Name?.Value == wanted) ??
              sheets.FirstOrDefault(s => string.Equals(s.Name?.Value, wanted, StringComparison.OrdinalIgnoreCase));
        if (sheet is null)
        {
            throw new NodeInputException(sheets.Count == 0
                ? $"В таблице «{relative}» нет листов."
                : $"В таблице «{relative}» нет листа «{wanted}». Листы: {string.Join(", ", sheets.Select(s => $"«{s.Name?.Value}»"))}.");
        }

        if (sheet.Id?.Value is not { } id || workbook.GetPartById(id) is not WorksheetPart part)
        {
            throw new NodeInputException($"Лист «{sheet.Name?.Value}» в «{relative}» — не таблица (например, диаграмма).");
        }

        return (workbook, sheet, part);
    }

    private static void CreateBook(Stream stream, string sheetName)
    {
        using var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new Workbook();
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        worksheet.Worksheet = new Worksheet(new SheetData());
        workbook.Workbook.AppendChild(new Sheets(new Sheet { Id = workbook.GetIdOfPart(worksheet), SheetId = 1, Name = sheetName }));
    }

    private static string ItemText(SharedStringItem item) =>
        item.Text?.Text ?? string.Concat(item.Elements<Run>().Select(r => r.Text?.Text));

    private static JsonNode? Value(Cell cell, string[] shared, bool date, bool date1904)
    {
        var type = cell.DataType?.Value;
        if (type == CellValues.InlineString)
        {
            return Text(cell.InlineString is { } inline ? inline.Text?.Text ?? string.Concat(inline.Elements<Run>().Select(r => r.Text?.Text)) : null);
        }

        var raw = cell.CellValue?.Text;
        if (raw is null)
        {
            return null;
        }

        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Length
                ? Text(shared[index])
                : null;
        }

        if (type == CellValues.Boolean)
        {
            return JsonValue.Create(raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase));
        }

        if (type == CellValues.String || type == CellValues.Error || type == CellValues.Date)
        {
            return Text(raw);
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
        {
            return Text(raw);
        }

        if (date && Date(number, date1904) is { } text)
        {
            return JsonValue.Create(text);
        }

        return Math.Abs(number) < 9_007_199_254_740_992d && number == Math.Floor(number)
            ? JsonValue.Create((long)number)
            : JsonValue.Create(number);
    }

    private static JsonValue? Text(string? text) => string.IsNullOrEmpty(text) ? null : JsonValue.Create(text);

    /// <summary>Дата Excel (число дней) — текст ISO: дата, дата и время или только время.</summary>
    private static string? Date(double serial, bool date1904)
    {
        var days = date1904 ? serial + 1462 : serial;
        if (days is < 0 or > 2_958_465)
        {
            return null;
        }

        var value = DateTime.FromOADate(days);
        value = new DateTime((value.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
        if (serial < 1)
        {
            return value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>Формат ячейки — дата или время: встроенные форматы дат Excel или свой формат с д/м/г/ч/с.</summary>
    private static bool IsDate(uint format, Dictionary<uint, string> custom)
    {
        if (custom.TryGetValue(format, out var code))
        {
            return DateLetters().IsMatch(FormatNoise().Replace(code, ""));
        }

        return format is >= 14 and <= 22 or >= 27 and <= 36 or >= 45 and <= 47 or >= 50 and <= 58;
    }

    /// <summary>Текст в кавычках, экранированные знаки и [цвет/язык] — не часть формата даты.</summary>
    [GeneratedRegex("\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]")]
    private static partial Regex FormatNoise();

    [GeneratedRegex("[ymdhsYMDHS]")]
    private static partial Regex DateLetters();

    private static int? Column(Cell cell) => cell.CellReference?.Value is { } reference ? ColumnOf(reference) : null;

    private static int? ColumnOf(string reference)
    {
        var letters = new string(reference.TakeWhile(char.IsAsciiLetter).ToArray());
        return TableValues.ColumnIndex(letters);
    }

    private static int? RowOf(string reference) =>
        int.TryParse(new string(reference.SkipWhile(char.IsAsciiLetter).ToArray()), NumberStyles.None, CultureInfo.InvariantCulture, out var row)
            ? row
            : null;

    /// <summary>Строкам и ячейкам без явного номера — номер (по порядку), чтобы новые встали на свои места.</summary>
    private static void NumberRowsAndCells(SheetData data)
    {
        uint rowIndex = 0;
        foreach (var row in data.Elements<Row>())
        {
            rowIndex = row.RowIndex?.Value ?? rowIndex + 1;
            row.RowIndex ??= rowIndex;
            var column = 0;
            foreach (var cell in row.Elements<Cell>())
            {
                column = Column(cell) ?? column + 1;
                cell.CellReference ??= TableValues.Letter(column) + rowIndex.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private static Row RowAt(SheetData data, uint index)
    {
        foreach (var row in data.Elements<Row>())
        {
            if (row.RowIndex!.Value == index)
            {
                return row;
            }

            if (row.RowIndex.Value > index)
            {
                return data.InsertBefore(new Row { RowIndex = index }, row);
            }
        }

        return data.AppendChild(new Row { RowIndex = index });
    }

    private static Cell CellAt(Row row, int column, out bool created)
    {
        created = false;
        var reference = TableValues.Letter(column) + row.RowIndex!.Value.ToString(CultureInfo.InvariantCulture);
        Cell? last = null;
        foreach (var cell in row.Elements<Cell>())
        {
            var at = Column(cell);
            if (at == column)
            {
                return cell;
            }

            if (at > column)
            {
                created = true;
                return row.InsertBefore(new Cell { CellReference = reference }, cell);
            }

            last = cell;
        }

        created = true;
        var added = new Cell { CellReference = reference };
        return last is null ? row.PrependChild(added) : row.InsertAfter(added, last);
    }

    private static UInt32Value? StyleAbove(SheetData data, int row, int column)
    {
        var above = data.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value == row - 1);
        return above?.Elements<Cell>().FirstOrDefault(c => Column(c) == column)?.StyleIndex is { } style ? new UInt32Value(style.Value) : null;
    }

    private static void SetValue(Cell cell, JsonNode? value)
    {
        cell.CellValue = null;
        cell.InlineString = null;
        cell.DataType = null;
        switch (value?.GetValueKind())
        {
            case null:
                return;
            case JsonValueKind.String:
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new Text(value.GetValue<string>()) { Space = SpaceProcessingModeValues.Preserve });
                return;
            case JsonValueKind.Number:
                cell.CellValue = new CellValue(TableValues.Text(value));
                return;
            case JsonValueKind.True or JsonValueKind.False:
                cell.DataType = CellValues.Boolean;
                cell.CellValue = new CellValue(value.GetValue<bool>() ? "1" : "0");
                return;
        }
    }

    private static void UpdateDimension(Worksheet worksheet, SheetData data)
    {
        if (worksheet.SheetDimension is not { } dimension)
        {
            return;
        }

        int minRow = int.MaxValue, maxRow = 0, minColumn = int.MaxValue, maxColumn = 0;
        foreach (var row in data.Elements<Row>())
        {
            foreach (var cell in row.Elements<Cell>())
            {
                var column = Column(cell) ?? 1;
                minRow = Math.Min(minRow, (int)row.RowIndex!.Value);
                maxRow = Math.Max(maxRow, (int)row.RowIndex.Value);
                minColumn = Math.Min(minColumn, column);
                maxColumn = Math.Max(maxColumn, column);
            }
        }

        if (maxRow > 0)
        {
            var start = TableValues.Letter(minColumn) + minRow;
            var end = TableValues.Letter(maxColumn) + maxRow;
            dimension.Reference = start == end ? start : $"{start}:{end}";
        }
    }

    /// <summary>
    /// «Умная таблица» Excel, которая кончается прямо над новой строкой, растягивается на неё — как при вводе вручную
    /// (если у таблицы нет строки итогов).
    /// </summary>
    private static void ExtendTables(WorksheetPart part, IEnumerable<int> rows)
    {
        foreach (var definition in part.TableDefinitionParts)
        {
            if (definition.Table is not { Reference.Value: { } reference } table || (table.TotalsRowCount?.Value ?? 0) > 0 ||
                reference.Split(':') is not [var start, var end] || RowOf(end) is not { } endRow)
            {
                continue;
            }

            var last = endRow;
            foreach (var row in rows.Order())
            {
                if (row == last + 1)
                {
                    last = row;
                }
            }

            if (last == endRow)
            {
                continue;
            }

            var extended = $"{start}:{new string(end.TakeWhile(char.IsAsciiLetter).ToArray())}{last}";
            table.Reference = extended;
            if (table.AutoFilter is { } filter)
            {
                filter.Reference = extended;
            }
        }
    }
}
