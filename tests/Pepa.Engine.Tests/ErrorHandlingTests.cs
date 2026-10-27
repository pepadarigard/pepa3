using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Остановка после ошибки, обработка ошибок, повторы, неизвестный результат и решения пользователя.</summary>
public sealed class ErrorHandlingTests
{
    private const string Action = TestActionNode.TypeName;

    private static ScenarioDefinition TwoSteps(NodeSettings? settings = null) => new ScenarioBuilder()
        .Node("start", "start")
        .Node("a", Action, new { key = "a" }, settings)
        .Node("b", Action, new { key = "b" })
        .Node("end", "end")
        .Chain("start", "a", "b", "end")
        .Build();

    [Fact]
    public async Task Failure_without_handler_stops_process_before_next_step()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.Fail);

        var id = await h.RunAsync(TwoSteps());
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Contains("Отказ внешней системы", process.StatusMessage);
        Assert.Equal(0, h.World.Executions("b"));
        Assert.Equal([ProcessAction.Retry, ProcessAction.Skip, ProcessAction.Cancel],
            h.Engine.GetAllowedActions(process, await h.Store.GetTokensAsync(id)));
        var journal = await h.Store.GetJournalAsync(id);
        Assert.Contains(journal, j => j.Level == JournalLevel.Error && j.Message.Contains("не выполнен"));
    }

    [Fact]
    public async Task Failure_routed_to_error_port_continues_with_last_error()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.Fail);
        var scenario = new ScenarioBuilder()
            .Variable("reason", VariableType.Text)
            .Node("start", "start")
            .Node("a", Action, new { key = "a" })
            .Node("handle", "variable.set", new { variable = "reason", value = "lastError.message" })
            .Node("end", "end")
            .Node("ok", "end")
            .Edge("start", "a")
            .Edge("a", "ok")
            .Edge("a", "handle", Ports.Error)
            .Edge("handle", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Contains("Отказ внешней системы", process.Variables.GetString("reason"));
        Assert.Equal("a", process.LastError!.GetString("nodeId"));
    }

    [Fact]
    public async Task Confirmed_failure_is_retried_up_to_limit_then_succeeds()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.Fail, ActionMode.Fail);

        var id = await h.RunAsync(TwoSteps(new NodeSettings { RetryCount = 2, RetryDelaySeconds = 30 }));
        await h.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting);
        Assert.Equal(1, h.World.Executions("a"));

        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(5));
        Assert.Equal(3, h.World.Executions("a"));
        Assert.Equal(1, h.World.EffectCount("a"));
        var attempts = (await h.StepsAsync(id)).Where(s => s.NodeId == "a").Select(s => (s.Attempt, s.State)).ToList();
        Assert.Equal([(1, StepState.Failed), (2, StepState.Failed), (3, StepState.Succeeded)], attempts);
    }

    [Fact]
    public async Task Retries_exhausted_stop_the_process()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.Fail, ActionMode.Fail, ActionMode.Fail);

        var id = await h.RunAsync(TwoSteps(new NodeSettings { RetryCount = 1, RetryDelaySeconds = 0 }));
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.Equal(2, h.World.Executions("a"));
        Assert.Equal(AttentionKind.StepFailed, process.Attention);
    }

    [Fact]
    public async Task Unknown_result_is_never_retried_automatically()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.ThrowAfterEffect);
        h.World.ForcedVerification["a"] = VerificationOutcome.Inconclusive;

        var id = await h.RunAsync(TwoSteps(new NodeSettings { RetryCount = 5, RetryDelaySeconds = 0 }));
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        await h.IdleAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(AttentionKind.UnknownStep, process.Attention);
        Assert.Equal(1, h.World.Executions("a"));
        Assert.Equal(0, h.World.Executions("b"));
        Assert.Equal([ProcessAction.Verify, ProcessAction.Retry, ProcessAction.MarkSucceeded, ProcessAction.MarkFailed, ProcessAction.Cancel],
            h.Engine.GetAllowedActions(process, await h.Store.GetTokensAsync(id)));
    }

    [Fact]
    public async Task Unknown_result_confirmed_by_verification_continues_without_repeat()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.ThrowAfterEffect);

        var id = await h.RunAsync(TwoSteps());
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal(1, h.World.Executions("a"));
        Assert.Equal(1, h.World.EffectCount("a"));
        var step = (await h.StepsAsync(id)).Single(s => s.NodeId == "a");
        Assert.Equal(StepState.Succeeded, step.State);
        Assert.Contains("Проверка", step.Resolution);
    }

    [Fact]
    public async Task Unknown_result_disproved_by_verification_is_treated_as_failure()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.ThrowBeforeEffect);

        var id = await h.RunAsync(TwoSteps(new NodeSettings { RetryCount = 1, RetryDelaySeconds = 0 }));
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal(2, h.World.Executions("a"));
        Assert.Equal(1, h.World.EffectCount("a"));
    }

    [Fact]
    public async Task User_marks_unknown_step_as_done_and_it_is_not_repeated()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.ThrowAfterEffect);
        h.World.ForcedVerification["a"] = VerificationOutcome.Inconclusive;

        var id = await h.RunAsync(TwoSteps());
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        await h.Engine.ResumeAsync(id, ResumeAction.MarkSucceeded);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal(1, h.World.Executions("a"));
        Assert.Equal(1, h.World.Executions("b"));
        var step = (await h.StepsAsync(id)).First(s => s.NodeId == "a");
        Assert.Equal(StepState.Succeeded, step.State);
        Assert.Equal("Отмечен выполненным пользователем.", step.Resolution);
    }

    [Fact]
    public async Task User_retries_unknown_step_explicitly()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.ThrowAfterEffect);
        h.World.ForcedVerification["a"] = VerificationOutcome.Inconclusive;

        var id = await h.RunAsync(TwoSteps());
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal(2, h.World.Executions("a"));
        var attempts = (await h.StepsAsync(id)).Where(s => s.NodeId == "a").Select(s => s.Attempt).ToList();
        Assert.Equal([1, 2], attempts);
    }

    [Fact]
    public async Task User_skips_failed_step()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.Fail);

        var id = await h.RunAsync(TwoSteps());
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        await h.Engine.ResumeAsync(id, ResumeAction.Skip);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal(1, h.World.Executions("a"));
        Assert.Equal(1, h.World.Executions("b"));
        Assert.Equal(StepState.Skipped, (await h.StepsAsync(id)).First(s => s.NodeId == "a").State);
    }

    [Fact]
    public async Task Action_not_allowed_in_current_state_is_rejected()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("a", ActionMode.Fail);
        var id = await h.RunAsync(TwoSteps());
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.ResumeAsync(id, ResumeAction.MarkSucceeded));
        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.ResumeAsync(id, ResumeAction.Continue));
        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.PauseAsync(id));
    }
}
