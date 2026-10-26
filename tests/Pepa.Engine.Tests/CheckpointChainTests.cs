using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>
/// Ключ от повторов (как random_id у Telegram) живёт в снимке шага и переходит к следующей попытке через
/// <see cref="NodeExecutionContext.PreviousCheckpoint"/>: неудачная подготовка не должна обрывать цепочку, а новое
/// посещение блока (цикл) — получать ключ прошлого.
/// </summary>
public sealed class CheckpointChainTests
{
    private sealed class KeyedSendNode : INodeExecutor
    {
        public ConcurrentQueue<string> SentKeys { get; } = new();
        public volatile bool HangPrepare;
        public volatile bool ThrowAfterSend;

        public NodeTypeDescriptor Descriptor { get; } = new()
        {
            Type = "test.keyed",
            Category = "test",
            Title = "Отправка с ключом",
            Description = "Как отправка в Telegram: ключ от повторов из снимка прошлой попытки.",
            Effect = NodeEffect.External,
            SupportsVerification = true,
            Outputs = [new PortDescriptor(Ports.Out, "Далее"), new PortDescriptor(Ports.Error, "Ошибка", PortKind.Error)],
        };

        public async ValueTask<JsonObject?> PrepareAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        {
            var key = context.PreviousCheckpoint?.GetString("key") ?? Guid.NewGuid().ToString("N");
            if (HangPrepare)
            {
                HangPrepare = false;
                await Task.Delay(Timeout.InfiniteTimeSpan, context.Time, cancellationToken);
            }

            return new JsonObject { ["key"] = key };
        }

        public ValueTask<NodeResult> ExecuteAsync(NodeExecutionContext context, CancellationToken cancellationToken)
        {
            SentKeys.Enqueue(context.Checkpoint!.GetString("key")!);
            if (ThrowAfterSend)
            {
                ThrowAfterSend = false;
                throw new IOException("связь оборвалась после отправки");
            }

            return ValueTask.FromResult<NodeResult>(NodeResult.Ok());
        }

        public ValueTask<VerificationResult> VerifyAsync(NodeExecutionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(VerificationResult.Inconclusive("нет связи"));
    }

    [Fact]
    public async Task Failed_preparation_between_attempts_keeps_the_dedupe_key()
    {
        var node = new KeyedSendNode { ThrowAfterSend = true };
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.Register(node));
        var id = await h.RunAsync(new ScenarioBuilder("Ключ")
            .Node("start", "start")
            .Node("send", "test.keyed")
            .Node("end", "end")
            .Chain("start", "send", "end").Build());

        await h.WaitForAsync(id, p => p is { Status: ProcessStatus.NeedsAttention, Attention: AttentionKind.UnknownStep });
        // «Повторить», но подготовка зависает и не успевает — шаг без снимка.
        node.HangPrepare = true;
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForAsync(id, p => p is { Status: ProcessStatus.NeedsAttention, Attention: AttentionKind.StepFailed }, advance: TimeSpan.FromSeconds(20));
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(1));

        var keys = node.SentKeys.ToArray();
        Assert.Equal(2, keys.Length);
        Assert.Equal(keys[0], keys[1]);
    }

    [Fact]
    public async Task Next_visit_of_the_block_in_a_loop_gets_a_new_key()
    {
        var node = new KeyedSendNode();
        await using var h = await EngineHarness.CreateAsync(extraNodes: r => r.Register(node));
        var id = await h.RunAsync(new ScenarioBuilder("Цикл")
            .Variable("items", VariableType.List, new JsonArray(1, 2))
            .Variable("item", VariableType.Any)
            .Node("start", "start")
            .Node("each", "loop.forEach", new { items = "vars.items", itemVariable = "item" })
            .Node("send", "test.keyed")
            .Node("end", "end")
            .Edge("start", "each").Edge("each", "send", Ports.Item).Edge("send", "each").Edge("each", "end", Ports.Done)
            .Build());

        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        var keys = node.SentKeys.ToArray();
        Assert.Equal(2, keys.Length);
        Assert.NotEqual(keys[0], keys[1]);
    }
}
