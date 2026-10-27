using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core.Nodes;

namespace Pepa.Adapters.Tables;

/// <summary>
/// Как записан CSV-файл: кодировка (с BOM или без), разделитель, перевод строки и знак дробной части. Изменённый файл
/// остаётся таким же: Excel и другие программы откроют его как раньше.
/// </summary>
internal sealed record CsvDialect(Encoding Encoding, byte[] Preamble, char Delimiter, string NewLine)
{
    /// <summary>Новый файл — для Excel с русскими настройками: UTF-8 с BOM, «;», дробная часть через запятую.</summary>
    public static readonly CsvDialect New = new(new UTF8Encoding(false, true), [0xEF, 0xBB, 0xBF], ';', "\r\n");

    /// <summary>С разделителем «;» (Excel с русскими настройками) дробная часть пишется через запятую.</summary>
    public bool DecimalComma => Delimiter == ';';

    public string EncodingName => Encoding.CodePage switch
    {
        1251 => "Windows-1251",
        1200 => "UTF-16",
        1201 => "UTF-16 BE",
        _ => Preamble.Length > 0 ? "UTF-8 с BOM" : "UTF-8",
    };

    /// <summary>Текст ячейки, как он запишется и прочитается: число — с дробной частью этого файла, да/нет — словами.</summary>
    public JsonNode? Stored(JsonNode? value)
    {
        if (TableValues.IsEmpty(value))
        {
            return null;
        }

        var text = value!.GetValueKind() switch
        {
            JsonValueKind.Number => DecimalComma ? TableValues.Text(value).Replace('.', ',') : TableValues.Text(value),
            JsonValueKind.String => Guard(value.GetValue<string>()),
            _ => TableValues.Text(value),
        };
        return JsonValue.Create(text);
    }

    /// <summary>
    /// Текст, который Excel принял бы за формулу («=», «+», «-», «@» в начале, кроме обычного числа вроде «-5»), пишется с
    /// апострофом впереди: данные с сайта или из сообщения не должны стать формулой при открытии файла.
    /// </summary>
    public static string Guard(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' && !TableValues.IsPlainNumber(text.Trim()) ? "'" + text : text;
}

/// <summary>CSV-файл, прочитанный для изменения: текст и границы каждой строки и поля в нём.</summary>
internal sealed class CsvDocument
{
    private readonly List<CsvRecord> _records;

    private CsvDocument(string text, List<CsvRecord> records, CsvDialect dialect)
    {
        Text = text;
        _records = records;
        Dialect = dialect;
    }

    public string Text { get; }
    public CsvDialect Dialect { get; }

    private sealed record CsvField(int Start, int End, string Value);

    /// <summary>Строка файла: границы без перевода строки и поля.</summary>
    private sealed record CsvRecord(int Start, int End, List<CsvField> Fields);

    public static CsvDocument Parse(byte[] bytes, string extension)
    {
        var (encoding, preamble) = DetectEncoding(bytes);
        string text;
        try
        {
            text = encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length);
        }
        catch (DecoderFallbackException)
        {
            throw new NodeInputException("Файл CSV не читается ни как UTF-8, ни как Windows-1251.");
        }

        var newLine = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n') ? "\r\n" : "\n";
        var delimiter = extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : DetectDelimiter(text);
        var dialect = new CsvDialect(encoding, preamble, delimiter, newLine);
        return new CsvDocument(text, Split(text, delimiter), dialect);
    }

    public static CsvDocument Empty(CsvDialect dialect) => new("", [], dialect);

    public TableSheet ToSheet()
    {
        var sheet = new TableSheet { Name = "", Csv = Dialect };
        for (var i = 0; i < _records.Count; i++)
        {
            var fields = _records[i].Fields;
            for (var j = 0; j < fields.Count; j++)
            {
                sheet.Set(i + 1, j + 1, fields[j].Value.Length > 0 ? JsonValue.Create(fields[j].Value) : null);
            }
        }

        return sheet;
    }

