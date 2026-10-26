using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Последовательные шаги, условия, циклы, переменные.</summary>
public sealed class ExecutionTests
{
    private const string Action = TestActionNode.TypeName;

    [Fact]
    public async Task Sequential_steps_run_in_order_once_each()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Node("start", "start")
            .Node("a", Action, new { key = "a" })
            .Node("b", Action, new { key = "b" })
            .Node("end", "end")
            .Chain("start", "a", "b", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal(1, h.World.Executions("a"));
        Assert.Equal(1, h.World.Executions("b"));
        var steps = await h.StepsAsync(id);
        Assert.Equal(["start", "a", "b", "end"], steps.Select(s => s.NodeId));
        Assert.All(steps, s => Assert.Equal(StepState.Succeeded, s.State));
        Assert.Equal(4, process.StepCount);
        Assert.NotNull(process.FinishedAt);
    }

    [Theory]
    [InlineData(true, "yes")]
    [InlineData(false, "no")]
    public async Task Condition_routes_by_variable(bool flag, string expectedBranch)
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Variable("flag", VariableType.Boolean, false, isInput: true)
            .Node("start", "start")
            .Node("check", "condition", new { expression = "vars.flag === true" })
            .Node("yes", Action, new { key = "yes" })
            .Node("no", Action, new { key = "no" })
            .Node("end", "end")
            .Edge("start", "check")
            .Edge("check", "yes", Ports.True)
            .Edge("check", "no", Ports.False)
            .Edge("yes", "end")
            .Edge("no", "end")
            .Build();

