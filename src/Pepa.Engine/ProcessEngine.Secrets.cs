using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

/// <summary>
/// Секретные переменные (пароли): их значения не должны попадать в историю процесса. Тексты, которые движок записывает о
/// шаге (сообщение, ошибка, пояснение, результат), проходят через замену значений на «••••» — так блокам (в том числе
/// расширениям) не нужно помнить, что им передали пароль. Параметры шага и сами переменные хранятся настоящими (по ним
/// процесс продолжается и проверяет результат), а при показе их скрывает API.
/// </summary>
public sealed partial class ProcessEngine
{
    public const string SecretMask = "••••";

    /// <summary>Значения секретных переменных процесса (короче трёх знаков не скрываются: такие встречаются в любом тексте).</summary>
    public static IReadOnlyList<string> SecretValues(ProcessRecord process, ScenarioDefinition definition)
    {
        var secrets = new List<string>();
        foreach (var variable in definition.Variables.Where(v => v.Secret))
        {
            var value = process.Variables[variable.Name] switch
            {
                JsonValue v when v.TryGetValue<string>(out var text) => text,
                null => null,
                var other => other.ToJsonString(),
            };
            if (value is { Length: >= 3 })
            {
                secrets.Add(value);
            }
        }

        // Длинные — первыми: значение, которое содержит другое, скрывается целиком.
        return secrets.Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToList();
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
}
