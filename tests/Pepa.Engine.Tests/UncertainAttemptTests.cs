using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Если прошлая попытка шага могла выполниться, окончательная ошибка следующей — не «не выполнено», а «итог неизвестен».</summary>
public sealed class UncertainAttemptTests
{
    private static ScenarioDefinition Act() => new ScenarioBuilder("Действие")
        .Node("start", "start")
        .Node("act", TestActionNode.TypeName, new { key = "order" })
        .Node("ok", "end")
        .Node("failed", "end", new { outcome = "failure", message = "ветка «Ошибка»" })
        .Chain("start", "act", "ok").Edge("act", "failed", Ports.Error).Build();

    [Fact]
    public async Task Failure_after_a_retry_of_an_unknown_step_is_unknown_not_the_error_branch()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("order", ActionMode.ThrowAfterEffect, ActionMode.Fail);
        h.World.ForcedVerification["order"] = VerificationOutcome.Inconclusive;
        var id = await h.RunAsync(Act());
        await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep);

        // Пользователь решил повторить; вторая попытка — «отказ», но первая могла выполниться (и выполнилась).
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        var stopped = await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep && p.StatusMessage!.Contains("Прошлая попытка"));
        Assert.Equal(ProcessStatus.NeedsAttention, stopped.Status);
        Assert.Equal(1, h.World.EffectCount("order"));
        await h.Engine.CancelAsync(id);
    }

    [Fact]
    public async Task Failure_after_definite_failures_still_takes_the_error_branch()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("order", ActionMode.Fail, ActionMode.Fail);
        var id = await h.RunAsync(new ScenarioBuilder("Действие")
            .Node("start", "start")
            .Node("act", TestActionNode.TypeName, new { key = "order" }, new NodeSettings { RetryCount = 1, RetryDelaySeconds = 0 })
            .Node("ok", "end")
            .Node("failed", "end", new { outcome = "failure", message = "ветка «Ошибка»" })
            .Chain("start", "act", "ok").Edge("act", "failed", Ports.Error).Build());
        var done = await h.WaitForAsync(id, p => p.IsTerminal);
        Assert.Equal("ветка «Ошибка»", done.StatusMessage);
    }
}
