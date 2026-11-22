using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Вызов сценария из сценария и перебор списков.</summary>
public sealed class SubprocessAndLoopTests
{
    private const string Action = TestActionNode.TypeName;

    private static ScenarioDefinition Child(int waitSeconds = 0, bool fail = false)
    {
        var builder = new ScenarioBuilder("Дочерний")
            .Variable("profile", VariableType.Text, "", isInput: true)
            .Variable("result", VariableType.Text, "")
            .Node("start", "start")
            .Node("a", Action, new { key = "child-{{ vars.profile }}", saveTo = "result" })
            .Node("end", "end", fail ? new { outcome = "failure", message = "Не получилось" } : null);
        if (waitSeconds > 0)
        {
            builder.Node("w", "wait", new { mode = "duration", amount = waitSeconds, unit = "seconds" }).Chain("start", "w", "a", "end");
        }
        else
        {
            builder.Chain("start", "a", "end");
        }

        return builder.Build();
    }

    private static ScenarioDefinition Parent(string childId, int? timeoutSeconds = null, bool wait = true)
    {
        var builder = new ScenarioBuilder("Родитель")
            .Variable("childResult", VariableType.Any)
            .Node("start", "start")
            .Node("call", wait ? "scenario.call" : "scenario.run",
                wait
                    ? new { scenario = childId, inputs = "({ profile: \"p1\" })", saveTo = "childResult", timeoutSeconds }
                    : (object)new { scenario = childId, inputs = "({ profile: \"p1\" })", saveTo = "childResult" })
            .Node("after", Action, new { key = "parent-after" })
            .Node("end", "end")
            .Edge("start", "call")
            .Edge("call", "after", wait ? Ports.Done : Ports.Out)
            .Edge("after", "end");
        if (wait)
        {
            builder.Node("failed", "end", new { outcome = "failure", message = "Дочерний не справился" }).Edge("call", "failed", Ports.Error);
            builder.Node("slow", "end", new { outcome = "failure", message = "Дочерний слишком долго" }).Edge("call", "slow", Ports.Timeout);
        }

        return builder.Build();
    }

    private static async Task<ProcessRecord> ChildOfAsync(EngineHarness h, string parentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var children = await h.Store.GetChildProcessesAsync(parentId);
            if (children.Count > 0)
            {
                return children[0];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Дочерний процесс не появился.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Call_waits_for_child_and_receives_its_variables()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(Child());
        var parent = await h.RunAsync(Parent(child));

        var done = await h.WaitForStatusAsync(parent, ProcessStatus.Completed);
        Assert.Equal("child-p1", done.Variables["childResult"]?["result"]?.GetValue<string>());
        var childProcess = await ChildOfAsync(h, parent);
        Assert.Equal(ProcessStatus.Completed, childProcess.Status);
        Assert.Equal(1, childProcess.Depth);
        Assert.Equal(1, h.World.Executions("child-p1"));
        Assert.Equal(1, h.World.Executions("parent-after"));
    }

    [Fact]
    public async Task Failed_child_sends_parent_to_error_branch()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(Child(fail: true));
        var parent = await h.RunAsync(Parent(child));

        var failed = await h.WaitForStatusAsync(parent, ProcessStatus.Failed);
        Assert.Equal("Дочерний не справился", failed.StatusMessage);
        Assert.Contains("Не получилось", failed.LastError?["message"]?.GetValue<string>());
        Assert.Equal(0, h.World.Executions("parent-after"));
    }

    [Fact]
    public async Task Cancelling_parent_stops_called_child_but_not_started_one()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(Child(waitSeconds: 3600));
        var calling = await h.RunAsync(Parent(child));
        var starting = await h.RunAsync(Parent(child, wait: false));

        var called = await ChildOfAsync(h, calling);
        var started = await ChildOfAsync(h, starting);
        await h.WaitForStatusAsync(called.Id, ProcessStatus.Waiting);
        // Запущенный без ожидания тоже должен дойти до своего ожидания — иначе проверка ниже зависела бы от скорости машины.
        await h.WaitForStatusAsync(started.Id, ProcessStatus.Waiting);
        await h.WaitForStatusAsync(starting, ProcessStatus.Completed);
        Assert.Equal(started.Id, (await h.ProcessAsync(starting)).Variables["childResult"]?.GetValue<string>());

