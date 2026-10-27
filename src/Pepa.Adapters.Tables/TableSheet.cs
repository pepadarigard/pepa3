using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Tables;

/// <summary>
/// Лист таблицы как он есть в файле: непустые ячейки по номерам строк и колонок (с 1, как в Excel). Значение ячейки —
/// текст, число или да/нет; дата из Excel — текст ISO («2026-10-27», «2026-10-27T10:30:00»).
/// </summary>
internal sealed class TableSheet
{
    public required string Name { get; init; }

    public SortedDictionary<int, SortedDictionary<int, JsonNode>> Rows { get; } = [];

    /// <summary>Как записывать значения в этот файл (для CSV — разделитель, кодировка, дробная часть).</summary>
    public CsvDialect? Csv { get; init; }

    /// <summary>Номер последней строки, где есть хоть одно значение; 0 — лист пуст.</summary>
    public int LastRow => Rows.Count == 0 ? 0 : Rows.Keys.Last();

    public JsonNode? Cell(int row, int column) =>
        Rows.TryGetValue(row, out var cells) && cells.TryGetValue(column, out var value) ? value : null;

    public void Set(int row, int column, JsonNode? value)
    {
        if (TableValues.IsEmpty(value))
        {
            if (Rows.TryGetValue(row, out var existing) && existing.Remove(column) && existing.Count == 0)
            {
                Rows.Remove(row);
            }

            return;
        }

        if (!Rows.TryGetValue(row, out var cells))
        {
            Rows[row] = cells = [];
        }

        cells[column] = value!;
    }

    /// <summary>Как значение прочитается из этого файла после записи (CSV хранит только текст).</summary>
    public JsonNode? Stored(JsonNode? value) => Csv is { } csv ? csv.Stored(value) : XlsxFormat.Stored(value);
}

/// <summary>Колонка таблицы: название (из строки заголовков или буква) и номер.</summary>
internal sealed record TableColumn(string Name, int Index);

/// <summary>
/// Разметка листа: строка заголовков (0 — без заголовков), колонки и строки с данными. Названия колонок — из первой
/// непустой строки; пустое название — буква колонки, повторное — с номером: «Имя (2)».
/// </summary>
internal sealed class TableLayout
{
    private TableLayout(TableSheet sheet, int headerRow, List<TableColumn> columns, List<int> dataRows)
    {
        Sheet = sheet;
        HeaderRow = headerRow;
        Columns = columns;
        DataRows = dataRows;
    }

    public TableSheet Sheet { get; }
    public int HeaderRow { get; }
    public IReadOnlyList<TableColumn> Columns { get; }
    public IReadOnlyList<int> DataRows { get; }

    public static TableLayout Of(TableSheet sheet, bool header)
    {
        var headerRow = header && sheet.Rows.Count > 0 ? sheet.Rows.Keys.First() : 0;
        var columns = new List<TableColumn>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(int index, string name)
        {
            var unique = name;
            for (var n = 2; !names.Add(unique); n++)
            {
                unique = $"{name} ({n})";
            }

            columns.Add(new TableColumn(unique, index));
        }

        if (headerRow > 0)
        {
            foreach (var (index, value) in sheet.Rows[headerRow])
            {
                var name = TableValues.Text(value).Trim();
                Add(index, name.Length > 0 ? name : TableValues.Letter(index));
            }
        }

        // Значения в колонках без названия не теряются: такая колонка называется буквой.
        var known = columns.Select(c => c.Index).ToHashSet();
        foreach (var index in sheet.Rows.Where(r => r.Key != headerRow).SelectMany(r => r.Value.Keys).Distinct().Order())
        {
            if (known.Add(index))
            {
                Add(index, TableValues.Letter(index));
            }
        }

        columns.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new TableLayout(sheet, headerRow, columns, sheet.Rows.Keys.Where(r => r != headerRow).ToList());
    }

    public JsonObject RowObject(int row)
    {
        var result = new JsonObject();
        foreach (var column in Columns)
        {
            result[column.Name] = Sheet.Cell(row, column.Index)?.DeepClone();
        }

        return result;
    }