        var id = await h.RunAsync(scenario, new JsonObject { ["flag"] = flag });
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal(1, h.World.Executions(expectedBranch));
        Assert.Equal(0, h.World.Executions(flag ? "no" : "yes"));
    }

    [Fact]
    public async Task Loop_with_counter_runs_body_expected_number_of_times()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Variable("i", VariableType.Number, 1)
            .Node("start", "start")
            .Node("check", "condition", new { expression = "vars.i <= 3" })
            .Node("body", Action, new { key = "item-{{ vars.i }}" })
            .Node("inc", "variable.set", new { variable = "i", value = "vars.i + 1" })
            .Node("end", "end")
            .Edge("start", "check")
            .Edge("check", "body", Ports.True)
            .Edge("check", "end", Ports.False)
            .Edge("body", "inc")
            .Edge("inc", "check")
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal(1, h.World.Executions("item-1"));
        Assert.Equal(1, h.World.Executions("item-2"));
        Assert.Equal(1, h.World.Executions("item-3"));
        Assert.Equal(0, h.World.Executions("item-4"));
        Assert.Equal(4, process.Variables["i"]!.GetValue<double>());
    }

    [Fact]
    public async Task Resolved_parameters_are_stored_in_step_input()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Variable("name", VariableType.Text, "мир")
            .Node("start", "start")
            .Node("a", Action, new { key = "привет-{{ vars.name }}" })
            .Node("end", "end")
            .Chain("start", "a", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        var step = (await h.StepsAsync(id)).Single(s => s.NodeId == "a");
        Assert.Equal("привет-мир", step.Input!.GetString("key"));
        Assert.Equal(1, h.World.Executions("привет-мир"));
    }

    [Fact]
    public async Task Action_output_is_saved_to_variable()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Variable("result", VariableType.Text)
            .Node("start", "start")
            .Node("a", Action, new { key = "значение", saveTo = "result" })
            .Node("end", "end")
            .Chain("start", "a", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal("значение", process.Variables.GetString("result"));
    }

    [Fact]
    public async Task Variable_type_mismatch_stops_process_with_clear_reason()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Variable("count", VariableType.Number, 0)
            .Node("start", "start")
            .Node("set", "variable.set", new { variable = "count", value = "\"не число\"" })
            .Node("end", "end")
            .Chain("start", "set", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Contains("тип «число»", process.StatusMessage);
        Assert.Equal(0, process.Variables["count"]!.GetValue<double>());
    }

    [Fact]
    public async Task Expression_error_does_not_execute_action()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Node("start", "start")
            .Node("a", Action, new { key = "{{ vars.missing.deep }}" })
            .Node("end", "end")
            .Chain("start", "a", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Empty(h.World.Effects);
        var step = (await h.StepsAsync(id)).Last();
        Assert.Equal(StepState.Failed, step.State);
    }

    [Fact]
    public async Task End_with_failure_outcome_marks_process_failed()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Node("start", "start")
            .Node("end", "end", new { outcome = "failure", message = "Нечего обрабатывать" })
            .Chain("start", "end")
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Failed);

        Assert.Equal("Нечего обрабатывать", process.StatusMessage);
    }

    [Fact]
    public async Task Inputs_are_validated_against_declared_variables()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Variable("count", VariableType.Number, 1, isInput: true)
            .Node("start", "start")
            .Node("end", "end")
            .Chain("start", "end")
            .Build();
        var scenarioId = await h.CreateScenarioAsync(scenario);

        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h.Engine.StartProcessAsync(scenarioId, new JsonObject { ["unknown"] = 1 }));
        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h.Engine.StartProcessAsync(scenarioId, new JsonObject { ["count"] = "много" }));

        var id = await h.Engine.StartProcessAsync(scenarioId, new JsonObject { ["count"] = 5 });
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(5, process.Variables["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Invalid_scenario_cannot_be_started()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder()
            .Node("start", "start")
            .Node("a", Action, new { key = "a" })
            .Chain("start", "a")
            .Build();
        var scenarioId = await h.CreateScenarioAsync(scenario);

        var error = await Assert.ThrowsAsync<ScenarioInvalidException>(() => h.Engine.StartProcessAsync(scenarioId));
        Assert.False(error.Report.IsValid);
        Assert.Empty(await h.Store.ListProcessesAsync(new ProcessQuery()));
    }

    [Fact]
    public async Task Endless_loop_stops_at_step_limit_and_can_be_continued()
    {
        await using var h = await EngineHarness.CreateAsync(new EngineOptions { DefaultStepBudget = 30 });
        var scenario = new ScenarioBuilder()
            .Variable("i", VariableType.Number, 0)
            .Node("start", "start")
            .Node("inc", "variable.set", new { variable = "i", value = "vars.i + 1" })
            .Node("check", "condition", new { expression = "vars.i < 1000000" })
            .Node("end", "end")
            .Chain("start", "inc", "check")
            .Edge("check", "inc", Ports.True)
            .Edge("check", "end", Ports.False)
            .Build();

        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepLimit, process.Attention);
        Assert.Equal(30, process.StepCount);

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        process = await h.WaitForAsync(id, p => p is { Status: ProcessStatus.NeedsAttention, StepCount: 60 });
        Assert.Equal(AttentionKind.StepLimit, process.Attention);

        await h.Engine.CancelAsync(id);
        await h.WaitForStatusAsync(id, ProcessStatus.Cancelled);
    }

    [Fact]
    public async Task Scenario_edits_do_not_affect_running_process()
    {
        await using var h = await EngineHarness.CreateAsync();
        var original = new ScenarioBuilder()
            .Node("start", "start")
            .Node("w", "wait", new { mode = "duration", amount = 10, unit = "seconds" })
            .Node("a", Action, new { key = "v1" })
            .Node("end", "end")
            .Chain("start", "w", "a", "end")
            .Build();
        var scenarioId = await h.CreateScenarioAsync(original);
        var id = await h.Engine.StartProcessAsync(scenarioId);
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        var stored = await h.Scenarios.GetAsync(scenarioId);
        var edited = ScenarioService.Parse(stored!);
        edited = edited with
        {
            Nodes = edited.Nodes.Select(n => n.Id == "a" ? n with { Config = new JsonObject { ["key"] = "v2" } } : n).ToList(),
        };
        await h.Scenarios.UpdateAsync(scenarioId, edited, stored!.Version);

        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(1));
        Assert.Equal(1, h.World.Executions("v1"));
        Assert.Equal(0, h.World.Executions("v2"));
    }
}
