using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Отладка: «Пауза перед блоком» и пошаговый запуск.</summary>
public sealed class DebugTests
{
    private static ScenarioDefinition ThreeSteps(bool breakpointOnSecond) => new ScenarioBuilder("Три шага")
        .Node("start", "start")
        .Node("a", TestActionNode.TypeName, new { key = "a" })
        .Node("b", TestActionNode.TypeName, new { key = "b" }, new NodeSettings { Breakpoint = breakpointOnSecond })
        .Node("c", TestActionNode.TypeName, new { key = "c" })
        .Node("end", "end")
        .Chain("start", "a", "b", "c", "end")
        .Build();

    [Fact]
    public async Task Breakpoint_pauses_before_the_block_and_continue_runs_it()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(ThreeSteps(breakpointOnSecond: true));
        var paused = await h.WaitForStatusAsync(id, ProcessStatus.Paused);
        Assert.Equal(PauseReason.Breakpoint, paused.PauseReason);
        Assert.Contains("Пауза перед блоком", paused.StatusMessage);
        Assert.Equal(1, h.World.Executions("a"));
        Assert.Equal(0, h.World.Executions("b"));
        Assert.Contains(ProcessAction.RunWithoutPauses, h.Engine.GetAllowedActions(paused, await h.Store.GetTokensAsync(id)));

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("b"));
        Assert.Equal(1, h.World.Executions("c"));
    }

    [Fact]
    public async Task Step_mode_pauses_before_every_block_until_run_without_pauses()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(ThreeSteps(breakpointOnSecond: true));
        var id = await h.Engine.StartProcessAsync(scenario, null, stepMode: true);

        await h.WaitForStatusAsync(id, ProcessStatus.Paused);
        Assert.Equal(0, h.World.Executions("a"));
        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForAsync(id, p => p.Status == ProcessStatus.Paused && h.World.Executions("a") == 1);
        Assert.Equal(0, h.World.Executions("b"));

        // «Продолжить без остановок»: ни пошагового режима, ни точки останова на «b».
        await h.Engine.ResumeAsync(id, ResumeAction.RunWithoutPauses);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("b"));
        Assert.Equal(1, h.World.Executions("c"));
    }

    [Fact]
    public async Task Breakpoint_survives_restart_and_retry_after_failure_does_not_pause_again()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("flaky", ActionMode.Fail);
        var id = await h.RunAsync(new ScenarioBuilder("С повтором")
            .Node("start", "start")
            .Node("a", TestActionNode.TypeName, new { key = "flaky" },
                new NodeSettings { Breakpoint = true, RetryCount = 1, RetryDelaySeconds = 1 })
            .Node("end", "end")
            .Chain("start", "a", "end").Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Paused);

        await h.RestartAsync();
        var afterRestart = await h.ProcessAsync(id);
        Assert.Equal(ProcessStatus.Paused, afterRestart.Status);
        Assert.Equal(PauseReason.Breakpoint, afterRestart.PauseReason);

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        // Первая попытка падает, повтор через секунду идёт без новой паузы.
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(2));
        Assert.Equal(2, h.World.Executions("flaky"));
    }

    [Fact]
    public async Task Loop_pauses_on_every_visit_of_the_block()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Цикл")
            .Variable("items", VariableType.List, new System.Text.Json.Nodes.JsonArray(1, 2))
            .Variable("item", VariableType.Any)
            .Node("start", "start")
            .Node("each", "loop.forEach", new { items = "vars.items", itemVariable = "item" })
            .Node("act", TestActionNode.TypeName, new { key = "visit" }, new NodeSettings { Breakpoint = true })
            .Node("end", "end")
            .Edge("start", "each").Edge("each", "act", Ports.Item).Edge("act", "each").Edge("each", "end", Ports.Done)
            .Build());
        for (var visit = 0; visit < 2; visit++)
        {
            await h.WaitForAsync(id, p => p.Status == ProcessStatus.Paused && h.World.Executions("visit") == visit);
            await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        }

        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(2, h.World.Executions("visit"));
    }
}
