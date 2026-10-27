using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Engine.Nodes;

/// <summary>
/// Блоки событий, ресурсов и процессов. Сами они ничего не делают во внешнем мире — возвращают движку
/// намерение (ждать событие, занять ресурс, запустить сценарий), которое движок записывает одной транзакцией с итогом шага.
/// </summary>
public static class FlowNodes
{
    public static NodeRegistry RegisterFlowNodes(this NodeRegistry registry) => registry
        .RegisterCategory(NodeCategories.Flow)
        .Register(new WaitEventNode())
        .Register(new SendEventNode())
        .Register(new AcquireResourceNode())
        .Register(new ReleaseResourceNode())
        .Register(new CallScenarioNode())
        .Register(new RunScenarioNode())
        .Register(new ParallelNode())
        .Register(new JoinNode())
        .Register(new AskUserNode());

    internal static readonly PortDescriptor TimeoutPort = new(Ports.Timeout, "Тайм-аут", PortKind.Optional);

    internal static FieldDescriptor TimeoutField(string help) => new()
    {
        Name = "timeoutSeconds", Label = "Ждать не дольше, секунд", Kind = FieldKind.Seconds, Min = 0, Max = 31_536_000,
        Placeholder = "без ограничения", Help = help,
    };

    internal static DateTimeOffset? TimeoutAt(NodeExecutionContext context) =>
        context.GetNumber("timeoutSeconds") is { } seconds and > 0
            ? context.Time.GetUtcNow() + TimeSpan.FromSeconds(seconds)
            : null;

