using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

/// <summary>
/// Секретные переменные (пароли): их значения не должны попадать в историю процесса. Тексты, которые движок записывает о
/// шаге (сообщение, ошибка, пояснение, результат), проходят через замену значений на «••••» — так блокам (в том числе
/// расширениям) не нужно помнить, что им передали пароль. Параметры шага и сами переменные хранятся настоящими (по ним
/// процесс продолжается и проверяет результат), а при показе их скрывает <see cref="SecretMasker"/>.
/// <para>
/// Секретные значения путешествуют вместе с данными: процесс помнит все значения, которые были у него секретными
/// (<see cref="ProcessRecord.Secrets"/>: прежние значения секретных переменных, переданные вызвавшим процессом,
/// полученные с событиями), а событие — секреты, которые в нём есть (<see cref="EventRecord.Secrets"/>). Сами данные не
/// меняются: получатель работает с настоящими значениями, скрываются они только при показе.
/// </para>
/// </summary>
public sealed partial class ProcessEngine
{
    public const string SecretMask = "••••";

    /// <summary>Значения короче не скрываются: такие встречаются в любом тексте.</summary>
    private const int MinSecretLength = 3;

    /// <summary>Значения короче скрываются и передаются дальше только отдельным словом (ПИН-код «1234» — не в «A-12345»).</summary>
    internal const int ShortSecretLength = 6;

    private static readonly JavaScriptEncoder RelaxedEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// Значения, секретные для процесса: секретных переменных сейчас, запомненные раньше и (если передан итог шага) те, что
    /// шаг присваивает секретным переменным. Без вариантов записи — их добавляет <see cref="SecretValues"/>.
    /// </summary>
    internal static List<string> RawSecretValues(ProcessRecord process, ScenarioDefinition definition, NodeResult? result = null)
    {
        var secrets = new List<string>(process.Secrets?.All ?? []);
        var updates = (result as NodeResult.Success)?.VariableUpdates;
        foreach (var variable in definition.Variables.Where(v => v.Secret))
        {
            Collect(process.Variables[variable.Name], secrets);
            if (updates is not null && updates.TryGetValue(variable.Name, out var assigned))
            {
                Collect(assigned, secrets);
            }
        }

        return secrets.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Прежние значения секретных переменных, которые шаг заменяет: их нужно запомнить.</summary>
    internal static List<string> ReplacedSecretValues(ProcessRecord process, ScenarioDefinition definition, NodeResult result)
    {
        var replaced = new List<string>();
        if (result is NodeResult.Success { VariableUpdates: { Count: > 0 } updates })
        {
            foreach (var variable in definition.Variables.Where(v => v.Secret && updates.ContainsKey(v.Name)))
            {
                Collect(process.Variables[variable.Name], replaced);
            }
        }

        return replaced;
    }

    /// <summary>
    /// Что скрывать в текстах процесса: секретные значения и их записи в JSON (с экранированием — так их показывают
    /// предпросмотры значений). Длинные — первыми: значение, которое содержит другое, скрывается целиком.
    /// </summary>
    public static SecretList SecretValues(ProcessRecord process, ScenarioDefinition definition, NodeResult? result = null) =>
        Expand(RawSecretValues(process, definition, result));

    /// <summary>Значения и их записи в JSON (обычная и без экранирования не-ASCII), длинные — первыми.</summary>
    public static SecretList Expand(IEnumerable<string>? values)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            if (value.Length < MinSecretLength)
            {
                continue;
            }

            result.UnionWith(Forms(value));
        }

