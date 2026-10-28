using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
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

    /// <summary>Число Excel под датой или длительностью (ячейка показана текстом): по нему сверяется записанное число.</summary>
    public Dictionary<(int Row, int Column), double> Serials { get; } = [];

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

    /// <summary>В ячейке ровно то, что запишет Pepa (<see cref="Stored"/>): текст — как есть, число — по значению.</summary>
    public bool Holds(int row, int column, JsonNode? value) =>
        TableValues.Holds(Cell(row, column), Stored(value), Serials.TryGetValue((row, column), out var serial) ? serial : null);
}

/// <summary>
/// Колонка таблицы: имя в строке данных (название из строки заголовков, буква колонки или «название (буква)» у
/// повторяющегося названия), номер и само название из заголовка (null — его нет).
/// </summary>
internal sealed record TableColumn(string Name, int Index, string? Header = null);

/// <summary>
/// Разметка листа: строка заголовков (0 — без заголовков), колонки и строки с данными. Названия колонок — из первой
/// непустой строки; колонка без названия называется буквой; повторяющееся название — неоднозначно: обратиться к такой
/// колонке по названию нельзя (только «Имя (B)»).
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
        var headers = new Dictionary<int, string>();
        if (headerRow > 0)
        {
            foreach (var (index, value) in sheet.Rows[headerRow])
            {
                if (TableValues.Text(value).Trim() is { Length: > 0 } name)
                {
                    headers[index] = name;
                }
            }
        }

        // Значения в колонках без названия не теряются: такая колонка называется буквой.
        var indexes = headers.Keys
            .Concat(sheet.Rows.Where(r => r.Key != headerRow).SelectMany(r => r.Value.Keys))
            .Distinct().Order().ToList();
        var counts = headers.Values.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(headers.Values, StringComparer.OrdinalIgnoreCase);
        var columns = new List<TableColumn>();
        foreach (var index in indexes)
        {
            var letter = TableValues.Letter(index);
            if (headers.TryGetValue(index, out var name) && counts[name] == 1)
            {
                columns.Add(new TableColumn(name, index, name));
                continue;
            }

            var unique = name is null ? letter : $"{name} ({letter})";
            for (var n = 2; name is null ? taken.Contains(unique) : !taken.Add(unique); n++)
            {
                unique = name is null ? $"{letter} ({n})" : $"{name} ({letter}, {n})";
            }

            if (name is null)
            {
                taken.Add(unique);
            }

            columns.Add(new TableColumn(unique, index, name));
        }

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

    /// <summary>
    /// Колонка по названию (при заголовках) или по букве (без них); null — такой нет. Название, которое в заголовке
    /// встречается несколько раз (без учёта регистра), — ошибка: Pepa не выбирает одну из них сама.
    /// </summary>
    public TableColumn? Find(string name)
    {
        var trimmed = name.Trim();
        if (HeaderRow == 0)
        {
            return TableValues.ColumnIndex(trimmed) is { } index
                ? Columns.FirstOrDefault(c => c.Index == index) ?? new TableColumn(TableValues.Letter(index), index)
                : null;
        }

        var byHeader = Columns.Where(c => string.Equals(c.Header, trimmed, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byHeader.Count > 1)
        {
            throw new NodeInputException(
                $"В таблице несколько колонок «{trimmed}» ({string.Join(", ", byHeader.Select(c => TableValues.Letter(c.Index)))}) — " +
                $"какая нужна, неясно. Переименуйте их или укажите {string.Join(" или ", byHeader.Select(c => $"«{c.Name}»"))}.");
        }

        return byHeader.FirstOrDefault() ??
               Columns.FirstOrDefault(c => c.Name == trimmed) ??
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

    /// <summary>Строки, где в колонке — искомое значение (см. <see cref="TableValues.Matches"/>).</summary>
    public List<int> Match(TableColumn column, string value) =>
        DataRows.Where(row => TableValues.Matches(Sheet.Cell(row, column.Index), value)).ToList();
}

internal static partial class TableValues
{
    /// <summary>Больше строк за раз не читается: список ушёл бы в переменную процесса целиком.</summary>
    public const int MaxRows = 10_000;

    public const int MaxColumn = 16_384;
    public const int MaxRow = 1_048_576;

    /// <summary>Больше значащих цифр число double хранит неточно: такие числа сравниваются только как текст.</summary>
    private const int MaxExactDigits = 15;

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

                    if (!IsWritable(text))
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

    /// <summary>Только знаки, допустимые в XML (как в файле Excel): без служебных, кроме табуляции и перевода строки.</summary>
    private static bool IsWritable(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]) || !XmlConvert.IsXmlChar(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Подходит ли ячейка к искомому значению (поиск строки): без учёта регистра и пробелов по краям; число в ячейке
    /// Excel — по значению; дробная часть — через запятую или точку. Номера с нулями впереди и длинные номера (счёт,
    /// карта) сравниваются только как текст: «007» ≠ «7», счета, отличающиеся последней цифрой, — разные. Пустое значение
    /// не подходит ни к чему.
    /// </summary>
    public static bool Matches(JsonNode? cell, string wanted)
    {
        var key = wanted.Trim();
        if (key.Length == 0)
        {
            return false;
        }

        var text = Text(cell).Trim();
        if (string.Equals(text, key, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (cell is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
        {
            return ExactNumber(key) is { } number && AsDouble(value) == number;
        }

        return IsPlainNumber(text) && IsPlainNumber(key) && text.Replace(',', '.') == key.Replace(',', '.');
    }

    /// <summary>
    /// В ячейке ровно записанное значение (<paramref name="stored"/> — как его прочитает Pepa): текст — точно, с учётом
    /// регистра; число — по значению (у ячейки с датой — по её числу <paramref name="serial"/>); да/нет — как есть.
    /// </summary>
    public static bool Holds(JsonNode? cell, JsonNode? stored, double? serial = null)
    {
        if (IsEmpty(stored))
        {
            return IsEmpty(cell);
        }

        var expected = (JsonValue)stored!;
        switch (expected.GetValueKind())
        {
            case JsonValueKind.Number:
                var number = AsDouble(expected);
                return serial is { } raw
                    ? raw == number
                    : cell is JsonValue actual && actual.GetValueKind() == JsonValueKind.Number && AsDouble(actual) == number;
            case JsonValueKind.True or JsonValueKind.False:
                return cell is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False &&
                       flag.GetValue<bool>() == expected.GetValue<bool>();
            default:
                return cell is JsonValue text && text.TryGetValue<string>(out var value) && value == expected.GetValue<string>();
        }
    }

    /// <summary>Число, которое можно сравнивать по значению: без нулей впереди и не длиннее 15 значащих цифр.</summary>
    private static double? ExactNumber(string text)
    {
        var normalized = text.Replace(',', '.');
        if (!ExactNumberPattern().IsMatch(normalized))
        {
            return null;
        }

        var mantissa = normalized.Split('e', 'E')[0].TrimStart('+', '-').Replace(".", "").TrimStart('0');
        return mantissa.Length <= MaxExactDigits &&
               double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : null;
    }

    [GeneratedRegex(@"^[+-]?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex ExactNumberPattern();

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
