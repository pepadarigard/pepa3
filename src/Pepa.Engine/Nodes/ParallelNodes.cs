using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;

namespace Pepa.Engine.Nodes;

/// <summary>
/// Разделение процесса на параллельные ветви. Ветви выполняются по очереди, шаг за шагом, но ожидания (сообщение,
/// событие, время, ресурс) идут одновременно: пока одна ветвь ждёт ответ, другая работает.
/// </summary>
public sealed class ParallelNode : INodeExecutor
{
    public const string TypeName = "parallel";

    public static readonly IReadOnlyList<string> BranchPorts = ["b1", "b2", "b3", "b4"];

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Flow.Id,
        Title = "Параллельно",
        Description = "Делит процесс на ветви (2–4), которые идут одновременно: например, одна ждёт ответ в Telegram, пока другая " +
                      "следит за сайтом. Ветви сходятся в блоке «Дождаться всех ветвей». Шаги ветвей выполняются по очереди — " +
                      "одновременно идут ожидания.",
        Icon = "split",
        Flow = NodeFlow.Split,
        Outputs =
        [
            new PortDescriptor("b1", "Ветвь 1"),
            new PortDescriptor("b2", "Ветвь 2"),
            new PortDescriptor("b3", "Ветвь 3", PortKind.Optional),
            new PortDescriptor("b4", "Ветвь 4", PortKind.Optional),
        ],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
    {
        var connected = BranchPorts.Where(port => context.Graph?.Next(context.Node.Id, port) is not null).ToList();
        return ValueTask.FromResult<NodeResult>(connected.Count >= 2
            ? new NodeResult.Split(connected)
            : NodeResult.Fail("Подключите хотя бы две ветви.", retryable: false));
    }
}

/// <summary>Слияние: процесс ждёт, пока все ветви своего «Параллельно» придут сюда, и продолжает одной ветвью.</summary>
public sealed class JoinNode : INodeExecutor
{
    public const string TypeName = "join";

    public NodeTypeDescriptor Descriptor { get; } = new()
    {
        Type = TypeName,
        Category = NodeCategories.Flow.Id,
        Title = "Дождаться всех ветвей",
        Description = "Ждёт, пока все ветви блока «Параллельно» дойдут сюда, и продолжает процесс одной ветвью. Переменные, " +
                      "которые ветви сохранили, доступны дальше.",
        Icon = "merge",
        Flow = NodeFlow.Join,
        Outputs = [new PortDescriptor(Ports.Out, "Далее")],
    };

    public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult<NodeResult>(new NodeResult.Join());
}