        return new SecretList(result);
    }

    /// <summary>
    /// Записи значения: как есть и без пробелов по краям (тексты часто обрезают), в JSON — с экранированием .NET (обычным и
    /// без экранирования не-ASCII) и как его записывает JSON.stringify в выражениях (экранируются только кавычка, обратная
    /// косая черта и управляющие символы).
    /// </summary>
    private static IEnumerable<string> Forms(string secret)
    {
        var trimmed = secret.Trim();
        return new[]
            {
                secret, JsonEncodedText.Encode(secret).Value, JsonEncodedText.Encode(secret, RelaxedEncoder).Value, JsonStringify(secret),
                trimmed, JsonEncodedText.Encode(trimmed).Value, JsonStringify(trimmed),
            }
            .Where(form => form.Length >= MinSecretLength).Distinct(StringComparer.Ordinal);
    }

    /// <summary>Строка так, как её записывает JSON.stringify (без внешних кавычек).</summary>
    internal static string JsonStringify(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var paired = char.IsHighSurrogate(c) ? i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])
                : char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1]);
            switch (c)
            {
                case '"':
                    result.Append("\\\"");
                    break;
                case '\\':
                    result.Append("\\\\");
                    break;
                case '\b':
                    result.Append("\\b");
                    break;
                case '\f':
                    result.Append("\\f");
                    break;
                case '\n':
                    result.Append("\\n");
                    break;
                case '\r':
                    result.Append("\\r");
                    break;
                case '\t':
                    result.Append("\\t");
                    break;
                case < ' ':
                    result.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    if (char.IsSurrogate(c) && !paired)
                    {
                        result.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        result.Append(c);
                    }

                    break;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Значения внутри секретного значения: строки и числа (ПИН-код, CVV) — и у секретного списка или объекта. Числа ищутся
    /// только целым числом, так что «1234» не найдётся в «A-12345».
    /// </summary>
    private static void Collect(JsonNode? node, List<string> secrets)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj)
                {
                    Collect(value, secrets);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Collect(item, secrets);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text.Length >= MinSecretLength)
                {
                    secrets.Add(text);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                if (value.ToJsonString() is { Length: >= MinSecretLength } number)
                {
                    secrets.Add(number);
                }

                break;
        }
    }

    /// <summary>
    /// Добавить к секретам процесса прежние значения его секретных переменных и пришедшие извне. Ничего не забывается:
    /// забытое значение показалось бы там, где оно хранится настоящим (параметры прошлых шагов, копии в обычных переменных).
    /// </summary>
    internal static ProcessSecrets? Remember(ProcessSecrets? known, IEnumerable<string> previous, IEnumerable<string> carried)
    {
        var carriedList = Add(known?.Carried, carried, []);
        var previousList = Add(known?.Previous, previous, carriedList);
        return carriedList.Count == 0 && previousList.Count == 0
            ? null
            : known is not null && carriedList.Count == known.Carried.Count && previousList.Count == known.Previous.Count
                ? known
                : new ProcessSecrets { Carried = carriedList, Previous = previousList };
    }

    private static List<string> Add(IReadOnlyList<string>? list, IEnumerable<string> values, IReadOnlyList<string> elsewhere)
    {
        var result = new List<string>(list ?? []);
        var seen = new HashSet<string>(result, StringComparer.Ordinal);
        seen.UnionWith(elsewhere);
        foreach (var value in values)
        {
            if (value.Length >= MinSecretLength && seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    /// <summary>Числа короче не передаются другим процессам секретом: «100» и «200» есть в любых данных.</summary>
    private const int MinCarriedNumberLength = 4;

    /// <summary>
    /// Какие из секретных значений есть внутри данных (в значениях и именах полей, в том числе в записи JSON) — по тому же
    /// правилу, что и при скрытии: длинное — где угодно в строке, короткое и число — отдельным словом. Числа короче четырёх
    /// цифр (CVV внутри секретного объекта) секретом другому процессу не передаются: в нём это было бы любое «100».
    /// </summary>
    internal static List<string> SecretsIn(IReadOnlyList<string> secrets, params JsonNode?[] data)
    {
        var carried = secrets.Where(s => s.Length >= MinSecretLength && !(s.Length < MinCarriedNumberLength && s.All(char.IsAsciiDigit)))
            .Distinct(StringComparer.Ordinal).ToList();
        if (carried.Count == 0)
        {
            return [];
        }

        var strings = new List<string>();
        foreach (var node in data)
        {
            Strings(node, strings);
        }

        // Все записи всех значений — за один проход по каждой строке.
        var forms = new SecretList(carried.SelectMany(Forms));
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in strings)
        {
            forms.Find(text, found);
        }

        return carried.Where(secret => Forms(secret).Any(found.Contains)).ToList();
    }

    private static void Strings(JsonNode? node, List<string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    into.Add(key);
                    Strings(value, into);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Strings(item, into);
                }

                break;
            case JsonValue value:
                into.Add(value.TryGetValue<string>(out var s) ? s : value.ToJsonString());
                break;
        }
    }

    public static string? Redact(string? text, IReadOnlyList<string> secrets)
    {
        if (text is null || secrets.Count == 0)
        {
            return text;
        }

        return (secrets as SecretList ?? new SecretList(secrets)).Redact(text);
    }

    public static JsonObject? Redact(JsonObject? json, IReadOnlyList<string> secrets) =>
        json is null || secrets.Count == 0 ? json : (JsonObject)RedactNode(json.DeepClone(), secrets)!;

    public static JsonNode? Redact(JsonNode? json, IReadOnlyList<string> secrets) =>
        json is null || secrets.Count == 0 ? json : RedactNode(json.DeepClone(), secrets);

    private static JsonNode? RedactNode(JsonNode? node, IReadOnlyList<string> secrets)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                // Секрет бывает и именем поля ({ [vars.password]: 1 }) — оно тоже скрывается (совпавшие имена — с номером).
                var result = new JsonObject();
                foreach (var (key, value) in obj.ToList())
                {
                    var name = Redact(key, secrets)!;
                    for (var n = 2; result.ContainsKey(name); n++)
                    {
                        name = $"{Redact(key, secrets)} ({n})";
                    }

                    obj.Remove(key);
                    result[name] = RedactNode(value, secrets);
                }

                return result;
            }
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = RedactNode(array[i]?.DeepClone(), secrets);
                }

                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Redact(text, secrets));
            case JsonValue value when secrets.Contains(value.ToJsonString()):
                // Секретное число (ПИН-код) — целиком.
                return JsonValue.Create(SecretMask);
            default:
                return node;
        }
    }

    /// <summary>
    /// Итог шага для истории: тексты и результат без секретов (изменения переменных не трогаются). Результат, уже очищенный
    /// раньше (<paramref name="cleanOutput"/>), не проверяется второй раз.
    /// </summary>
    internal static NodeResult Redact(NodeResult result, IReadOnlyList<string> secrets, JsonObject? cleanOutput = null) => secrets.Count == 0
        ? result
        : result switch
        {
            NodeResult.Success ok => ok with
            {
                Message = Redact(ok.Message, secrets),
                Output = cleanOutput is not null && ReferenceEquals(ok.Output, cleanOutput) ? ok.Output : Redact(ok.Output, secrets),
            },
            NodeResult.RunAgain again => again with { Message = Redact(again.Message, secrets) },
            NodeResult.Failure fail => fail with { Error = Redact(fail.Error, secrets)! },
            NodeResult.Unknown unknown => unknown with { Reason = Redact(unknown.Reason, secrets)! },
            NodeResult.Wait wait => wait with { Message = Redact(wait.Message, secrets) },
            NodeResult.Finish finish => finish with { Message = Redact(finish.Message, secrets) },
            _ => result,
        };

    /// <summary>Значения секретных переменных — «••••» (в истории шага: изменения переменных).</summary>
    internal static JsonObject? MaskSecretVariables(JsonObject? variables, ScenarioDefinition definition)
    {
        if (variables is null || !definition.Variables.Any(v => v.Secret))
        {
            return variables;
        }

        var masked = (JsonObject)variables.DeepClone();
        foreach (var secret in definition.Variables.Where(v => v.Secret && masked[v.Name] is not null))
        {
            masked[secret.Name] = SecretMask;
        }

        return masked;
    }

    /// <summary>
    /// Секреты вызвавшего процесса, переданные вызванному сценарию: они секретны и в нём (даже если там переменная не
    /// отмечена секретной), а сами значения остаются настоящими.
    /// </summary>
    internal static PreparedStart InheritSecrets(ProcessRecord parent, PreparedStart prepared)
    {
        var parentSecrets = RawSecretValues(parent, Core.Serialization.PepaJson.Deserialize<ScenarioDefinition>(parent.DefinitionJson));
        return parentSecrets.Count == 0 ? prepared : prepared with { Secrets = SecretsIn(parentSecrets, prepared.Variables) };
    }
}

