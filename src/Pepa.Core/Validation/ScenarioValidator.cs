using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pepa.Core.Expressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Core.Validation;

public enum IssueSeverity
{
    Error,
    Warning,
}

/// <summary>Замечание проверки. <see cref="Code"/> стабилен и пригоден для локализации и тестов.</summary>
public sealed record ValidationIssue(
    IssueSeverity Severity,
    string Code,
    string Message,
    string? NodeId = null,
    string? EdgeId = null,
    string? Field = null);

public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);
}

/// <summary>
/// Проверка сценария до запуска. Сценарий с ошибками можно сохранить как черновик, но нельзя запустить.
/// </summary>
public sealed partial class ScenarioValidator(NodeRegistry registry, IExpressionEvaluator expressions)
{
    [GeneratedRegex(@"^[\p{L}_$][\p{L}\p{N}_$]*$")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimeRegex();

    public static bool IsValidVariableName(string name) => IdentifierRegex().IsMatch(name) && !ReservedNames.Contains(name);

    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "vars", "lastError", "process", "this", "true", "false", "null", "undefined", "new", "function",
        "return", "var", "let", "const", "if", "else", "for", "while", "do", "switch", "case", "break",
        "continue", "typeof", "instanceof", "in", "of", "delete", "void", "class", "extends", "super",
        "import", "export", "try", "catch", "finally", "throw", "yield", "await", "async", "with", "default",
        "enum", "static", "debugger",
    };

    public ValidationReport Validate(ScenarioDefinition scenario)
    {
        var issues = new List<ValidationIssue>();
        void Error(string code, string message, string? node = null, string? edge = null, string? field = null) =>
            issues.Add(new ValidationIssue(IssueSeverity.Error, code, message, node, edge, field));
        void Warning(string code, string message, string? node = null, string? edge = null, string? field = null) =>
            issues.Add(new ValidationIssue(IssueSeverity.Warning, code, message, node, edge, field));

        if (scenario.SchemaVersion != ScenarioDefinition.CurrentSchemaVersion)
        {
            Error("schema.version",
                $"Версия формата {scenario.SchemaVersion} не поддерживается (ожидается {ScenarioDefinition.CurrentSchemaVersion}).");
            return new ValidationReport(issues);
        }

        if (string.IsNullOrWhiteSpace(scenario.Name))
        {
            Error("scenario.name", "Укажите название сценария.");
        }

        var variables = ValidateVariables(scenario, Error, Warning);

        // Блоки
        var nodes = new Dictionary<string, NodeDefinition>(StringComparer.Ordinal);
        var descriptors = new Dictionary<string, NodeTypeDescriptor>(StringComparer.Ordinal);
        foreach (var node in scenario.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                Error("node.id.empty", "У блока нет идентификатора.");
                continue;
            }

            if (!nodes.TryAdd(node.Id, node))
            {
                Error("node.id.duplicate", $"Идентификатор блока «{node.Id}» повторяется.", node.Id);
                continue;
            }

            if (!registry.TryGet(node.Type, out var executor))
            {
                Error("node.type.unknown", $"Неизвестный тип блока «{node.Type}».", node.Id);
                continue;
            }

            descriptors[node.Id] = executor.Descriptor;
            ValidateConfig(node, executor, variables, Error);
            ValidateSettings(node, executor.Descriptor, Error);
        }

        string Name(string nodeId) =>
            nodes.TryGetValue(nodeId, out var n)
                ? DisplayName(n, descriptors.GetValueOrDefault(nodeId))
                : $"«{nodeId}»";

        var starts = scenario.Nodes.Where(n => descriptors.GetValueOrDefault(n.Id)?.IsStart == true).ToList();
        if (starts.Count == 0)
        {
            Error("start.missing", "Нет блока «Начало». С него начинается выполнение.");
        }
        else if (starts.Count > 1)
        {
            foreach (var extra in starts.Skip(1))
            {
                Error("start.multiple", "Блок «Начало» может быть только один.", extra.Id);
            }
        }

        if (!scenario.Nodes.Any(n => descriptors.GetValueOrDefault(n.Id)?.IsEnd == true))
        {
            Error("end.missing", "Нет блока «Завершение». Добавьте его, чтобы процесс мог закончиться.");
        }

        // Связи
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var validEdges = new List<EdgeDefinition>();
        foreach (var edge in scenario.Edges)
        {
            if (string.IsNullOrWhiteSpace(edge.Id) || !edgeIds.Add(edge.Id))
            {
                Error("edge.id", $"Идентификатор связи «{edge.Id}» пустой или повторяется.", edge: edge.Id);
                continue;
            }

            if (!nodes.ContainsKey(edge.Source) || !nodes.ContainsKey(edge.Target))
            {
                Error("edge.dangling", "Связь ведёт к несуществующему блоку.", edge: edge.Id);
                continue;
            }

            if (descriptors.TryGetValue(edge.Source, out var sourceDescriptor) &&
                sourceDescriptor.FindOutput(edge.SourcePort) is null)
            {
                Error("edge.port.unknown", $"У блока {Name(edge.Source)} нет выхода «{edge.SourcePort}».", edge.Source, edge.Id);
                continue;
            }

            if (descriptors.TryGetValue(edge.Target, out var targetDescriptor) && targetDescriptor.IsStart)
            {
                Error("edge.into.start", "В блок «Начало» нельзя вести связь.", edge.Target, edge.Id);
                continue;
            }

            validEdges.Add(edge);
        }

