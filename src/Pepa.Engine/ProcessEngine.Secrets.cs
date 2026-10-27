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

    /// <summary>Сколько прежних значений секретных переменных помнит процесс (при большем — самые старые забываются).</summary>
    private const int MaxPreviousSecrets = 500;

    /// <summary>Сколько секретов, пришедших извне, помнит процесс (больше бывает только по ошибке сценария).</summary>
    private const int MaxCarriedSecrets = 5000;

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
            Collect(process.Variables[variable.Name], secrets, top: true);
            if (updates is not null && updates.TryGetValue(variable.Name, out var assigned))
            {
                Collect(assigned, secrets, top: true);
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
                Collect(process.Variables[variable.Name], replaced, top: true);
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

            result.Add(value);
            result.Add(JsonEncodedText.Encode(value).Value);
            result.Add(JsonEncodedText.Encode(value, RelaxedEncoder).Value);
        }

        return new SecretList(result);
    }

    /// <summary>
    /// Значения внутри секретного значения: само значение (строка или число — ПИН-код), а у секретного списка или объекта —
    /// его строки (числа и «да/нет» внутри — обычно служебные: код ответа 200, количество).
    /// </summary>
    private static void Collect(JsonNode? node, List<string> secrets, bool top)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj)
                {
                    Collect(value, secrets, top: false);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Collect(item, secrets, top: false);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                if (text.Length >= MinSecretLength)
                {
                    secrets.Add(text);
                }

                break;
            case JsonValue value when top && value.GetValueKind() == JsonValueKind.Number:
                if (value.ToJsonString() is { Length: >= MinSecretLength } number)
                {
                    secrets.Add(number);
                }

                break;
        }
    }

    /// <summary>
    /// Добавить к секретам процесса прежние значения его секретных переменных и пришедшие извне. Прежних помнится не больше
    /// <see cref="MaxPreviousSecrets"/> (забываются самые старые), пришедшие извне не вытесняются ими.
    /// </summary>
    internal static ProcessSecrets? Remember(ProcessSecrets? known, IEnumerable<string> previous, IEnumerable<string> carried)
    {
        var carriedList = Add(known?.Carried, carried, MaxCarriedSecrets);
        var previousList = Add(known?.Previous, previous.Where(v => !carriedList.Contains(v, StringComparer.Ordinal)), MaxPreviousSecrets);
        return carriedList.Count == 0 && previousList.Count == 0
            ? null
            : known is not null && carriedList.SequenceEqual(known.Carried) && previousList.SequenceEqual(known.Previous)
                ? known
                : new ProcessSecrets { Carried = carriedList, Previous = previousList };
    }

    private static List<string> Add(IReadOnlyList<string>? list, IEnumerable<string> values, int max)
    {
        var result = new List<string>(list ?? []);
        foreach (var value in values)
        {
            if (value.Length >= MinSecretLength && !result.Contains(value, StringComparer.Ordinal))
            {
                result.Add(value);
            }
        }

        if (result.Count > max)
        {
            result.RemoveRange(0, result.Count - max);
        }

        return result;
    }

    /// <summary>
    /// Какие из секретных значений есть внутри данных — по тому же правилу, что и при скрытии: длинное — где угодно в строке,
    /// короткое и число — отдельным словом.
    /// </summary>
    internal static List<string> SecretsIn(IReadOnlyList<string> secrets, params JsonNode?[] data)
    {
        var strings = new List<string>();
        foreach (var node in data)
        {
            Strings(node, strings);
        }

        return secrets.Where(secret => strings.Any(s => SecretList.Occurs(s, secret))).ToList();
    }

    private static void Strings(JsonNode? node, List<string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj)
                {
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
                foreach (var (key, value) in obj.ToList())
                {
                    obj[key] = RedactNode(value?.DeepClone(), secrets);
                }

                return obj;
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

    /// <summary>Итог шага для истории: тексты и результат без секретов (изменения переменных не трогаются).</summary>
    internal static NodeResult Redact(NodeResult result, IReadOnlyList<string> secrets) => secrets.Count == 0
        ? result
        : result switch
        {
            NodeResult.Success ok => ok with { Message = Redact(ok.Message, secrets), Output = Redact(ok.Output, secrets) },
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
/// Готовый к поиску набор секретных значений (длинные — первыми). Короткие и числа ищутся только отдельным словом, начала
/// длинных — и перед «…» (блоки сокращают значения в сообщениях).
/// </summary>
public sealed class SecretList : IReadOnlyList<string>
{
    private readonly List<string> _items;

    public SecretList(IEnumerable<string> items) =>
        _items = items.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToList();

    public int Count => _items.Count;

    public string this[int index] => _items[index];

    public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Короткое значение и число — только отдельным словом (числом), длинное — где угодно.</summary>
    private static bool Bounded(string secret) => secret.Length < ProcessEngine.ShortSecretLength || secret.All(char.IsAsciiDigit);

    private static bool IsWordChar(char c, bool digitsOnly) => digitsOnly ? char.IsAsciiDigit(c) : char.IsLetterOrDigit(c);

    /// <summary>Есть ли значение в тексте по правилу скрытия.</summary>
    public static bool Occurs(string text, string secret) => Find(text, secret, 0) >= 0;

    private static int Find(string text, string secret, int from)
    {
        var bounded = Bounded(secret);
        var digits = secret.All(char.IsAsciiDigit);
        for (var at = text.IndexOf(secret, from, StringComparison.Ordinal); at >= 0;
             at = at + 1 < text.Length ? text.IndexOf(secret, at + 1, StringComparison.Ordinal) : -1)
        {
            if (!bounded || ((at == 0 || !IsWordChar(text[at - 1], digits)) &&
                             (at + secret.Length == text.Length || !IsWordChar(text[at + secret.Length], digits))))
            {
                return at;
            }
        }

        return -1;
    }

    public string Redact(string text)
    {
        foreach (var secret in _items)
        {
            if (text.Length < secret.Length)
            {
                continue;
            }

            if (!Bounded(secret))
            {
                text = text.Replace(secret, ProcessEngine.SecretMask, StringComparison.Ordinal);
                continue;
            }

            for (var at = Find(text, secret, 0); at >= 0; at = Find(text, secret, at + ProcessEngine.SecretMask.Length))
            {
                text = string.Concat(text.AsSpan(0, at), ProcessEngine.SecretMask, text.AsSpan(at + secret.Length));
                if (at + ProcessEngine.SecretMask.Length >= text.Length)
                {
                    break;
                }
            }
        }

        return text.Contains('…') || text.Contains("...", StringComparison.Ordinal) ? RedactPreviews(text) : text;
    }

    /// <summary>
    /// Начало значения, за которым сразу «…» или «...», — тоже секрет (не короче трёх знаков и половины значения): так его
    /// показал блок, сократив длинное значение.
    /// </summary>
    private string RedactPreviews(string text)
    {
        foreach (var secret in _items)
        {
            var min = Math.Min(ProcessEngine.ShortSecretLength, Math.Max(3, (secret.Length + 1) / 2));
            if (secret.Length <= min)
            {
                continue;
            }

            var start = 0;
            while (start < text.Length && text.IndexOf(secret[..min], start, StringComparison.Ordinal) is var at and >= 0)
            {
                var length = min;
                while (length < secret.Length && at + length < text.Length && text[at + length] == secret[length])
                {
                    length++;
                }

                var rest = text.AsSpan(at + length);
                var ellipsis = rest.StartsWith("…") ? 1 : rest.StartsWith("...") ? 3 : 0;
                if (length < secret.Length && ellipsis > 0)
                {
                    text = string.Concat(text.AsSpan(0, at), ProcessEngine.SecretMask, text.AsSpan(at + length + ellipsis));
                    start = at + ProcessEngine.SecretMask.Length;
                }
                else
                {
                    start = at + 1;
                }
            }
        }

        return text;
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