    internal static string? Optional(NodeExecutionContext context, string name) =>
        context.GetString(name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

public sealed class WaitEventNode : INodeExecutor
{
    public const string TypeName = "event.wait";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Flow.Id,
        Title = "Ждать событие",
        Description = "Ждёт подходящее событие (сообщение, сигнал другого процесса). Ожидание переживает закрытие приложения.",
        Icon = "bell-ring",
        SummaryField = "correlationKey",
        Outputs = [new PortDescriptor(Ports.Event, "Пришло"), FlowNodes.TimeoutPort],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "source", Label = "Источник", Kind = FieldKind.Text, Required = true, Default = EventSources.Manual,
                Help = "Откуда ждать событие: manual — отправлено вручную, process — другим процессом, http — по адресу API.",
            },
            new FieldDescriptor
            {
                Name = "eventType", Label = "Вид события", Kind = FieldKind.Text, Placeholder = "любой",
                Help = "Например: reply. Пусто — любое событие этого источника.",
            },
            new FieldDescriptor
            {
                Name = "correlationKey", Label = "Ключ сопоставления", Kind = FieldKind.Template,
                Placeholder = "{{ vars.orderId }}",
                Help = "Если указан — подойдёт только событие с точно таким ключом (номер заказа, диалога и т.п.).",
            },
            new FieldDescriptor
            {
                Name = "match", Label = "Дополнительное условие", Kind = FieldKind.MatchCondition,
                Placeholder = "event.payload.text.includes(\"код\")",
                Help = "Проверяется для каждого пришедшего события. Доступны event, vars и process.",
            },
            FlowNodes.TimeoutField("Если событие не придёт за это время — процесс пойдёт по выходу «Тайм-аут»."),
            new FieldDescriptor
            {
                Name = "lookbackSeconds", Label = "Учитывать события, пришедшие раньше, секунд", Kind = FieldKind.Seconds,
                Default = 0, Min = 0, Max = 86_400,
                Help = "Событие могло прийти чуть раньше, чем процесс начал ждать. Только события, которые никто не забрал.",
            },
            new FieldDescriptor
            {
                Name = "priority", Label = "Приоритет", Kind = FieldKind.Number, Default = 0, Min = -1000, Max = 1000,
                Help = "Если событие подходит нескольким процессам, его получит процесс с бо́льшим приоритетом. " +
                       "При равном — событие не отдаётся никому, а ждёт вашего решения.",
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить событие в", Kind = FieldKind.Variable },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        // Ключ задан, но получился пустым (например, переменная не заполнена): без ключа процесс принял бы чужое событие.
        var configuredKey = context.Node.Config.GetString("correlationKey");
        var key = context.GetString("correlationKey");
        if (!string.IsNullOrWhiteSpace(configuredKey) && string.IsNullOrEmpty(key))
        {
            throw new NodeInputException(
                $"Ключ сопоставления «{configuredKey}» получился пустым — без ключа процесс принял бы чужое событие. Проверьте переменные.");
        }

        if (string.Equals(context.GetString("source")?.Trim(), EventSources.Question, StringComparison.OrdinalIgnoreCase))
        {
            throw new NodeInputException($"Источник «{EventSources.Question}» служебный: ответы на вопросы ждёт блок «Спросить пользователя».");
        }

        var spec = new EventSubscriptionSpec
        {
            Source = context.RequireString("source").Trim(),
            EventType = FlowNodes.Optional(context, "eventType"),
            CorrelationKey = string.IsNullOrEmpty(key) ? null : key,
            MatchExpression = FlowNodes.Optional(context, "match"),
            Priority = (int)(context.GetNumber("priority") ?? 0),
            LookbackSeconds = (int)(context.GetNumber("lookbackSeconds") ?? 0),
            SaveTo = FlowNodes.Optional(context, "saveTo"),
        };
        return ValueTask.FromResult<NodeResult>(new NodeResult.WaitForEvent(spec, FlowNodes.TimeoutAt(context)));
    }
}

public sealed class SendEventNode : INodeExecutor
{
    public const string TypeName = "event.send";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Flow.Id,
        Title = "Отправить событие",
        Description = "Отправляет событие, которое может ждать другой процесс или запустить сценарий.",
        Icon = "send",
        SummaryField = "eventType",
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
        Fields =
        [
            new FieldDescriptor { Name = "source", Label = "Источник", Kind = FieldKind.Text, Required = true, Default = EventSources.Process },
            new FieldDescriptor { Name = "eventType", Label = "Вид события", Kind = FieldKind.Text, Required = true, Placeholder = "ready" },
            new FieldDescriptor
            {
                Name = "correlationKey", Label = "Ключ сопоставления", Kind = FieldKind.Template, Placeholder = "{{ vars.orderId }}",
            },
            new FieldDescriptor
            {
                Name = "payload", Label = "Данные", Kind = FieldKind.Expression, Placeholder = "{ text: vars.message }",
                Help = "Любое значение JavaScript. Получатель увидит его в event.payload.",
            },
            new FieldDescriptor
            {
                Name = "externalId", Label = "Ключ от повторов", Kind = FieldKind.Template, Placeholder = "{{ vars.orderId }}-ready",
                Help = "Событие с тем же источником и ключом второй раз не создаётся.",
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        context.Input.TryGetPropertyValue("payload", out var payload);
        var input = new EventInput
        {
            Source = context.RequireString("source").Trim(),
            Type = context.RequireString("eventType").Trim(),
            CorrelationKey = context.GetString("correlationKey") is { Length: > 0 } key ? key : null,
            ExternalId = FlowNodes.Optional(context, "externalId"),
            Payload = payload?.DeepClone(),
        };
        return ValueTask.FromResult<NodeResult>(new NodeResult.PublishEvent(input));
    }
}

public sealed class AcquireResourceNode : INodeExecutor
{
    public const string TypeName = "resource.acquire";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Flow.Id,
        Title = "Занять ресурс",
        Description = "Занимает ресурс (профиль браузера, аккаунт), чтобы другие процессы не использовали его одновременно. " +
                      "Если ресурс занят — процесс ждёт своей очереди, не мешая остальным.",
        Icon = "lock",
        SummaryField = "key",
        Outputs = [new PortDescriptor(Ports.Out, "Занят"), FlowNodes.TimeoutPort],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "key", Label = "Ресурс", Kind = FieldKind.Template, Required = true, Placeholder = "adspower:{{ vars.profile }}",
                Help = "Имя ресурса. Процессы с одинаковым именем ресурса не работают с ним одновременно.",
            },
            new FieldDescriptor { Name = "title", Label = "Понятное название", Kind = FieldKind.Template, Placeholder = "Профиль AdsPower" },
            FlowNodes.TimeoutField("Если ресурс не освободится за это время — процесс пойдёт по выходу «Тайм-аут»."),
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<NodeResult>(new NodeResult.AcquireResource(
            context.RequireString("key").Trim(), FlowNodes.Optional(context, "title"), FlowNodes.TimeoutAt(context)));
}

public sealed class ReleaseResourceNode : INodeExecutor
{
    public const string TypeName = "resource.release";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Flow.Id,
        Title = "Освободить ресурс",
        Description = "Освобождает ресурс для других процессов. При завершении процесса его ресурсы освобождаются сами.",
        Icon = "lock-open",
        SummaryField = "key",
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "key", Label = "Ресурс", Kind = FieldKind.Template, Required = true, Placeholder = "adspower:{{ vars.profile }}",
            },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<NodeResult>(new NodeResult.ReleaseResource(context.RequireString("key").Trim()));
}