        // Каждый выход: не больше одной связи; обязательные выходы подключены.
        foreach (var (nodeId, descriptor) in descriptors)
        {
            foreach (var port in descriptor.Outputs)
            {
                var count = validEdges.Count(e => e.Source == nodeId && e.SourcePort == port.Id);
                if (count > 1)
                {
                    Error("port.multiple",
                        $"Из выхода «{port.Label}» блока {Name(nodeId)} выходит несколько связей. " +
                        "Оставьте одну: параллельное выполнение задаётся отдельными блоками.", nodeId);
                }
                else if (count == 0 && port.Required)
                {
                    Error("port.unconnected", $"Выход «{port.Label}» блока {Name(nodeId)} не подключён.", nodeId);
                }
            }
        }

        // Достижимость
        if (starts.Count == 1)
        {
            var reachable = Traverse(starts[0].Id, validEdges, forward: true);
            foreach (var node in scenario.Nodes.Where(n => nodes.ContainsKey(n.Id) && !reachable.Contains(n.Id)))
            {
                Warning("node.unreachable", $"До блока {Name(node.Id)} нельзя дойти от «Начала» — он не выполнится.", node.Id);
            }

            var ends = scenario.Nodes.Where(n => descriptors.GetValueOrDefault(n.Id)?.IsEnd == true).Select(n => n.Id);
            var canFinish = new HashSet<string>(StringComparer.Ordinal);
            foreach (var end in ends)
            {
                canFinish.UnionWith(Traverse(end, validEdges, forward: false));
            }

            if (canFinish.Count > 0)
            {
                foreach (var nodeId in reachable.Where(id => !canFinish.Contains(id)))
                {
                    Warning("node.no.exit",
                        $"Из блока {Name(nodeId)} нельзя дойти до «Завершения» — процесс может зациклиться.", nodeId);
                }
            }
        }

