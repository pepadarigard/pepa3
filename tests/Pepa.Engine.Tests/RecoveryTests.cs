using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>
/// Сохранение состояния и восстановление: перезапуск, сбой между действием и записью результата,
/// отсутствие автозапуска, отсутствие повторов подтверждённых действий.
/// </summary>
public sealed class RecoveryTests
{
    private const string Action = TestActionNode.TypeName;

    private static ScenarioDefinition ActionWaitAction() => new ScenarioBuilder()
        .Node("start", "start")
        .Node("a", Action, new { key = "a" })
        .Node("w", "wait", new { mode = "duration", amount = 10, unit = "minutes" })
        .Node("b", Action, new { key = "b" })
        .Node("end", "end")
        .Chain("start", "a", "w", "b", "end")
        .Build();

    [Fact]
    public async Task After_restart_nothing_continues_automatically()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(ActionWaitAction());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        await h.RestartAsync();
        var paused = await h.ProcessAsync(id);
        Assert.Equal(ProcessStatus.Paused, paused.Status);
        Assert.Equal(PauseReason.Restart, paused.PauseReason);
        Assert.Equal(1, h.LastRecovery!.Paused);

        // Срок ожидания давно прошёл, но процесс стоит.
        await h.IdleAsync(TimeSpan.FromHours(2));
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(id)).Status);
        Assert.Equal(0, h.World.Executions("b"));

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(1));

        // Подтверждённое до перезапуска действие не повторилось.
        Assert.Equal(1, h.World.Executions("a"));
        Assert.Equal(1, h.World.Executions("b"));
        var journal = await h.Store.GetJournalAsync(id);
        Assert.Contains(journal, j => j.Message.Contains("позже срока"));
    }

    [Fact]
    public async Task Waiting_state_survives_restart_with_remaining_time()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(ActionWaitAction());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        var wakeAt = (await h.Store.GetTokensAsync(id)).Single().WakeAt;

        await h.RestartAsync();
        Assert.Equal(wakeAt, (await h.Store.GetTokensAsync(id)).Single().WakeAt);

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        var resumed = await h.ProcessAsync(id);
        Assert.Equal(ProcessStatus.Waiting, resumed.Status);

        await h.IdleAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(0, h.World.Executions("b"));
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.World.Executions("b"));
    }

    [Fact]
    public async Task Crash_between_action_and_saving_result_never_repeats_the_action()
    {
        await using var h = await EngineHarness.CreateAsync(withFaults: true);
        // Сбой ровно при записи «подтверждено» для действия «a»: действие уже выполнено, результат не сохранён.
        h.Faults.FailWhenStep = s => s is { NodeId: "a", State: StepState.Succeeded };

        var id = await h.RunAsync(ActionWaitAction());
        await h.WaitForAsync(id, _ => h.Faults.Tripped);
        await Task.Delay(200);
        Assert.Equal(1, h.World.EffectCount("a"));

        await h.CrashAndRestartAsync();
        var process = await h.ProcessAsync(id);
        Assert.Equal(ProcessStatus.NeedsAttention, process.Status);
        Assert.Equal(AttentionKind.UnknownStep, process.Attention);
        Assert.Equal(1, h.LastRecovery!.UnknownSteps);
        var step = (await h.StepsAsync(id)).Single(s => s.NodeId == "a");
        Assert.Equal(StepState.Unknown, step.State);

        // Ничего не происходит само.
        await h.IdleAsync(TimeSpan.FromHours(1));
        Assert.Equal(1, h.World.Executions("a"));

        // Пользователь просит проверить: внешний мир подтверждает выполнение — действие не повторяется.
        var result = await h.Engine.ResumeAsync(id, ResumeAction.Verify);
        Assert.Contains("подтвердила", result.Message);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromMinutes(1));
        Assert.Equal(1, h.World.Executions("a"));
        Assert.Equal(1, h.World.EffectCount("a"));
        Assert.Equal(1, h.World.Executions("b"));
    }

    [Fact]
    public async Task Crash_with_inconclusive_verification_requires_user_decision()
    {
        await using var h = await EngineHarness.CreateAsync(withFaults: true);
        h.Faults.FailWhenStep = s => s is { NodeId: "a", State: StepState.Succeeded };
        h.World.ForcedVerification["a"] = VerificationOutcome.Inconclusive;

        var id = await h.RunAsync(ActionWaitAction());
        await h.WaitForAsync(id, _ => h.Faults.Tripped);
        await h.CrashAndRestartAsync();

        var result = await h.Engine.ResumeAsync(id, ResumeAction.Verify);
        Assert.Contains("не дала однозначного ответа", result.Message);
        var process = await h.ProcessAsync(id);
        Assert.Equal(ProcessStatus.NeedsAttention, process.Status);
        Assert.Equal(1, h.World.Executions("a"));

        await h.Engine.ResumeAsync(id, ResumeAction.MarkSucceeded);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromMinutes(1));
        Assert.Equal(1, h.World.Executions("a"));
    }

    [Fact]
    public async Task Crash_before_action_started_replans_step_once_on_resume()
    {
        await using var h = await EngineHarness.CreateAsync(withFaults: true);
        // Сбой при записи «выполняется» — действие ещё не начато.
        h.Faults.FailWhenStep = s => s is { NodeId: "a", State: StepState.Running };

        var id = await h.RunAsync(ActionWaitAction());
        await h.WaitForAsync(id, _ => h.Faults.Tripped);
        await Task.Delay(200);
        Assert.Equal(0, h.World.Executions("a"));

        await h.CrashAndRestartAsync();
        var process = await h.ProcessAsync(id);
        Assert.Equal(ProcessStatus.Paused, process.Status);
        Assert.Equal(1, h.LastRecovery!.CancelledPlannedSteps);

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromMinutes(1));
        Assert.Equal(1, h.World.Executions("a"));
        var states = (await h.StepsAsync(id)).Where(s => s.NodeId == "a").Select(s => s.State).ToList();
        Assert.Equal([StepState.Cancelled, StepState.Succeeded], states);
    }

    [Fact]
    public async Task Storage_failure_on_result_marks_process_for_attention_without_restart()
    {
        await using var h = await EngineHarness.CreateAsync(withFaults: true);
        h.Faults.FailEverythingAfterFirstFault = false;
        h.Faults.FailWhenStep = s => s is { NodeId: "a", State: StepState.Succeeded };

        var id = await h.RunAsync(ActionWaitAction());
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        h.Faults.FailWhenStep = null;

        Assert.Equal(AttentionKind.UnknownStep, process.Attention);
        Assert.Contains("Не удалось сохранить результат", process.StatusMessage);
        Assert.Equal(1, h.World.Executions("a"));

        await h.Engine.ResumeAsync(id, ResumeAction.Verify);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromMinutes(1));
        Assert.Equal(1, h.World.Executions("a"));
    }

    [Fact]
    public async Task Process_paused_by_user_stays_paused_after_restart()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(ActionWaitAction());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await h.Engine.PauseAsync(id);

        await h.RestartAsync();
        var process = await h.ProcessAsync(id);
        Assert.Equal(ProcessStatus.Paused, process.Status);
        Assert.Equal(PauseReason.User, process.PauseReason);
    }

    [Fact]
    public async Task Completed_processes_are_untouched_by_recovery()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder()
            .Node("start", "start").Node("a", Action, new { key = "x" }).Node("end", "end")
            .Chain("start", "a", "end").Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        await h.RestartAsync();
        await h.IdleAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(ProcessStatus.Completed, (await h.ProcessAsync(id)).Status);
        Assert.Equal(1, h.World.Executions("x"));
        Assert.Equal(0, h.LastRecovery!.Paused);
    }
}