public sealed class CallScenarioNode : INodeExecutor
{
    public const string TypeName = "scenario.call";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Flow.Id,
        Title = "Вызвать сценарий",
        Description = "Запускает другой сценарий отдельным процессом и ждёт, пока он завершится. " +
                      "Если остановить этот процесс — вызванный тоже остановится.",
        Icon = "workflow",
        SummaryField = "scenario",
        Outputs =
        [
            new PortDescriptor(Ports.Done, "Выполнен"),
            new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error),
            FlowNodes.TimeoutPort,
        ],
        Fields =
        [
            new FieldDescriptor { Name = "scenario", Label = "Сценарий", Kind = FieldKind.Scenario, Required = true },
            new FieldDescriptor
            {
                Name = "inputs", Label = "Входные данные", Kind = FieldKind.Expression, Placeholder = "{ profile: vars.profile }",
                Help = "Объект: имена переменных вызываемого сценария и их значения.",
            },
            new FieldDescriptor
            {
                Name = "saveTo", Label = "Сохранить итог в", Kind = FieldKind.Variable,
                Help = "Сюда запишутся переменные вызванного сценария на момент его завершения (объект).",
            },
            FlowNodes.TimeoutField("Если вызванный сценарий не завершится за это время — он будет остановлен, а процесс пойдёт по выходу «Тайм-аут»."),
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<NodeResult>(new NodeResult.StartChild(
            context.RequireString("scenario"), RunScenarioNode.Inputs(context), WaitForCompletion: true,
            FlowNodes.TimeoutAt(context), FlowNodes.Optional(context, "saveTo")));
}