        new ParallelCheck(descriptors, nodes, validEdges, Name, (code, message, node) => Error(code, message, node)).Run();
        return new ValidationReport(issues);
    }

    public static string DisplayName(NodeDefinition node, NodeTypeDescriptor? descriptor) =>
        $"«{(string.IsNullOrWhiteSpace(node.Label) ? descriptor?.Title ?? node.Type : node.Label)}»";

    private static Dictionary<string, VariableDefinition> ValidateVariables(
        ScenarioDefinition scenario,
        Action<string, string, string?, string?, string?> error,
        Action<string, string, string?, string?, string?> warning)
    {
        var variables = new Dictionary<string, VariableDefinition>(StringComparer.Ordinal);
        foreach (var variable in scenario.Variables)
        {
            if (!IsValidVariableName(variable.Name))
            {
                error("variable.name",
                    $"Имя переменной «{variable.Name}» недопустимо: используйте буквы, цифры и «_», не начиная с цифры.",
                    null, null, null);
                continue;
            }

            if (!variables.TryAdd(variable.Name, variable))
            {
                error("variable.duplicate", $"Переменная «{variable.Name}» объявлена дважды.", null, null, null);
                continue;
            }

            if (variable.Default is not null && !MatchesType(variable.Default, variable.Type))
            {
                warning("variable.default.type",
                    $"Значение по умолчанию переменной «{variable.Name}» не соответствует её типу.", null, null, null);
            }
        }

        return variables;
    }

    public static bool MatchesType(JsonNode? value, VariableType type) => type switch
    {
        VariableType.Any => true,
        _ when value is null => true,
        VariableType.Text => value.Kind() == JsonValueKind.String,
        VariableType.Number => value.Kind() == JsonValueKind.Number,
        VariableType.Boolean => value.Kind() is JsonValueKind.True or JsonValueKind.False,
        VariableType.List => value is JsonArray,
        VariableType.Object => value is JsonObject,
        _ => true,
    };

    public static bool IsFieldVisible(FieldDescriptor field, JsonObject config, IReadOnlyList<FieldDescriptor> fields)
    {
        if (field.VisibleWhen is not { } condition)
        {
            return true;
        }

        var other = fields.FirstOrDefault(f => f.Name == condition.Field);
        var value = config.TryGetPropertyValue(condition.Field, out var node) && node is not null
            ? node.AsString()
            : other?.Default.AsString();
        return value is not null && condition.Values.Contains(value);
    }

    private void ValidateConfig(
        NodeDefinition node,
        INodeExecutor executor,
        IReadOnlyDictionary<string, VariableDefinition> variables,
        Action<string, string, string?, string?, string?> error)
    {
        var descriptor = executor.Descriptor;
        var name = DisplayName(node, descriptor);
        foreach (var field in descriptor.Fields)
        {
            if (!IsFieldVisible(field, node.Config, descriptor.Fields))
            {
                continue;
            }

            node.Config.TryGetPropertyValue(field.Name, out var value);
            value ??= field.Default;
            var text = value.AsString();
            var isEmpty = value is null || (value.Kind() == JsonValueKind.String && string.IsNullOrWhiteSpace(text));

            if (isEmpty)
            {
                if (field.Required)
                {
                    error("field.required", $"{name}: заполните поле «{field.Label}».", node.Id, null, field.Name);
                }

                continue;
            }

            switch (field.Kind)
            {
                case FieldKind.Template or FieldKind.MultilineTemplate or FieldKind.Path:
                    if (expressions.CheckTemplate(text!) is { } templateError)
                    {
                        error("field.template", $"{name}, поле «{field.Label}»: {templateError}", node.Id, null, field.Name);
                    }

                    break;

                case FieldKind.Expression or FieldKind.Condition or FieldKind.MatchCondition:
                    if (expressions.CheckExpression(text!) is { } expressionError)
                    {
                        error("field.expression", $"{name}, поле «{field.Label}»: {expressionError}", node.Id, null, field.Name);
                    }

                    break;

                case FieldKind.Number or FieldKind.Seconds:
                    var number = value.AsNumber();
                    if (number is null)
                    {
                        error("field.number", $"{name}: в поле «{field.Label}» должно быть число.", node.Id, null, field.Name);
                    }
                    else if ((field.Min is { } min && number < min) || (field.Max is { } max && number > max))
                    {
                        error("field.range",
                            $"{name}: значение поля «{field.Label}» должно быть {RangeText(field)}.", node.Id, null, field.Name);
                    }

                    break;

                case FieldKind.Boolean:
                    if (value.Kind() is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        error("field.boolean", $"{name}: поле «{field.Label}» должно быть «да» или «нет».", node.Id, null, field.Name);
                    }

                    break;

                case FieldKind.Select:
                    if (field.Options is { } options && options.All(o => o.Value != text))
                    {
                        error("field.select", $"{name}: недопустимое значение «{text}» в поле «{field.Label}».", node.Id, null, field.Name);
                    }

                    break;

                case FieldKind.Variable:
                    if (!variables.ContainsKey(text!))
                    {
                        error("field.variable.undeclared",
                            $"{name}: переменная «{text}» не объявлена. Добавьте её в списке переменных сценария.",
                            node.Id, null, field.Name);
                    }

                    break;

                case FieldKind.Time:
                    if (!TimeRegex().IsMatch(text!))
                    {
                        error("field.time", $"{name}: время в поле «{field.Label}» укажите как ЧЧ:ММ, например 09:30.",
                            node.Id, null, field.Name);
                    }

                    break;
            }
        }

        foreach (var issue in executor.Validate(node))
        {
            error("node.config", $"{name}: {issue.Message}", node.Id, null, issue.Field);
        }
    }

    private static void ValidateSettings(NodeDefinition node, NodeTypeDescriptor descriptor,
        Action<string, string, string?, string?, string?> error)
    {
        var name = DisplayName(node, descriptor);
        if (node.Settings.RetryCount is < 0 or > 100)
        {
            error("settings.retry", $"{name}: число повторов должно быть от 0 до 100.", node.Id, null, null);
        }

        if (node.Settings.RetryDelaySeconds is < 0 or > 86_400)
        {
            error("settings.retry.delay", $"{name}: пауза перед повтором — от 0 до 86400 секунд.", node.Id, null, null);
        }

        if (node.Settings.TimeoutSeconds is { } timeout and (< 1 or > 86_400))
        {
            error("settings.timeout", $"{name}: тайм-аут — от 1 до 86400 секунд.", node.Id, null, null);
        }
    }

    private static string RangeText(FieldDescriptor field) => (field.Min, field.Max) switch
    {
        ({ } min, { } max) => $"от {Format(min)} до {Format(max)}",
        ({ } min, null) => $"не меньше {Format(min)}",
        (null, { } max) => $"не больше {Format(max)}",
        _ => "",
    };

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static HashSet<string> Traverse(string startId, IReadOnlyList<EdgeDefinition> edges, bool forward)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { startId };
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in edges)
            {
                var (from, to) = forward ? (edge.Source, edge.Target) : (edge.Target, edge.Source);
                if (from == current && visited.Add(to))
                {
                    queue.Enqueue(to);
                }
            }
        }

        return visited;
    }
}
