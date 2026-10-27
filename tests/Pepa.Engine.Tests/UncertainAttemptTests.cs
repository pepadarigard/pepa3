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

    [Fact]
    public async Task Verify_of_a_converted_step_checks_the_earlier_attempt_and_does_not_run_the_action_again()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("order", ActionMode.ThrowAfterEffect, ActionMode.Fail);
        h.World.ForcedVerification["order"] = VerificationOutcome.Inconclusive;
        var id = await h.RunAsync(Act());
        await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep);
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep && p.StatusMessage!.Contains("Прошлая попытка"));

        // Теперь проверка видит внешнюю систему: «Проверить» сверяет первую попытку (по её снимку) — она выполнилась.
        h.World.ForcedVerification.TryRemove("order", out _);
        await h.Engine.ResumeAsync(id, ResumeAction.Verify);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.EffectCount("order"));
        Assert.Equal(2, h.World.Executions("order"));
    }

    [Fact]
    public async Task Final_failure_checks_the_earlier_attempt_by_itself()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("order", ActionMode.ThrowAfterEffect, ActionMode.Fail);
        h.World.ForcedVerification["order"] = VerificationOutcome.Inconclusive;
        var id = await h.RunAsync(Act());
        await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep);

        // Вторая попытка не удалась, но проверка первой показывает: выполнена — шаг выполнен, без вопроса пользователю.
        h.World.ForcedVerification.TryRemove("order", out _);
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.EffectCount("order"));
    }

    [Fact]
    public async Task Verify_that_finds_the_earlier_attempt_not_done_takes_the_error_branch()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("order", ActionMode.ThrowBeforeEffect, ActionMode.Fail);
        h.World.ForcedVerification["order"] = VerificationOutcome.Inconclusive;
        var id = await h.RunAsync(Act());
        await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep);
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep && p.StatusMessage!.Contains("Прошлая попытка"));

        h.World.ForcedVerification.TryRemove("order", out _);
        await h.Engine.ResumeAsync(id, ResumeAction.Verify);
        Assert.Equal("ветка «Ошибка»", (await h.WaitForAsync(id, p => p.IsTerminal)).StatusMessage);
        Assert.Equal(0, h.World.EffectCount("order"));
        Assert.Equal(2, h.World.Executions("order"));
    }

    [Fact]
    public async Task User_can_say_the_unknown_step_was_not_done()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("order", ActionMode.ThrowBeforeEffect);
        h.World.ForcedVerification["order"] = VerificationOutcome.Inconclusive;
        var id = await h.RunAsync(Act());
        var stopped = await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep);
        Assert.Contains(ProcessAction.MarkFailed, h.Engine.GetAllowedActions(stopped, await h.Store.GetTokensAsync([id])));

        await h.Engine.ResumeAsync(id, ResumeAction.MarkFailed);
        Assert.Equal("ветка «Ошибка»", (await h.WaitForAsync(id, p => p.IsTerminal)).StatusMessage);
        Assert.Equal(1, h.World.Executions("order"));
    }

    [Fact]
    public async Task Taking_the_blocks_own_resource_again_does_not_forget_the_earlier_attempt()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("order", ActionMode.ThrowAfterEffect, ActionMode.Fail);
        h.World.ForcedVerification["order"] = VerificationOutcome.Inconclusive;
        var id = await h.RunAsync(new ScenarioBuilder("С ресурсом")
            .Node("start", "start")
            .Node("act", TestActionNode.TypeName, new { key = "order", resource = "profile-1" })
            .Node("ok", "end")
            .Node("failed", "end", new { outcome = "failure", message = "ветка «Ошибка»" })
            .Chain("start", "act", "ok").Edge("act", "failed", Ports.Error).Build());
        await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep);

        // Ресурс освободили вручную, затем «Повторить»: блок снова занимает ресурс — это не новое посещение блока.
        await h.Engine.ForceReleaseResourceAsync("profile-1");
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        var stopped = await h.WaitForAsync(id, p => p.Attention == AttentionKind.UnknownStep && p.StatusMessage!.Contains("Прошлая попытка"));
        Assert.Equal(ProcessStatus.NeedsAttention, stopped.Status);
        await h.Engine.CancelAsync(id);
    }
}