/// <summary>
/// Готовый к поиску набор секретных значений (длинные — первыми). Короткие и числа ищутся только отдельным словом (числом),
/// начала длинных — и перед «…» (блоки сокращают значения в сообщениях). Текст проходится один раз для всех значений сразу
/// (значения разложены по первым двум знакам), пересекающиеся совпадения объединяются — и текст собирается один раз:
/// время растёт с длиной текста, а не с числом секретов, запомненных процессом.
/// </summary>
public sealed class SecretList : IReadOnlyList<string>
{
    private readonly List<string> _items;
    private Dictionary<int, Entry[]>? _index;

    private sealed record Entry(string Value, bool Bounded, bool Digits, int MinPreview);

    public SecretList(IEnumerable<string> items) =>
        _items = items.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToList();

    public int Count => _items.Count;

    public string this[int index] => _items[index];

    public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private Dictionary<int, Entry[]> Index => _index ??= _items.Where(s => s.Length >= 2)
        .Select(s => new Entry(s, Bounded(s), IsNumber(s), Math.Min(ProcessEngine.ShortSecretLength, Math.Max(3, (s.Length + 1) / 2))))
        .GroupBy(e => Key(e.Value, 0))
        .ToDictionary(g => g.Key, g => g.ToArray());

    private static int Key(string text, int at) => (text[at] << 16) | text[at + 1];

