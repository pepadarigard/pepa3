using System.Text.Json.Nodes;

namespace Pepa.Core.Expressions;

/// <summary>
/// Вычисление выражений и шаблонов строк. Выражения не имеют побочных эффектов: изменения переменных
/// внутри выражения не сохраняются.
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>Проверка синтаксиса выражения. null — ошибок нет.</summary>
    string? CheckExpression(string expression);

    /// <summary>Проверка синтаксиса шаблона и всех выражений в нём. null — ошибок нет.</summary>
    string? CheckTemplate(string template);

    JsonNode? Evaluate(string expression, ExpressionScope scope);

    /// <summary>Вычислить условие. Результат обязан быть логическим значением.</summary>
    bool EvaluateCondition(string expression, ExpressionScope scope);

    string RenderTemplate(string template, ExpressionScope scope);
}

/// <summary>Что доступно выражению: <c>vars</c>, <c>lastError</c>, <c>process</c>.</summary>
public sealed record ExpressionScope(JsonObject Variables, JsonObject? LastError = null, JsonObject? Process = null)
{
    public static ExpressionScope Empty { get; } = new(new JsonObject());
}

public sealed class ExpressionException(string message, Exception? inner = null) : Exception(message, inner);