public sealed class RunScenarioNode : INodeExecutor
{
    public const string TypeName = "scenario.run";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Flow.Id,
        Title = "Запустить сценарий",
        Description = "Запускает другой сценарий отдельным процессом и сразу идёт дальше, не дожидаясь его.",
        Icon = "rocket",
        SummaryField = "scenario",
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
        Fields =
        [
            new FieldDescriptor { Name = "scenario", Label = "Сценарий", Kind = FieldKind.Scenario, Required = true },
            new FieldDescriptor
            {
                Name = "inputs", Label = "Входные данные", Kind = FieldKind.Expression, Placeholder = "{ profile: vars.profile }",
                Help = "Объект: имена переменных запускаемого сценария и их значения.",
            },
            new FieldDescriptor { Name = "saveTo", Label = "Сохранить номер процесса в", Kind = FieldKind.Variable },
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<NodeResult>(new NodeResult.StartChild(
            context.RequireString("scenario"), Inputs(context), WaitForCompletion: false, null, FlowNodes.Optional(context, "saveTo")));

    internal static JsonObject? Inputs(NodeExecutionContext context)
    {
        context.Input.TryGetPropertyValue("inputs", out var inputs);
        return inputs switch
        {
            null => null,
            JsonObject value => (JsonObject)value.DeepClone(),
            _ => throw new NodeInputException("Входные данные должны быть объектом, например { profile: vars.profile }."),
        };
    }
}

public sealed class ForEachNode : INodeExecutor
{
    public const string TypeName = "loop.forEach";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Logic.Id,
        Title = "Для каждого элемента",
        Description = "Перебирает список: для каждого элемента процесс проходит ветку «Элемент» и возвращается сюда. " +
                      "Когда элементы закончатся — идёт по выходу «Готово».",
        Icon = "repeat",
        SummaryField = "items",
        Outputs = [new PortDescriptor(Ports.Item, "Элемент"), new PortDescriptor(Ports.Done, "Готово")],
        Fields =
        [
            new FieldDescriptor
            {
                Name = "items", Label = "Список", Kind = FieldKind.Expression, Required = true, Placeholder = "vars.files",
                Help = "Список вычисляется один раз при входе в цикл; изменения во время перебора на него не влияют.",
            },
            new FieldDescriptor { Name = "itemVariable", Label = "Текущий элемент — в переменную", Kind = FieldKind.Variable, Required = true },
            new FieldDescriptor { Name = "indexVariable", Label = "Номер элемента (с 0) — в переменную", Kind = FieldKind.Variable },
        ],
    };

    public IEnumerable<NodeConfigIssue> Validate(NodeDefinition node)
    {
        var item = node.Config.GetString("itemVariable");
        if (item is { Length: > 0 } && item == node.Config.GetString("indexVariable"))
        {
            yield return new NodeConfigIssue("элемент и номер нужно сохранять в разные переменные.", "indexVariable");
        }
    }

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var node = context.Node;
        var itemVariable = context.RequireString("itemVariable");
        var indexVariable = FlowNodes.Optional(context, "indexVariable");

        // Продолжение перебора — только если процесс вернулся сюда по кругу (из тела цикла), а не вошёл заново тем же путём,
        // что и в первый раз (например, следующий шаг внешнего цикла после выхода из этого). Новый вход начинает перебор сначала.
        var continuing = context.Memory is { } memory && context.EnteredFrom is { } from &&
                         from != memory.GetString("entry") &&
                         context.Graph?.LoopBody(node.Id, Ports.Item).Contains(from) == true;
        JsonArray items;
        int index;
        string? entry;
        if (continuing)
        {
            items = context.Memory!["items"]?.DeepClone() as JsonArray ?? [];
            index = (int)(context.Memory.GetNumber("next") ?? items.Count);
            entry = context.Memory.GetString("entry");
        }
        else
        {
            context.Input.TryGetPropertyValue("items", out var value);
            items = value switch
            {
                JsonArray array => (JsonArray)array.DeepClone(),
                null => throw new NodeInputException("Список для перебора пуст (null). Укажите список, например vars.files."),
                _ => throw new NodeInputException("Для перебора нужен список, например vars.files или [1, 2, 3]."),
            };
            index = 0;
            entry = context.EnteredFrom;
        }

        if (index >= items.Count)
        {
            var finished = new NodeResult.Success(Ports.Done, new Dictionary<string, JsonNode?>(), new JsonObject { ["count"] = items.Count },
                items.Count == 0 ? "Список пуст — перебирать нечего." : $"Перебор завершён: {items.Count} элемент(ов).")
            {
                Memory = null,
                ReplaceMemory = true,
            };
            return ValueTask.FromResult<NodeResult>(finished);
        }

        var updates = new Dictionary<string, JsonNode?> { [itemVariable] = items[index]?.DeepClone() };
        if (indexVariable is not null)
        {
            updates[indexVariable] = index;
        }

        return ValueTask.FromResult<NodeResult>(new NodeResult.Success(Ports.Item, updates,
            new JsonObject { ["index"] = index, ["count"] = items.Count }, null)
        {
            Memory = new JsonObject { ["items"] = items, ["next"] = index + 1, ["entry"] = entry },
            ReplaceMemory = true,
        });
    }
}
