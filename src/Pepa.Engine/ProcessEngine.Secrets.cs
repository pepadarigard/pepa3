using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Serialization;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

/// <summary>
/// Секретные переменные (пароли): их значения не должны попадать в историю процесса. Тексты, которые движок записывает о
/// шаге (сообщение, ошибка, пояснение, результат), проходят через замену значений на «••••» — так блокам (в том числе
/// расширениям) не нужно помнить, что им передали пароль. Параметры шага и сами переменные хранятся настоящими (по ним
/// процесс продолжается и проверяет результат), а при показе их скрывает <see cref="SecretMasker"/>.
/// </summary>
public sealed partial class ProcessEngine
{
    public const string SecretMask = "••••";

    /// <summary>Значения секретных переменных процесса (короче трёх знаков не скрываются: такие встречаются в любом тексте).</summary>
    /// <param name="result">Итог шага: значения, которые он присваивает секретным переменным, тоже секретны.</param>
    public static IReadOnlyList<string> SecretValues(ProcessRecord process, ScenarioDefinition definition, NodeResult? result = null)
    {
        var secrets = new List<string>();
        var updates = (result as NodeResult.Success)?.VariableUpdates;
        foreach (var variable in definition.Variables.Where(v => v.Secret))
        {
            Collect(process.Variables[variable.Name], secrets);
            if (updates is not null && updates.TryGetValue(variable.Name, out var assigned))
            {
                Collect(assigned, secrets);
            }
        }

        // Длинные — первыми: значение, которое содержит другое, скрывается целиком.
        return secrets.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToList();
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
                if (text is { Length: >= 3 } && text is not ("true" or "false" or "null"))
                {
                    secrets.Add(text);
                }

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
            text = text.Replace(secret, SecretMask, StringComparison.Ordinal);
        }

        return text;
    }

    public static JsonObject? Redact(JsonObject? json, IReadOnlyList<string> secrets) =>
        json is null || secrets.Count == 0 ? json : (JsonObject)RedactNode(json.DeepClone(), secrets)!;

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
    /// Значения, переданные вызванному сценарию из секретных переменных, секретны и в нём (даже если там переменная не
    /// отмечена): отметка ставится в копии сценария, с которой работает дочерний процесс.
    /// </summary>
    internal static PreparedStart InheritSecrets(ProcessRecord parent, PreparedStart prepared)
    {
        var parentSecrets = SecretValues(parent, PepaJson.Deserialize<ScenarioDefinition>(parent.DefinitionJson));
        if (parentSecrets.Count == 0)
        {
            return prepared;
        }

        var changed = false;
        var variables = prepared.Definition.Variables.Select(v =>
        {
            if (v.Secret || !ContainsAny(prepared.Variables[v.Name], parentSecrets))
            {
                return v;
            }

            changed = true;
            return v with { Secret = true };
        }).ToList();
        if (!changed)
        {
            return prepared;
        }

        var definition = prepared.Definition with { Variables = variables };
        return prepared with { Definition = definition, Scenario = prepared.Scenario with { DefinitionJson = PepaJson.Serialize(definition) } };
    }

    private static bool ContainsAny(JsonNode? node, IReadOnlyList<string> secrets) => node switch
    {
        JsonObject obj => obj.Any(p => ContainsAny(p.Value, secrets)),
        JsonArray array => array.Any(item => ContainsAny(item, secrets)),
        JsonValue value => (value.TryGetValue<string>(out var text) ? text : value.ToJsonString()) is var s &&
                           secrets.Any(secret => s.Contains(secret, StringComparison.Ordinal)),
        _ => false,
    };
}

/// <summary>
/// Как показать процесс, не раскрывая секретов: секретные переменные — «••••»; в параметрах шага — каждое поле, в котором
/// блок использовал секретную переменную (так скрыто и прежнее значение, если переменную потом поменяли); везде остальное —
/// по значениям.
/// </summary>
public sealed partial class SecretMasker
{
    private readonly ScenarioDefinition _definition;
    private readonly IReadOnlyList<string> _secretNames;
    private readonly Dictionary<string, NodeDefinition> _nodes;

    public SecretMasker(ProcessRecord process, ScenarioDefinition definition)
    {
        _definition = definition;
        _secretNames = definition.Variables.Where(v => v.Secret).Select(v => v.Name).ToList();
        _nodes = definition.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Values = ProcessEngine.SecretValues(process, definition);
    }

    public IReadOnlyList<string> Values { get; }

    public bool Any => _secretNames.Count > 0;

    public string? Text(string? text) => ProcessEngine.Redact(text, Values);

    public JsonObject? Json(JsonObject? json) => ProcessEngine.Redact(json, Values);

    public JsonObject Variables(JsonObject variables) =>
        ProcessEngine.Redact(ProcessEngine.MaskSecretVariables(variables, _definition), Values)!;

    /// <summary>Сценарий для показа: значения по умолчанию секретных переменных скрыты.</summary>
    public ScenarioDefinition Definition() => !Any
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
        if (input is not null && _nodes.TryGetValue(step.NodeId, out var node))
        {
            foreach (var (field, value) in node.Config)
            {
                if (input.ContainsKey(field) && value is not null && UsesSecret(value.ToJsonString()))
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

    /// <summary>В поле блока есть обращение к секретной переменной: vars.password или vars["password"].</summary>
    private bool UsesSecret(string config) => _secretNames.Any(name =>
        Regex.IsMatch(config, @"vars\s*(\.\s*" + Regex.Escape(name) + @"(?![\p{L}\p{N}_$])|\[\s*\\?['""`]" + Regex.Escape(name) + @"\\?['""`]\s*\])"));
}