        await h.Engine.CancelAsync(calling);
        await h.WaitForStatusAsync(called.Id, ProcessStatus.Cancelled);
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(started.Id)).Status);
    }

    [Fact]
    public async Task Child_timeout_stops_child_and_follows_timeout_branch()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(Child(waitSeconds: 3600));
        var parent = await h.RunAsync(Parent(child, timeoutSeconds: 120));
        var childProcess = await ChildOfAsync(h, parent);

        var failed = await h.WaitForStatusAsync(parent, ProcessStatus.Failed, advance: TimeSpan.FromSeconds(10));
        Assert.Equal("Дочерний слишком долго", failed.StatusMessage);
        await h.WaitForStatusAsync(childProcess.Id, ProcessStatus.Cancelled);
        Assert.Equal(0, h.World.Executions("child-p1"));
    }

    [Fact]
    public async Task Paused_parent_gets_child_result_only_after_resume()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(Child(waitSeconds: 60));
        var parent = await h.RunAsync(Parent(child));
        var childProcess = await ChildOfAsync(h, parent);
        await h.WaitForStatusAsync(parent, ProcessStatus.Waiting);
        await h.Engine.PauseAsync(parent);

        await h.WaitForStatusAsync(childProcess.Id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(5));
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(parent)).Status);
        Assert.Equal(0, h.World.Executions("parent-after"));

        await h.Engine.ResumeAsync(parent, ResumeAction.Continue);
        await h.WaitForStatusAsync(parent, ProcessStatus.Completed);
    }

    [Fact]
    public async Task Both_parent_and_child_pause_on_restart_and_finish_after_resume()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(Child(waitSeconds: 60));
        var parent = await h.RunAsync(Parent(child));
        var childProcess = await ChildOfAsync(h, parent);
        await h.WaitForStatusAsync(childProcess.Id, ProcessStatus.Waiting);
        await h.WaitForStatusAsync(parent, ProcessStatus.Waiting);

        await h.RestartAsync();
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(parent)).Status);
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(childProcess.Id)).Status);
        await h.IdleAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(0, h.World.Executions("child-p1"));

        await h.Engine.ResumeAsync(childProcess.Id, ResumeAction.Continue);
        await h.WaitForStatusAsync(childProcess.Id, ProcessStatus.Completed);
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(parent)).Status);
        await h.Engine.ResumeAsync(parent, ResumeAction.Continue);
        await h.WaitForStatusAsync(parent, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("child-p1"));
    }

    [Fact]
    public async Task Scenario_calling_itself_stops_at_depth_limit()
    {
        await using var h = await EngineHarness.CreateAsync();
        var created = await h.Scenarios.CreateAsync(Child());
        var recursive = new ScenarioBuilder("Сам себя")
            .Node("start", "start")
            .Node("call", "scenario.call", new { scenario = created.Id })
            .Node("end", "end")
            .Edge("start", "call").Edge("call", "end", Ports.Done)
            .Build();
        await h.Scenarios.UpdateAsync(created.Id, recursive, created.Version);

        var root = await h.Engine.StartProcessAsync(created.Id);
        ProcessRecord? last = null;
        // Девять вложенных процессов — около 26 записей на диск подряд: на загруженном раннере Windows (>0,3 с на запись) 15 с впритык.
        await h.UntilAsync(async () =>
                (last = (await h.Store.ListProcessesAsync(new ProcessQuery { Statuses = [ProcessStatus.NeedsAttention] })).SingleOrDefault()) is not null,
            "процесс на пределе вложенности", TimeSpan.FromSeconds(60));
        Assert.NotNull(last);
        Assert.Equal(8, last.Depth);
        Assert.Equal(9, (await h.Store.ListProcessesAsync(new ProcessQuery())).Count);
        Assert.Equal(0, (await h.ProcessAsync(root)).Depth);
        Assert.Contains("вложенность", last.StatusMessage);
    }

    [Fact]
    public async Task ForEach_runs_body_for_every_item_once()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Перебор")
            .Variable("item", VariableType.Any).Variable("i", VariableType.Number, 0)
            .Node("start", "start")
            .Node("loop", "loop.forEach", new { items = "[\"a\", \"b\", \"c\"]", itemVariable = "item", indexVariable = "i" })
            .Node("a", Action, new { key = "item-{{ vars.i }}-{{ vars.item }}" })
            .Node("end", "end")
            .Edge("start", "loop").Edge("loop", "a", Ports.Item).Edge("a", "loop").Edge("loop", "end", Ports.Done)
            .Build());

        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("item-0-a"));
        Assert.Equal(1, h.World.Executions("item-1-b"));
        Assert.Equal(1, h.World.Executions("item-2-c"));
        Assert.Null(done.Memory);
    }

    [Fact]
    public async Task ForEach_over_empty_list_goes_straight_to_done()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Пусто")
            .Variable("item", VariableType.Any)
            .Node("start", "start")
            .Node("loop", "loop.forEach", new { items = "[]", itemVariable = "item" })
            .Node("a", Action, new { key = "never" })
            .Node("end", "end")
            .Edge("start", "loop").Edge("loop", "a", Ports.Item).Edge("a", "loop").Edge("loop", "end", Ports.Done)
            .Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(0, h.World.Executions("never"));
    }

    [Fact]
    public async Task Nested_loop_restarts_for_each_outer_item_even_after_early_exit()
    {
        await using var h = await EngineHarness.CreateAsync();
        // Внешний: [1, 2]; внутренний: ["x", "y", "z"], но при "y" — выход из внутреннего цикла сразу во внешний.
        var id = await h.RunAsync(new ScenarioBuilder("Вложенный")
            .Variable("outer", VariableType.Any).Variable("inner", VariableType.Any)
            .Node("start", "start")
            .Node("o", "loop.forEach", new { items = "[1, 2]", itemVariable = "outer" })
            .Node("i", "loop.forEach", new { items = "[\"x\", \"y\", \"z\"]", itemVariable = "inner" })
            .Node("a", Action, new { key = "{{ vars.outer }}{{ vars.inner }}" })
            .Node("stop", "condition", new { expression = "vars.inner === \"y\"" })
            .Node("end", "end")
            .Edge("start", "o")
            .Edge("o", "i", Ports.Item)
            .Edge("i", "a", Ports.Item)
            .Edge("a", "stop")
            .Edge("stop", "o", Ports.True)
            .Edge("stop", "i", Ports.False)
            .Edge("i", "o", Ports.Done)
            .Edge("o", "end", Ports.Done)
            .Build());

        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("1x"));
        Assert.Equal(1, h.World.Executions("1y"));
        Assert.Equal(0, h.World.Executions("1z"));
        Assert.Equal(1, h.World.Executions("2x"));
        Assert.Equal(1, h.World.Executions("2y"));
    }

    [Fact]
    public async Task ForEach_position_survives_restart_without_repeating_items()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Перебор с паузой")
            .Variable("item", VariableType.Any)
            .Node("start", "start")
            .Node("loop", "loop.forEach", new { items = "[1, 2, 3]", itemVariable = "item" })
            .Node("a", Action, new { key = "n{{ vars.item }}" })
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            .Node("end", "end")
            .Edge("start", "loop").Edge("loop", "a", Ports.Item).Edge("a", "w").Edge("w", "loop").Edge("loop", "end", Ports.Done)
            .Build());
        await h.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && h.World.Executions("n1") == 1);

        await h.RestartAsync();
        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.World.Executions("n1"));
        Assert.Equal(1, h.World.Executions("n2"));
        Assert.Equal(1, h.World.Executions("n3"));
    }

    [Fact]
    public async Task ForEach_rejects_non_list()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Не список")
            .Variable("item", VariableType.Any)
            .Node("start", "start")
            .Node("loop", "loop.forEach", new { items = "\"abc\"", itemVariable = "item" })
            .Node("end", "end")
            .Edge("start", "loop").Edge("loop", "end", Ports.Item).Edge("loop", "end", Ports.Done)
            .Build());
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("нужен список", process.StatusMessage);
    }

    [Fact]
    public async Task ForEach_with_pause_before_each_item_runs_every_item_once()
    {
        // Частая схема «пауза перед каждым элементом»: тело возвращается через блок, которым процесс вошёл в цикл.
        // Раньше такой возврат считался новым входом, и первый элемент повторялся до исчерпания лимита шагов.
        await using var h = await EngineHarness.CreateAsync(new EngineOptions { DefaultStepBudget = 60 });
        var id = await h.RunAsync(new ScenarioBuilder("Пауза перед каждым")
            .Variable("item", VariableType.Any)
            .Node("start", "start")
            .Node("w", "wait", new { mode = "duration", amount = 1, unit = "seconds" })
            .Node("loop", "loop.forEach", new { items = "[\"a\", \"b\", \"c\"]", itemVariable = "item" })
            .Node("send", Action, new { key = "send-{{ vars.item }}" })
            .Node("end", "end")
            .Edge("start", "w").Edge("w", "loop").Edge("loop", "send", Ports.Item).Edge("send", "w").Edge("loop", "end", Ports.Done)
            .Build());

        await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(1));
        Assert.Equal(1, h.World.Executions("send-a"));
        Assert.Equal(1, h.World.Executions("send-b"));
        Assert.Equal(1, h.World.Executions("send-c"));
    }

    [Fact]
    public async Task ForEach_with_check_before_each_item_does_not_repeat_items()
    {
        await using var h = await EngineHarness.CreateAsync(new EngineOptions { DefaultStepBudget = 60 });
        var id = await h.RunAsync(new ScenarioBuilder("Проверка перед каждым")
            .Variable("item", VariableType.Any)
            .Node("start", "start")
            .Node("check", "condition", new { expression = "vars.item !== 99" })
            .Node("loop", "loop.forEach", new { items = "[1, 2, 3]", itemVariable = "item" })
            .Node("a", Action, new { key = "n{{ vars.item }}" })
            .Node("end", "end")
            .Edge("start", "check").Edge("check", "loop", Ports.True).Edge("check", "end", Ports.False)
            .Edge("loop", "a", Ports.Item).Edge("a", "check").Edge("loop", "end", Ports.Done)
            .Build());

        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("n1"));
        Assert.Equal(1, h.World.Executions("n2"));
        Assert.Equal(1, h.World.Executions("n3"));
    }

    [Fact]
    public async Task ForEach_stores_the_list_once_not_in_every_step()
    {
        // Перебор длинного списка (строки таблицы) не должен сохранять весь список в каждом шаге: хранилище росло бы
        // квадратично (400 строк по 1 КБ давали 156 МБ параметров шагов).
        await using var h = await EngineHarness.CreateAsync();
        var row = new string('x', 1000);
        var id = await h.RunAsync(new ScenarioBuilder("Длинный список")
            .Variable("item", VariableType.Any)
            .Node("start", "start")
            .Node("loop", "loop.forEach", new { items = $"Array.from({{ length: 60 }}, (_, i) => i + '{row}')", itemVariable = "item" })
            .Node("log", "log", new { message = "строка" })
            .Node("end", "end")
            .Edge("start", "loop").Edge("loop", "log", Ports.Item).Edge("log", "loop").Edge("loop", "end", Ports.Done)
            .Build());

        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        var loopSteps = (await h.StepsAsync(id)).Where(s => s.NodeId == "loop").ToList();
        Assert.Equal(61, loopSteps.Count);
        var stored = loopSteps.Sum(s => s.Input?.ToJsonString().Length ?? 0);
        Assert.InRange(stored, 60 * 1000, 2 * 60 * 1000);
    }

    [Fact]
    public async Task ForEach_list_is_evaluated_once_even_if_the_body_changes_its_variable()
    {
        // Подсказка поля обещает: «Список вычисляется один раз при входе в цикл».
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Тело меняет список")
            .Variable("data", VariableType.Any, JsonNode.Parse("""{ "rows": [1, 2, 3] }"""))
            .Variable("item", VariableType.Any)
            .Node("start", "start")
            .Node("loop", "loop.forEach", new { items = "vars.data.rows", itemVariable = "item" })
            .Node("a", Action, new { key = "row{{ vars.item }}" })
            .Node("clear", "variable.set", new { variable = "data", value = "null" })
            .Node("end", "end")
            .Edge("start", "loop").Edge("loop", "a", Ports.Item).Edge("a", "clear").Edge("clear", "loop").Edge("loop", "end", Ports.Done)
            .Build());

        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("row1"));
        Assert.Equal(1, h.World.Executions("row2"));
        Assert.Equal(1, h.World.Executions("row3"));
    }
}