    /// <summary>Короткое значение и число — только отдельным словом (числом), длинное — где угодно.</summary>
    private static bool Bounded(string secret) => secret.Length < ProcessEngine.ShortSecretLength || IsNumber(secret);

    private static bool IsNumber(string secret) => secret.All(char.IsAsciiDigit);

    private static bool IsWordChar(char c, bool digitsOnly) => digitsOnly ? char.IsAsciiDigit(c) : char.IsLetterOrDigit(c);

    /// <summary>
    /// Перед позицией — граница слова: начало текста или не буква (не цифра). В тексте с JSON (есть кавычки) — и конец
    /// экранирования «\n», «\t», «\uXXXX»: так выглядят значения, записанные как JSON. Цифры самого «\u1234» — не число.
    /// </summary>
    private static bool BoundaryBefore(string text, int at, bool digitsOnly, bool json)
    {
        if (digitsOnly && InsideUnicodeEscape(text, at))
        {
            return false;
        }

        if (at == 0 || !IsWordChar(text[at - 1], digitsOnly))
        {
            return true;
        }

        if (!json)
        {
            return false;
        }

        if (at >= 2 && text[at - 2] == '\\' && text[at - 1] is 'n' or 't' or 'r' or 'b' or 'f')
        {
            return true;
        }

        return at >= 6 && text[at - 6] == '\\' && text[at - 5] == 'u' && IsHex(text, at - 4, 4);
    }

