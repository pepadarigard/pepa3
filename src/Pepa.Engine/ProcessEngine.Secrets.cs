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

    /// <summary>Сколько секретных значений помнит процесс (при большем — самые старые забываются).</summary>
    private const int MaxRememberedSecrets = 500;

    /// <summary>Значения короче не скрываются: такие встречаются в любом тексте.</summary>
    private const int MinSecretLength = 3;

    private static readonly JavaScriptEncoder RelaxedEncoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// Значения, секретные для процесса: секретных переменных сейчас, запомненные раньше и (если передан итог шага) те, что
    /// шаг присваивает секретным переменным. Без вариантов записи — их добавляет <see cref="SecretValues"/>.
    /// </summary>
    internal static List<string> RawSecretValues(ProcessRecord process, ScenarioDefinition definition, NodeResult? result = null)
    {
        var secrets = new List<string>(process.Secrets ?? []);
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

    /// <summary>
    /// Что скрывать в текстах процесса: секретные значения и их записи в JSON (с экранированием — так их показывают
    /// предпросмотры значений). Длинные — первыми: значение, которое содержит другое, скрывается целиком.
    /// </summary>
    public static IReadOnlyList<string> SecretValues(ProcessRecord process, ScenarioDefinition definition, NodeResult? result = null) =>
        Expand(RawSecretValues(process, definition, result));

    /// <summary>Значения и их записи в JSON (обычная и без экранирования не-ASCII), длинные — первыми.</summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string>? values)
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

        return result.OrderByDescending(s => s.Length).ToList();
    }

    /// <summary>Строки и числа внутри значения (у секретного списка или объекта секретно каждое значение).</summary>
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
            case JsonValue value:
                var text = value.TryGetValue<string>(out var s) ? s : value.ToJsonString();
                if (text is { Length: >= MinSecretLength } && text is not ("true" or "false" or "null"))
                {
                    secrets.Add(text);
                }

                break;
        }
    }

    /// <summary>Запомнить секретные значения процесса (новые — в конец; лишние старые забываются).</summary>
    internal static IReadOnlyList<string>? Remember(IReadOnlyList<string>? known, IEnumerable<string> values)
    {
        var list = new List<string>(known ?? []);
        foreach (var value in values)
        {
            if (value.Length >= MinSecretLength && !list.Contains(value, StringComparer.Ordinal))
            {
                list.Add(value);
            }
        }

        if (list.Count > MaxRememberedSecrets)
        {
            list.RemoveRange(0, list.Count - MaxRememberedSecrets);
        }

        return list.Count == 0 ? null : list;
    }

    /// <summary>Какие из секретных значений есть внутри данных (целым значением или частью строки).</summary>
    internal static List<string> SecretsIn(IReadOnlyList<string> secrets, params JsonNode?[] data)
    {
        var strings = new List<string>();
        foreach (var node in data)
        {
            Strings(node, strings);
        }

        // Короткое значение (ПИН-код) — только если совпадает целиком: иначе «1234» нашлось бы в номере заказа «A-12345».
        return secrets.Where(secret => strings.Any(s => s == secret || (secret.Length >= 6 && s.Contains(secret, StringComparison.Ordinal))))
            .ToList();
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

        foreach (var secret in secrets)
        {
            // Короткое значение (ПИН-код) — только отдельным словом: «1234» в номере заказа «A-12345» — не ПИН.
            text = secret.Length >= ShortSecretLength
                ? text.Replace(secret, SecretMask, StringComparison.Ordinal)
                : Regex.Replace(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(secret) + @"(?![\p{L}\p{N}])", SecretMask, RegexOptions.CultureInvariant);
        }

        return RedactPreviews(text, secrets);
    }

    /// <summary>Значения короче скрываются только отдельным словом.</summary>
    private const int ShortSecretLength = 6;

    /// <summary>
    /// Блоки сокращают длинные значения в сообщениях («tok_9f8e…»): начало секрета перед «…» — тоже секрет.
    /// </summary>
    private static string RedactPreviews(string text, IReadOnlyList<string> secrets)
    {
        const int MinPrefix = ShortSecretLength;
        foreach (var ellipsis in new[] { "…", "..." })
        {
            var at = text.IndexOf(ellipsis, StringComparison.Ordinal);
            while (at >= MinPrefix)
            {
                var cut = 0;
                foreach (var secret in secrets)
                {
                    for (var length = Math.Min(secret.Length - 1, at); length >= MinPrefix; length--)
                    {
                        if (string.CompareOrdinal(text, at - length, secret, 0, length) == 0)
                        {
                            cut = Math.Max(cut, length);
                            break;
                        }
                    }
                }

                if (cut > 0)
                {
                    text = string.Concat(text.AsSpan(0, at - cut), SecretMask, text.AsSpan(at + ellipsis.Length));
                    at = text.IndexOf(ellipsis, at - cut + SecretMask.Length, StringComparison.Ordinal);
                }
                else
                {
                    at = text.IndexOf(ellipsis, at + ellipsis.Length, StringComparison.Ordinal);
                }
            }
        }

        return text;
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
/// Как показать процесс, не раскрывая секретов: секретные переменные — «••••»; в параметрах шага — каждое поле, в котором
/// блок использовал секретную переменную (так скрыто и прежнее значение, если переменную потом поменяли); везде остальное —
/// по значениям (все значения, которые были секретными в этом процессе).
/// </summary>
public sealed class SecretMasker
{
    private readonly ScenarioDefinition _definition;
    private readonly IReadOnlyList<string> _secretNames;
    private readonly Dictionary<string, NodeDefinition> _nodes;
    private readonly Regex? _usesSecret;

    public SecretMasker(ProcessRecord process, ScenarioDefinition definition)
    {
        _definition = definition;
        _secretNames = definition.Variables.Where(v => v.Secret).Select(v => v.Name).ToList();
        _nodes = definition.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Values = ProcessEngine.SecretValues(process, definition);
        if (_secretNames.Count > 0)
        {
            // vars.password, vars?.password, vars["password"], vars?.['password'] — в шаблонах и выражениях.
            var names = string.Join("|", _secretNames.Select(Regex.Escape));
            _usesSecret = new Regex(
                @"\bvars\s*(?:\?\.|\.)\s*(?:" + names + @")(?![\p{L}\p{N}_$])|\bvars\s*(?:\?\.)?\[\s*(['""`])(?:" + names + @")\1\s*\]",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
    }

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
        if (input is not null && _usesSecret is not null && _nodes.TryGetValue(step.NodeId, out var node))
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
        JsonValue v when v.TryGetValue<string>(out var text) => _usesSecret!.IsMatch(text),
        _ => false,
    };
}
