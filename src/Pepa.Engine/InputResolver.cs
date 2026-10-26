using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Expressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Validation;

namespace Pepa.Engine;

/// <summary>
/// Превращает параметры блока в конкретные значения шага: подставляет шаблоны, вычисляет выражения.
/// Результат сохраняется в шаге — история показывает, что именно было выполнено, а проверка результата
/// после сбоя использует те же значения.
/// </summary>
internal static class InputResolver
{
    public static JsonObject Resolve(NodeDefinition node, NodeTypeDescriptor descriptor, ExpressionScope scope,
        IExpressionEvaluator expressions)
    {
        var input = new JsonObject();
        foreach (var field in descriptor.Fields)
        {
            if (!ScenarioValidator.IsFieldVisible(field, node.Config, descriptor.Fields))
            {
                continue;
            }

            node.Config.TryGetPropertyValue(field.Name, out var raw);
            var value = (raw ?? field.Default)?.DeepClone();

            input[field.Name] = field.Kind switch
            {
                FieldKind.Template or FieldKind.MultilineTemplate or FieldKind.Path when value.AsString() is { } text =>
                    JsonValue.Create(expressions.RenderTemplate(text, scope)),
                FieldKind.Expression when value.AsString() is { Length: > 0 } code =>
                    expressions.Evaluate(code, scope),
                FieldKind.Condition when value.AsString() is { Length: > 0 } code =>
                    JsonValue.Create(expressions.EvaluateCondition(code, scope)),
                _ => value,
            };
        }

        return input;
    }
}