    /// <summary>Колонка по названию (при заголовках) или по букве (без них); null — такой нет.</summary>
    public TableColumn? Find(string name)
    {
        var trimmed = name.Trim();
        if (HeaderRow == 0)
        {
            return TableValues.ColumnIndex(trimmed) is { } index
                ? Columns.FirstOrDefault(c => c.Index == index) ?? new TableColumn(TableValues.Letter(index), index)
                : null;
        }

        return Columns.FirstOrDefault(c => c.Name == trimmed) ??
               Columns.FirstOrDefault(c => string.Equals(c.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public string ColumnList() => Columns.Count == 0
        ? "в таблице пока нет колонок"
        : "есть: " + string.Join(", ", Columns.Take(30).Select(c => $"«{c.Name}»")) + (Columns.Count > 30 ? " …" : "");

    /// <summary>
    /// Ячейки, которые нужно записать: колонка → значение. Значения — объект «колонка: значение» или список (по порядку
    /// колонок). Неизвестная колонка — ошибка: строка не должна записаться не туда.
    /// </summary>
    public IReadOnlyList<(TableColumn Column, JsonNode? Value)> Resolve(JsonNode? values)
    {
        var result = new List<(TableColumn, JsonNode?)>();
        switch (values)
        {
            case JsonObject map when map.Count > 0:
                foreach (var (key, value) in map)
                {
                    var column = Find(key) ?? throw new NodeInputException(HeaderRow == 0
                        ? $"«{key}» — не буква колонки (A, B, … — у таблицы без заголовков)."
                        : $"В таблице нет колонки «{key}»: {ColumnList()}.");
                    if (result.Any(r => r.Item1.Index == column.Index))
                    {
                        throw new NodeInputException($"Колонка «{column.Name}» указана дважды.");
                    }

                    result.Add((column, TableValues.Check(column.Name, value)));
                }

                break;
            case JsonArray list when list.Count > 0:
                if (HeaderRow > 0 && list.Count > Columns.Count)
                {
                    throw new NodeInputException($"Значений {list.Count}, а колонок в таблице {Columns.Count}: {ColumnList()}.");
                }

                for (var i = 0; i < list.Count; i++)
                {
                    var column = HeaderRow > 0 ? Columns[i] : new TableColumn(TableValues.Letter(i + 1), i + 1);
                    result.Add((column, TableValues.Check(column.Name, list[i])));
                }

                break;
            default:
                throw new NodeInputException(
                    "Значения — объект «колонка: значение», например ({ Имя: vars.name, Статус: 'готово' }), или список.");
        }

        return result;
    }

    /// <summary>Строки, где в колонке — искомое значение (без учёта регистра и пробелов по краям; 91,5 = 91.5).</summary>
    public List<int> Match(TableColumn column, string value) =>
        DataRows.Where(row => TableValues.Same(Sheet.Cell(row, column.Index), value)).ToList();
}

internal static partial class TableValues
{
    /// <summary>Больше строк за раз не читается: список ушёл бы в переменную процесса целиком.</summary>
    public const int MaxRows = 10_000;

    public const int MaxColumn = 16_384;
    public const int MaxRow = 1_048_576;

    public static bool IsEmpty(JsonNode? value) =>
        value is null || (value is JsonValue v && v.TryGetValue<string>(out var text) && string.IsNullOrWhiteSpace(text));

    public static string Text(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        // Число — как в JSON (без потери знаков у длинных целых): 91.5, 100, 79161234567.
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValue<bool>() ? "да" : "нет",
        _ => value.ToJsonString(),
    };

    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Число из значения JSON; null — не конечное число.</summary>
    public static double? AsDouble(JsonValue value) =>
        double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : null;

    /// <summary>Значение для ячейки: текст, число, да/нет или пусто. Объект или список в ячейку не помещается.</summary>
    public static JsonNode? Check(string column, JsonNode? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonValue v when v.GetValueKind() is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                if (v.GetValueKind() == JsonValueKind.Number && AsDouble(v) is null)
                {
                    throw new NodeInputException($"Значение колонки «{column}» — не число.");
                }

                if (v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is var text)
                {
                    if (text.Length > 32_767)
                    {
                        throw new NodeInputException($"Значение колонки «{column}» длиннее 32 767 знаков — столько в ячейку не помещается.");
                    }

                    if (text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')) || HasBrokenSurrogate(text))
                    {
                        throw new NodeInputException($"В значении колонки «{column}» есть служебные знаки, которые нельзя записать в таблицу.");
                    }
                }

                return v.DeepClone();
            default:
                throw new NodeInputException($"Значение колонки «{column}» — {(value is JsonArray ? "список" : "объект")}, " +
                                             "а в ячейку помещается текст, число или да/нет.");
        }
    }

    private static bool HasBrokenSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Одинаковы ли значения ячеек: без учёта регистра и пробелов по краям, числа — по значению.</summary>
    public static bool Same(JsonNode? cell, JsonNode? expected)
    {
        if (IsEmpty(cell) || IsEmpty(expected))
        {
            return IsEmpty(cell) && IsEmpty(expected);
        }

        return Same(cell, Text(expected));
    }

    public static bool Same(JsonNode? cell, string expected)
    {
        var text = Text(cell).Trim();
        var wanted = expected.Trim();
        if (string.Equals(text, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ParseNumber(text) is { } a && ParseNumber(wanted) is { } b && a == b;
    }

    private static double? ParseNumber(string text) =>
        PlainNumber().IsMatch(text) && double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    [GeneratedRegex(@"^[+-]?[0-9]+([.,][0-9]+)?$")]
    private static partial Regex PlainNumber();

    public static bool IsPlainNumber(string text) => PlainNumber().IsMatch(text);

    /// <summary>Буква колонки: 1 → A, 27 → AA.</summary>
    public static string Letter(int index)
    {
        var result = "";
        for (var n = index; n > 0; n = (n - 1) / 26)
        {
            result = (char)('A' + (n - 1) % 26) + result;
        }

        return result;
    }

    /// <summary>Номер колонки по букве (A, AA, …, XFD); null — не буква колонки.</summary>
    public static int? ColumnIndex(string letters)
    {
        if (letters.Length is 0 or > 3)
        {
            return null;
        }

        var index = 0;
        foreach (var c in letters.ToUpperInvariant())
        {
            if (c is < 'A' or > 'Z')
            {
                return null;
            }

            index = index * 26 + (c - 'A' + 1);
        }

        return index <= MaxColumn ? index : null;
    }
}
