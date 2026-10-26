using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Ожидания, тайм-ауты, независимые параллельные процессы, пауза и остановка.</summary>
public sealed class WaitAndConcurrencyTests
{
    private const string Action = TestActionNode.TypeName;

    private static ScenarioDefinition WaitThenAction(int seconds, string key) => new ScenarioBuilder()
        .Node("start", "start")
        .Node("w", "wait", new { mode = "duration", amount = seconds, unit = "seconds" })
        .Node("a", Action, new { key })
        .Node("end", "end")
        .Chain("start", "w", "a", "end")
        .Build();

    [Fact]
    public async Task Wait_completes_only_after_its_time()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(WaitThenAction(60, "after"));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        await h.IdleAsync(TimeSpan.FromSeconds(50));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);
        Assert.Equal(0, h.World.Executions("after"));

        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(1));
        Assert.Equal(1, h.World.Executions("after"));

        var waitStep = (await h.StepsAsync(id)).Single(s => s.NodeId == "w");
        Assert.Equal(StepState.Succeeded, waitStep.State);
    }

    [Fact]
    public async Task Waiting_process_does_not_occupy_a_worker()
    {
        await using var h = await EngineHarness.CreateAsync(new EngineOptions { MaxParallelProcesses = 1 });
        var waiting = await h.RunAsync(WaitThenAction(3600, "late"));
        await h.WaitForStatusAsync(waiting, ProcessStatus.Waiting);

        var quick = await h.RunAsync(new ScenarioBuilder()
            .Node("start", "start").Node("a", Action, new { key = "quick" }).Node("end", "end")
            .Chain("start", "a", "end").Build());

        await h.WaitForStatusAsync(quick, ProcessStatus.Completed);
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(waiting)).Status);
    }

    [Fact]
    public async Task Action_timeout_gives_unknown_result_and_checks_external_system()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Block("slow");
        var scenario = new ScenarioBuilder()
            .Node("start", "start")
            .Node("a", Action, new { key = "slow" }, new NodeSettings { TimeoutSeconds = 5, RetryCount = 3 })
            .Node("end", "end")
            .Chain("start", "a", "end")
            .Build();
        h.World.ForcedVerification["slow"] = Pepa.Core.Nodes.VerificationOutcome.Inconclusive;

        var id = await h.RunAsync(scenario);
        await h.World.EnteredAsync("slow");
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention, advance: TimeSpan.FromSeconds(1));

        Assert.Equal(AttentionKind.UnknownStep, process.Attention);
        Assert.Contains("не завершилось", process.StatusMessage);
        // Повторы не применяются к неизвестному результату.
        await h.IdleAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(1, h.World.Executions("slow"));
    }

    [Fact]
    public async Task Next_time_of_day_is_today_or_tomorrow()
    {
        var zone = TimeZoneInfo.Utc;
        var now = new DateTimeOffset(2026, 3, 2, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 9, 30, 0, TimeSpan.Zero), WaitNode.NextTimeOfDay(now, "09:30", zone));
        Assert.Equal(new DateTimeOffset(2026, 3, 3, 8, 0, 0, TimeSpan.Zero), WaitNode.NextTimeOfDay(now, "08:00", zone));
        Assert.Equal(new DateTimeOffset(2026, 3, 3, 9, 0, 0, TimeSpan.Zero), WaitNode.NextTimeOfDay(now, "09:00", zone));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Instances_of_one_scenario_have_independent_variables()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Variable("name", VariableType.Text, "", isInput: true)
            .Variable("result", VariableType.Text)
            .Node("start", "start")
            .Node("a", Action, new { key = "item-{{ vars.name }}", saveTo = "result" })
            .Node("end", "end")
            .Chain("start", "a", "end")
            .Build();
        var scenarioId = await h.CreateScenarioAsync(scenario);

        var ids = new List<string>();
        foreach (var name in new[] { "один", "два", "три" })
        {
            ids.Add(await h.Engine.StartProcessAsync(scenarioId, new JsonObject { ["name"] = name }));
        }

        var results = new List<string?>();
        foreach (var id in ids)
        {
            var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
            results.Add(process.Variables["result"]?.GetValue<string>());
        }

        Assert.Equal(["item-один", "item-два", "item-три"], results);
    }

    [Fact]
    public async Task Independent_processes_run_in_parallel()
    {
        await using var h = await EngineHarness.CreateAsync(new EngineOptions { MaxParallelProcesses = 2 });
        h.World.Block("p1");
        h.World.Block("p2");
        ScenarioDefinition One(string key) => new ScenarioBuilder()
            .Node("start", "start").Node("a", Action, new { key }).Node("end", "end")
            .Chain("start", "a", "end").Build();

        var first = await h.RunAsync(One("p1"));
        var second = await h.RunAsync(One("p2"));
        await h.World.EnteredAsync("p1").WaitAsync(TimeSpan.FromSeconds(10));
        await h.World.EnteredAsync("p2").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, h.Engine.ExecutingStepCount);

        h.World.Release("p1");
        h.World.Release("p2");
        await h.WaitForStatusAsync(first, ProcessStatus.Completed);
        await h.WaitForStatusAsync(second, ProcessStatus.Completed);
    }

    [Fact]
    public async Task Failure_of_one_process_does_not_affect_another()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("bad", ActionMode.Fail);
        ScenarioDefinition One(string key) => new ScenarioBuilder()
            .Node("start", "start").Node("a", Action, new { key }).Node("b", Action, new { key = key + "-next" })
            .Node("end", "end").Chain("start", "a", "b", "end").Build();

        var bad = await h.RunAsync(One("bad"));
        var good = await h.RunAsync(One("good"));

        var failed = await h.WaitForStatusAsync(bad, ProcessStatus.NeedsAttention);
        await h.WaitForStatusAsync(good, ProcessStatus.Completed);
        Assert.Equal(AttentionKind.StepFailed, failed.Attention);
        Assert.Equal(0, h.World.Executions("bad-next"));
        Assert.Equal(1, h.World.Executions("good-next"));
    }

    [Fact]
    public async Task Pause_during_action_takes_effect_after_the_step()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Block("first");
        var id = await h.RunAsync(new ScenarioBuilder()
            .Node("start", "start").Node("a", Action, new { key = "first" }).Node("b", Action, new { key = "second" })
            .Node("end", "end").Chain("start", "a", "b", "end").Build());
        await h.World.EnteredAsync("first").WaitAsync(TimeSpan.FromSeconds(10));

        var result = await h.Engine.PauseAsync(id);
        Assert.Contains("после завершения текущего шага", result.Message);
        h.World.Release("first");

        var paused = await h.WaitForStatusAsync(id, ProcessStatus.Paused);
        Assert.Equal(PauseReason.User, paused.PauseReason);
        await h.IdleAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, h.World.Executions("second"));

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("first"));
        Assert.Equal(1, h.World.Executions("second"));
    }

    [Fact]
    public async Task Paused_waiting_process_does_not_wake_until_resumed()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(WaitThenAction(10, "after"));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await h.Engine.PauseAsync(id);
        await h.WaitForStatusAsync(id, ProcessStatus.Paused);

        await h.IdleAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(0, h.World.Executions("after"));

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(1));
        Assert.Equal(1, h.World.Executions("after"));
    }

    [Fact]
    public async Task Cancel_while_waiting_stops_process()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(WaitThenAction(30, "never"));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        await h.Engine.CancelAsync(id);
        await h.WaitForStatusAsync(id, ProcessStatus.Cancelled);
        await h.IdleAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(0, h.World.Executions("never"));
        var waitStep = (await h.StepsAsync(id)).Single(s => s.NodeId == "w");
        Assert.Equal(StepState.Cancelled, waitStep.State);
        Assert.All(await h.Store.GetTokensAsync(id), t => Assert.Equal(TokenState.Completed, t.State));
    }

    [Fact]
    public async Task Cancel_during_action_interrupts_it_and_never_continues()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Block("long");
        h.World.ForcedVerification["long"] = Pepa.Core.Nodes.VerificationOutcome.Inconclusive;
        var id = await h.RunAsync(new ScenarioBuilder()
            .Node("start", "start").Node("a", Action, new { key = "long" }).Node("b", Action, new { key = "next" })
            .Node("end", "end").Chain("start", "a", "b", "end").Build());
        await h.World.EnteredAsync("long").WaitAsync(TimeSpan.FromSeconds(10));

        await h.Engine.CancelAsync(id);
        await h.WaitForStatusAsync(id, ProcessStatus.Cancelled);

        Assert.Equal(0, h.World.Executions("next"));
        var step = (await h.StepsAsync(id)).Single(s => s.NodeId == "a");
        Assert.Equal(StepState.Unknown, step.State);
    }
}