    /// <summary>
    /// Файл с изменёнными ячейками (строка → колонка → значение, null — очистить). Нетронутые строки и поля остаются
    /// байт в байт как были; новые строки дописываются в конец.
    /// </summary>
    public byte[] With(IReadOnlyDictionary<int, IReadOnlyDictionary<int, JsonNode?>> changes)
    {
        var text = new StringBuilder(Text);
        // С конца файла к началу: правка строки не сдвигает границы строк выше.
        foreach (var (row, cells) in changes.Where(c => c.Key <= _records.Count).OrderByDescending(c => c.Key))
        {
            var record = _records[row - 1];
            text.Remove(record.Start, record.End - record.Start).Insert(record.Start, Rebuild(record, cells));
        }

        var appended = changes.Where(c => c.Key > _records.Count).OrderBy(c => c.Key).ToList();
        if (appended.Count > 0)
        {
            if (text.Length > 0 && text[^1] is not ('\n' or '\r'))
            {
                text.Append(Dialect.NewLine);
            }

            var next = _records.Count + 1;
            foreach (var (row, cells) in appended)
            {
                for (; next < row; next++)
                {
                    text.Append(Dialect.NewLine);
                }

                text.Append(Rebuild(null, cells)).Append(Dialect.NewLine);
                next++;
            }
        }

        var result = text.ToString();
        try
        {
            return [.. Dialect.Preamble, .. Dialect.Encoding.GetBytes(result)];
        }
        catch (EncoderFallbackException ex)
        {
            throw new NodeInputException(
                $"Знак «{ex.CharUnknown}» нельзя записать в этот CSV-файл: он в кодировке {Dialect.EncodingName}. " +
                "Сохраните таблицу как «CSV UTF-8» или .xlsx.");
        }
    }

    private string Rebuild(CsvRecord? record, IReadOnlyDictionary<int, JsonNode?> cells)
    {
        var count = Math.Max(record?.Fields.Count ?? 0, cells.Keys.Max());
        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = cells.TryGetValue(i + 1, out var value)
                ? Quote(TableValues.Text(Dialect.Stored(value)))
                : record is not null && i < record.Fields.Count
                    ? Text[record.Fields[i].Start..record.Fields[i].End]
                    : "";
        }

        return string.Join(Dialect.Delimiter, parts);
    }

    private string Quote(string value) =>
        value.IndexOfAny([Dialect.Delimiter, '"', '\r', '\n']) >= 0 || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static (Encoding Encoding, byte[] Preamble) DetectEncoding(byte[] bytes)
    {
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            return (new UTF8Encoding(false, true), [0xEF, 0xBB, 0xBF]);
        }

        if (bytes is [0xFF, 0xFE, ..])
        {
            return (new UnicodeEncoding(false, false, true), [0xFF, 0xFE]);
        }

        if (bytes is [0xFE, 0xFF, ..])
        {
            return (new UnicodeEncoding(true, false, true), [0xFE, 0xFF]);
        }

        var utf8 = new UTF8Encoding(false, true);
        try
        {
            utf8.GetString(bytes);
            return (utf8, []);
        }
        catch (DecoderFallbackException)
        {
            // Excel с русскими настройками сохраняет «CSV (разделители — запятые)» в Windows-1251.
            return (Windows1251.Value, []);
        }
    }

    private static readonly Lazy<Encoding> Windows1251 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    });

    /// <summary>Разделитель — тот из «;», «,», табуляции, которого больше в первой непустой строке (вне кавычек).</summary>
    private static char DetectDelimiter(string text)
    {
        var line = text.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        var counts = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted && counts.ContainsKey(c))
            {
                counts[c]++;
            }
        }

        var best = counts.MaxBy(c => c.Value);
        return best.Value > 0 && best.Value > counts[';'] ? best.Key : ';';
    }

    private static List<CsvRecord> Split(string text, char delimiter)
    {
        var records = new List<CsvRecord>();
        var i = 0;
        while (i < text.Length)
        {
            var recordStart = i;
            var fields = new List<CsvField>();
            while (true)
            {
                var fieldStart = i;
                string value;
                if (i < text.Length && text[i] == '"')
                {
                    var builder = new StringBuilder();
                    i++;
                    while (i < text.Length)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                builder.Append('"');
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        builder.Append(text[i++]);
                    }

                    // Нестрогий CSV: знаки после закрывающей кавычки — часть значения.
                    while (i < text.Length && text[i] != delimiter && text[i] is not ('\r' or '\n'))
                    {
                        builder.Append(text[i++]);
                    }

                    value = builder.ToString();
                }
                else
                {
                    while (i < text.Length && text[i] != delimiter && text[i] is not ('\r' or '\n'))
                    {
                        i++;
                    }

                    value = text[fieldStart..i];
                }

                fields.Add(new CsvField(fieldStart, i, value));
                if (i < text.Length && text[i] == delimiter)
                {
                    i++;
                    continue;
                }

                break;
            }

            var recordEnd = i;
            if (i < text.Length && text[i] == '\r')
            {
                i++;
            }

            if (i < text.Length && text[i] == '\n')
            {
                i++;
            }

            records.Add(new CsvRecord(recordStart, recordEnd, fields));
        }

        return records;
    }
}
