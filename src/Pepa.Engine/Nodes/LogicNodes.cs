using System.Globalization;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Engine.Nodes;

/// <summary>Встроенные логические блоки. Все — без внешних эффектов.</summary>
public static class LogicNodes
{
    public static NodeRegistry RegisterLogicNodes(this NodeRegistry registry) => registry
        .RegisterCategory(NodeCategories.Logic)
        .Register(new StartNode())
        .Register(new EndNode())
        .Register(new ConditionNode())
        .Register(new SetVariableNode())
        .Register(new LogNode())
        .Register(new WaitNode())
        .Register(new ForEachNode())
        .Register(new FindInTextNode());
}

public sealed class StartNode : INodeExecutor
{
    public const string TypeName = "start";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Logic.Id,
        Title = "Начало",
        Description = "С этого блока начинается выполнение сценария.",
        Icon = "play",
        IsStart = true,
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<NodeResult>(NodeResult.Ok());
}

public sealed class EndNode : INodeExecutor
{
    public const string TypeName = "end";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Logic.Id,
        Title = "Завершение",
        Description = "Завершает процесс успешно или с ошибкой.",
        Icon = "flag",
        IsEnd = true,
        SummaryField = "message",
        Fields =
        [
            new FieldDescriptor
            {
                Name = "outcome", Label = "Итог", Kind = FieldKind.Select, Default = "success",
                Options = [new("success", "Успешно"), new("failure", "С ошибкой")],
            },
            new FieldDescriptor
            {
                Name = "message", Label = "Сообщение", Kind = FieldKind.Template,
                Placeholder = "Например: Обработано {{ vars.count }} файлов",
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var success = context.GetString("outcome") != "failure";
        var message = context.GetString("message");
        return ValueTask.FromResult<NodeResult>(
            new NodeResult.Finish(success, string.IsNullOrWhiteSpace(message) ? null : message));
    }
}

public sealed class ConditionNode : INodeExecutor
{
    public const string TypeName = "condition";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Logic.Id,
        Title = "Условие",
        Description = "Проверяет условие и направляет процесс по ветке «Да» или «Нет».",
        Icon = "git-branch",
        SummaryField = "expression",
        Outputs = [new PortDescriptor(Ports.True, "Да"), new PortDescriptor(Ports.False, "Нет")],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "expression", Label = "Условие", Kind = FieldKind.Condition, Required = true,
                Placeholder = "vars.count < 3",
                Help = "Выражение JavaScript, дающее true или false. Переменные — через vars., например vars.count >= 10",
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var value = context.Input.GetBoolean("expression")
                    ?? throw new NodeInputException("Условие не дало результата.");
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(value ? Ports.True : Ports.False));
    }
}

public sealed class SetVariableNode : INodeExecutor
{
    public const string TypeName = "variable.set";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Logic.Id,
        Title = "Задать переменную",
        Description = "Записывает в переменную результат выражения.",
        Icon = "variable",
        SummaryField = "variable",
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
        Fields =
        [
            new FieldDescriptor { Name = "variable", Label = "Переменная", Kind = FieldKind.Variable, Required = true },
            new FieldDescriptor
            {
                Name = "value", Label = "Значение", Kind = FieldKind.Expression, Required = true,
                Placeholder = "vars.count + 1",
                Help = "Выражение JavaScript. Текст — в кавычках: \"готово\". Число — без кавычек: 5.",
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var variable = context.RequireString("variable");
        context.Input.TryGetPropertyValue("value", out var value);
        var updates = new Dictionary<string, JsonNode?> { [variable] = value?.DeepClone() };
        return ValueTask.FromResult<NodeResult>(NodeResult.Ok(Ports.Out, updates));
    }
}

public sealed class LogNode : INodeExecutor
{
    public const string TypeName = "log";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Logic.Id,
        Title = "Запись в журнал",
        Description = "Добавляет сообщение в журнал процесса.",
        Icon = "scroll-text",
        SummaryField = "message",
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "message", Label = "Сообщение", Kind = FieldKind.MultilineTemplate, Required = true,
                Placeholder = "Счётчик: {{ vars.count }}",
            },
            new FieldDescriptor
            {
                Name = "level", Label = "Важность", Kind = FieldKind.Select, Default = "info",
                Options = [new("info", "Обычное"), new("warning", "Предупреждение")],
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var message = context.GetString("message") ?? "";
        var level = context.GetString("level") == "warning" ? MessageLevel.Warning : MessageLevel.Info;
        return ValueTask.FromResult<NodeResult>(
            new NodeResult.Success(Ports.Out, new Dictionary<string, JsonNode?>(), null, message, level));
    }
}

public sealed class WaitNode : INodeExecutor
{
    public const string TypeName = "wait";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Logic.Id,
        Title = "Ожидание",
        Description = "Ждёт заданное время. Ожидание сохраняется и переживает закрытие приложения.",
        Icon = "timer",
        SummaryField = "amount",
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "mode", Label = "Как ждать", Kind = FieldKind.Select, Default = "duration",
                Options = [new("duration", "Заданное время"), new("untilTime", "До времени суток")],
            },
            new FieldDescriptor
            {
                Name = "amount", Label = "Сколько", Kind = FieldKind.Number, Default = 10, Min = 0, Max = 1_000_000,
                Required = true, VisibleWhen = new FieldCondition("mode", ["duration"]),
            },
            new FieldDescriptor
            {
                Name = "unit", Label = "Единица", Kind = FieldKind.Select, Default = "seconds",
                VisibleWhen = new FieldCondition("mode", ["duration"]),
                Options = [new("seconds", "секунд"), new("minutes", "минут"), new("hours", "часов"), new("days", "дней")],
            },
            new FieldDescriptor
            {
                Name = "time", Label = "Время", Kind = FieldKind.Time, Required = true, Placeholder = "09:30",
                Help = "Если это время сегодня уже прошло — ожидание до завтра.",
                VisibleWhen = new FieldCondition("mode", ["untilTime"]),
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var now = context.Time.GetUtcNow();
        DateTimeOffset until;
        if (context.GetString("mode") == "untilTime")
        {
            until = NextTimeOfDay(now, context.RequireString("time"), context.Time.LocalTimeZone);
        }
        else
        {
            var amount = context.GetNumber("amount") ?? throw new NodeInputException("Не указано, сколько ждать.");
            if (amount < 0)
            {
                throw new NodeInputException("Время ожидания не может быть отрицательным.");
            }

            var duration = context.GetString("unit") switch
            {
                "minutes" => TimeSpan.FromMinutes(amount),
                "hours" => TimeSpan.FromHours(amount),
                "days" => TimeSpan.FromDays(amount),
                _ => TimeSpan.FromSeconds(amount),
            };
            until = now + duration;
        }

        return ValueTask.FromResult<NodeResult>(new NodeResult.Wait(until, null));
    }

    public static DateTimeOffset NextTimeOfDay(DateTimeOffset now, string time, TimeZoneInfo zone)
    {
        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOfDay))
        {
            throw new NodeInputException($"Время «{time}» указано неверно, нужно ЧЧ:ММ.");
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = local.Date + timeOfDay.ToTimeSpan();
        if (candidate <= local.DateTime)
        {
            candidate = candidate.AddDays(1);
        }

        // Несуществующее время при переходе на летнее время — сдвигаем на час вперёд.
        while (zone.IsInvalidTime(candidate))
        {
            candidate = candidate.AddHours(1);
        }

        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }
}
