using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pepa.Core;

/// <summary>Безопасное чтение значений из параметров блоков и переменных.</summary>
public static class JsonObjectExtensions
{
    public static string? GetString(this JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) ? node.AsString() : null;

    public static bool? GetBoolean(this JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        if (value.TryGetValue<JsonElement>(out var element) &&
            element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return element.GetBoolean();
        }

        return null;
    }

    public static double? GetNumber(this JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) ? node.AsNumber() : null;

    /// <summary>Строковое значение узла; для чисел и логических — их запись, для null — null.</summary>
    public static string? AsString(this JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s))
            {
                return s;
            }

            if (value.TryGetValue<JsonElement>(out var element))
            {
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => element.GetRawText(),
                };
            }

            return value.ToJsonString();
        }

        return node.ToJsonString();
    }

    public static double? AsNumber(this JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number)
        {
            return element.GetDouble();
        }

        if (value.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>Тип значения в терминах JSON.</summary>
    public static JsonValueKind Kind(this JsonNode? node) => node switch
    {
        null => JsonValueKind.Null,
        JsonObject => JsonValueKind.Object,
        JsonArray => JsonValueKind.Array,
        JsonValue v => v.GetValueKind(),
        _ => JsonValueKind.Undefined,
    };
}