    private static bool InsideUnicodeEscape(string text, int at)
    {
        for (var digits = 0; digits < 4; digits++)
        {
            var u = at - 1 - digits;
            if (u >= 1 && text[u] == 'u' && text[u - 1] == '\\' && IsHex(text, u + 1, digits))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHex(string text, int from, int count)
    {
        for (var i = from; i < from + count; i++)
        {
            if (i < 0 || i >= text.Length || !char.IsAsciiHexDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool BoundaryAfter(string text, int end, bool digitsOnly) => end == text.Length || !IsWordChar(text[end], digitsOnly);

    /// <summary>Какие значения есть в тексте (по правилу скрытия) — добавляются в <paramref name="found"/>.</summary>
    public void Find(string text, ISet<string> found) => Scan(text, previews: false, null, found);

    /// <summary>Есть ли значение в тексте по правилу скрытия.</summary>
    public static bool Occurs(string text, string secret)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        new SecretList([secret]).Find(text, found);
        return found.Count > 0;
    }

    /// <summary>
    /// Один проход по тексту: в каждой позиции — значения с теми же первыми двумя знаками. Полное совпадение (короткое и
    /// число — отдельным словом) или, если <paramref name="previews"/>, начало значения (не короче трёх знаков и половины
    /// значения), за которым сразу «…» или «...», — так его показал блок, сократив длинное значение.
    /// </summary>
    private void Scan(string text, bool previews, List<(int Start, int End)>? ranges, ISet<string>? found)
    {
        var index = Index;
        if (index.Count == 0)
        {
            return;
        }

        var json = text.Contains('"');
        for (var at = 0; at + 1 < text.Length; at++)
        {
            if (!index.TryGetValue(Key(text, at), out var candidates))
            {
                continue;
            }

            foreach (var entry in candidates)
            {
                var secret = entry.Value;
                var length = 2;
                while (length < secret.Length && at + length < text.Length && text[at + length] == secret[length])
                {
                    length++;
                }

                if (length == secret.Length)
                {
                    if (!entry.Bounded || (BoundaryBefore(text, at, entry.Digits, json) && BoundaryAfter(text, at + length, entry.Digits)))
                    {
                        ranges?.Add((at, at + length));
                        found?.Add(secret);
                    }
                }
                else if (previews && length >= entry.MinPreview && Ellipsis(text, at + length) is > 0 and var ellipsis &&
                         (!entry.Bounded || BoundaryBefore(text, at, entry.Digits, json)))
                {
                    ranges!.Add((at, at + length + ellipsis));
                }
            }
        }
    }

    private static int Ellipsis(string text, int at) =>
        at < text.Length && text[at] == '…' ? 1 : at + 3 <= text.Length && string.CompareOrdinal(text, at, "...", 0, 3) == 0 ? 3 : 0;

    public string Redact(string text)
    {
        var ranges = new List<(int Start, int End)>();
        Scan(text, text.Contains('…') || text.Contains("...", StringComparison.Ordinal), ranges, null);
        if (ranges.Count == 0)
        {
            return text;
        }

        // Пересекающиеся совпадения («abcdef» и «defghi» в «abcdefghi») скрываются вместе — без хвостов.
        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var result = new System.Text.StringBuilder(text.Length);
        var position = 0;
        var current = ranges[0];
        foreach (var range in ranges.Skip(1).Append((Start: int.MaxValue, End: int.MaxValue)))
        {
            if (range.Start < current.End)
            {
                current.End = Math.Max(current.End, range.End);
                continue;
            }

            result.Append(text, position, current.Start - position).Append(ProcessEngine.SecretMask);
            position = current.End;
            current = range;
        }

        return result.Append(text, position, text.Length - position).ToString();
    }
}

/// <summary>
/// Как показать процесс, не раскрывая секретов: секретные переменные — «••••»; в параметрах шага — каждое поле, в котором
/// блок использовал секретную переменную (так скрыто и прежнее значение, если переменную потом поменяли); везде остальное —
/// по значениям (все значения, которые были секретными в этом процессе).
/// </summary>
public sealed class SecretMasker
{
    private readonly ScenarioDefinition _definition;
    private readonly IReadOnlyList<string> _secretNames;
    private readonly Dictionary<string, NodeDefinition> _nodes;
    private Regex? _usesSecret;

    public SecretMasker(ProcessRecord process, ScenarioDefinition definition)
    {
        _definition = definition;
        _secretNames = definition.Variables.Where(v => v.Secret).Select(v => v.Name).ToList();
        _nodes = definition.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Values = ProcessEngine.SecretValues(process, definition);
    }

    /// <summary>vars.password, vars?.password, vars["password"], vars?.['password'] — в шаблонах и выражениях (строится, когда нужен).</summary>
    private Regex UsesSecretPattern => _usesSecret ??= new Regex(
        @"\bvars\s*(?:\?\.|\.)\s*(?:" + string.Join("|", _secretNames.Select(Regex.Escape)) + @")(?![\p{L}\p{N}_$])|\bvars\s*(?:\?\.)?\[\s*(['""`])(?:" +
        string.Join("|", _secretNames.Select(Regex.Escape)) + @")\1\s*\]",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public IReadOnlyList<string> Values { get; }

    public bool Any => _secretNames.Count > 0 || Values.Count > 0;

    public string? Text(string? text) => ProcessEngine.Redact(text, Values);

    public JsonObject? Json(JsonObject? json) => ProcessEngine.Redact(json, Values);

    public JsonObject Variables(JsonObject variables) =>
        ProcessEngine.Redact(ProcessEngine.MaskSecretVariables(variables, _definition), Values)!;

    /// <summary>Сценарий для показа: значения по умолчанию секретных переменных скрыты.</summary>
    public ScenarioDefinition Definition() => _secretNames.Count == 0
        ? _definition
        : _definition with
        {
            Variables = _definition.Variables.Select(v => v.Secret && v.Default is not null ? v with { Default = ProcessEngine.SecretMask } : v).ToList(),
        };

    public StepRecord Step(StepRecord step)
    {
        if (!Any)
        {
            return step;
        }

        var input = step.Input is null ? null : (JsonObject)step.Input.DeepClone();
        if (input is not null && _secretNames.Count > 0 && _nodes.TryGetValue(step.NodeId, out var node))
        {
            foreach (var (field, value) in node.Config)
            {
                if (input.ContainsKey(field) && UsesSecret(value))
                {
                    input[field] = ProcessEngine.SecretMask;
                }
            }

            // «Задать переменную» для секретной переменной: значение — секрет целиком, даже записанное в самом блоке.
            if (node.Type == Nodes.SetVariableNode.TypeName && input.ContainsKey("value") &&
                (input["variable"] ?? node.Config["variable"]) is JsonValue target && target.TryGetValue<string>(out var name) &&
                _secretNames.Contains(name, StringComparer.Ordinal))
            {
                input["value"] = ProcessEngine.SecretMask;
            }
        }

        var output = step.Output is null ? null : (JsonObject)step.Output.DeepClone();
        if (output?["variables"] is JsonObject updates)
        {
            output["variables"] = ProcessEngine.MaskSecretVariables(updates, _definition);
        }

        return step with
        {
            Input = Json(input),
            Output = Json(output),
            Error = Text(step.Error),
            Resolution = Text(step.Resolution),
        };
    }

    /// <summary>В поле блока (в любой его строке) есть обращение к секретной переменной.</summary>
    private bool UsesSecret(JsonNode? value) => value switch
    {
        JsonObject obj => obj.Any(p => UsesSecret(p.Value)),
        JsonArray array => array.Any(UsesSecret),
        JsonValue v when v.TryGetValue<string>(out var text) => UsesSecretPattern.IsMatch(text),
        _ => false,
    };
}
