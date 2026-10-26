using System.Text.Json.Nodes;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Runtime;

namespace Pepa.Core.Expressions;

public sealed record ExpressionLimits
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public int MaxStatements { get; init; } = 100_000;
    public long MemoryBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxRecursion { get; init; } = 256;
}

/// <summary>
/// Выражения на JavaScript (Jint) в песочнице: нет доступа к .NET, файлам и сети; ограничены время,
/// память, глубина рекурсии и число операций. Для каждого вычисления создаётся отдельный движок,
/// поэтому вычисления независимы и потокобезопасны.
/// </summary>
public sealed class JintExpressionEvaluator(ExpressionLimits? limits = null) : IExpressionEvaluator
{
    private readonly ExpressionLimits _limits = limits ?? new ExpressionLimits();

    public string? CheckExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return "Выражение не заполнено.";
        }

        try
        {
            Engine.PrepareScript(Wrap(expression), strict: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"Синтаксическая ошибка: {ex.Message}";
        }
    }

    public string? CheckTemplate(string template)
    {
        IReadOnlyList<TemplateParser.Segment> segments;
        try
        {
            segments = TemplateParser.Parse(template);
        }
        catch (ExpressionException ex)
        {
            return ex.Message;
        }

        foreach (var segment in segments)
        {
            if (segment is TemplateParser.Expression e && CheckExpression(e.Code) is { } error)
            {
                return $"{{{{ {e.Code} }}}}: {error}";
            }
        }

        return null;
    }

    public JsonNode? Evaluate(string expression, ExpressionScope scope)
    {
        var engine = CreateEngine(scope);
        var value = Run(engine, expression);
        return ToJson(engine, value, expression);
    }

    public bool EvaluateCondition(string expression, ExpressionScope scope)
    {
        var engine = CreateEngine(scope);
        var value = Run(engine, expression);
        if (value.IsBoolean())
        {
            return value.AsBoolean();
        }

        throw new ExpressionException(
            $"Условие «{expression}» должно давать «да» или «нет» (true/false), а получилось: {Describe(engine, value)}. " +
            "Добавьте сравнение, например: vars.count > 0");
    }

    public string RenderTemplate(string template, ExpressionScope scope)
    {
        var segments = TemplateParser.Parse(template);
        if (segments.All(s => s is TemplateParser.Literal))
        {
            return template;
        }

        var engine = CreateEngine(scope);
        var builder = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case TemplateParser.Literal literal:
                    builder.Append(literal.Text);
                    break;
                case TemplateParser.Expression expression:
                    builder.Append(Stringify(engine, Run(engine, expression.Code)));
                    break;
            }
        }

        return builder.ToString();
    }

    private Engine CreateEngine(ExpressionScope scope)
    {
        var engine = new Engine(options => options
            .TimeoutInterval(_limits.Timeout)
            .MaxStatements(_limits.MaxStatements)
            .LimitMemory(_limits.MemoryBytes)
            .LimitRecursion(_limits.MaxRecursion)
            .Strict());

        var parser = new JsonParser(engine);
        engine.SetValue("vars", parser.Parse(scope.Variables.ToJsonString()));
        engine.SetValue("lastError", scope.LastError is null ? JsValue.Null : parser.Parse(scope.LastError.ToJsonString()));
        engine.SetValue("process", parser.Parse(scope.Process?.ToJsonString() ?? "{}"));
        return engine;
    }

    private JsValue Run(Engine engine, string expression)
    {
        try
        {
            return engine.Evaluate(Wrap(expression));
        }
        catch (JavaScriptException ex)
        {
            throw new ExpressionException($"Ошибка в выражении «{expression}»: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new ExpressionException(
                $"Выражение «{expression}» выполняется слишком долго (больше {_limits.Timeout.TotalSeconds:0.#} с).", ex);
        }
        catch (MemoryLimitExceededException ex)
        {
            throw new ExpressionException($"Выражение «{expression}» использует слишком много памяти.", ex);
        }
        catch (StatementsCountOverflowException ex)
        {
            throw new ExpressionException($"Выражение «{expression}» выполняет слишком много операций.", ex);
        }
        catch (RecursionDepthOverflowException ex)
        {
            throw new ExpressionException($"Выражение «{expression}»: слишком глубокая рекурсия.", ex);
        }
        catch (ExpressionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExpressionException($"Ошибка в выражении «{expression}»: {ex.Message}", ex);
        }
    }

    private static JsonNode? ToJson(Engine engine, JsValue value, string expression)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        var serialized = new JsonSerializer(engine).Serialize(value, JsValue.Undefined, JsValue.Undefined);
        if (serialized.IsUndefined())
        {
            throw new ExpressionException(
                $"Результат выражения «{expression}» нельзя сохранить в переменную (это функция или символ).");
        }

        return JsonNode.Parse(serialized.AsString());
    }

    private static string Stringify(Engine engine, JsValue value)
    {
        if (value.IsUndefined() || value.IsNull())
        {
            return "";
        }

        if (value.IsString())
        {
            return value.AsString();
        }

        var serialized = new JsonSerializer(engine).Serialize(value, JsValue.Undefined, JsValue.Undefined);
        if (serialized.IsUndefined())
        {
            return TypeConverter.ToString(value);
        }

        var json = serialized.AsString();
        // Дата и другие объекты, которые сериализуются в строку, — без кавычек.
        return json.StartsWith('"') ? JsonNode.Parse(json)!.GetValue<string>() : json;
    }

    private static string Describe(Engine engine, JsValue value)
    {
        if (value.IsUndefined())
        {
            return "пустое значение (undefined)";
        }

        if (value.IsNull())
        {
            return "пустое значение (null)";
        }

        var text = Stringify(engine, value);
        if (text.Length > 60)
        {
            text = text[..60] + "…";
        }

        return value.IsString() ? $"строка «{text}»" : text;
    }

    private static string Wrap(string expression) => "(\n" + expression + "\n)";
}
