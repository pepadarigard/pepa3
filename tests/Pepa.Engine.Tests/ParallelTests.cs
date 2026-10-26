using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Параллельные ветви: разделение, одновременные ожидания, слияние, ошибки и проверка структуры.</summary>
public sealed class ParallelTests
{
    private static EventInput Reply(string key, string text) => new()
    {
        Source = "manual", Type = "reply", CorrelationKey = key, Payload = new JsonObject { ["text"] = text },
    };

    /// <summary>
    /// Ветвь 1 ждёт ответ, ветвь 2 ждёт 10 минут и делает действие; после слияния — итог с данными обеих.
    /// С <paramref name="stopInBranch"/> ветвь 2 после действия заканчивает процесс с ошибкой.
    /// </summary>
    private static ScenarioDefinition ReplyAndTimer(bool stopInBranch = false) => new ScenarioBuilder("Параллельно")
        .Variable("reply", VariableType.Any).Variable("done", VariableType.Any)
        .Node("start", "start")
        .Node("split", "parallel")
        .Node("ask", "event.wait", new { source = "manual", correlationKey = "A-1", saveTo = "reply" })
        .Node("pause", "wait", new { amount = 10, unit = "minutes" })
        .Node("act", "test.action", new { key = "act", saveTo = "done" })
        .Node("check", "condition", new { expression = stopInBranch ? "true" : "false" })
        .Node("join", "join")
        .Node("end", "end", new { message = "Ответ: {{ vars.reply.payload.text }}; действие: {{ vars.done }}" })
        .Node("stop", "end", new { outcome = "failure", message = "Остановлено во второй ветви" })
        .Edge("start", "split").Edge("split", "ask", "b1").Edge("split", "pause", "b2")
        .Edge("ask", "join", Ports.Event).Edge("pause", "act").Edge("act", "check")
        .Edge("check", "stop", Ports.True).Edge("check", "join", Ports.False).Edge("join", "end")
        .Build();

    private static string Describe(EngineHarness h, string id) =>
        string.Join(", ", h.Store.GetTokensAsync(id).Result.Select(t => $"{t.NodeId}:{t.State}/{t.WaitKind}"));

    [Fact]
    public async Task Branches_wait_at_the_same_time_and_join_continues_once_with_data_of_both()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(ReplyAndTimer());
        await h.WaitForAsync(id, _ => h.World.Executions("act") == 0 && h.Store.GetTokensAsync(id).Result.Count(t => t.State == TokenState.Waiting) == 2);

        // Ответ пришёл, пока вторая ветвь ещё ждёт своё время: процесс не идёт дальше слияния.
        await h.Engine.PublishEventAsync(Reply("A-1", "да"));
        await h.WaitForAsync(id, _ => h.Store.GetTokensAsync(id).Result.Any(t => t.WaitKind == WaitKind.Join));
        Assert.Equal(0, h.World.Executions("act"));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);

        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromMinutes(1));
        Assert.Equal("Ответ: да; действие: act", done.StatusMessage);
        Assert.Equal(1, h.World.Executions("act"));
        // Слияние пройдено один раз: дальше — одна ветвь; все маркеры завершены.
        Assert.Single(await h.StepsAsync(id), s => s.NodeId == "end");
        Assert.All(await h.Store.GetTokensAsync(id), t => Assert.Equal(TokenState.Completed, t.State));
        var journal = await h.Store.GetJournalAsync(id);
        Assert.Contains(journal, j => j.Message.Contains("разделился на параллельные ветви: 2", StringComparison.Ordinal));
        Assert.Contains(journal, j => j.Message.Contains("Все ветви (2) пришли", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Branch_waits_survive_restart_and_continue_only_after_resume()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(ReplyAndTimer());
        await h.WaitForAsync(id, _ => h.Store.GetTokensAsync(id).Result.Count(t => t.State == TokenState.Waiting) == 2);
        await h.Engine.PublishEventAsync(Reply("A-1", "да"));
        await h.WaitForAsync(id, _ => h.Store.GetTokensAsync(id).Result.Any(t => t.WaitKind == WaitKind.Join));

        await h.RestartAsync(whileClosed: () => h.Time.Advance(TimeSpan.FromMinutes(30)));
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(id)).Status);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.World.Executions("act"));

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(5));
        Assert.Equal("Ответ: да; действие: act", done.StatusMessage);
    }

    [Fact]
    public async Task Failure_in_one_branch_stops_the_process_and_retry_continues_both()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("act", ActionMode.Fail);
        var id = await h.RunAsync(ReplyAndTimer());
        await h.WaitForAsync(id, _ => Describe(h, id).Contains("ask:Waiting", StringComparison.Ordinal) && Describe(h, id).Contains("pause:Waiting", StringComparison.Ordinal));
        var failed = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention, advance: TimeSpan.FromMinutes(1));
        Assert.Equal(AttentionKind.StepFailed, failed.Attention);

        // Ответ, пришедший, пока процесс ждёт решения, не теряется: ветвь получит его, когда процесс продолжится.
        await h.Engine.PublishEventAsync(Reply("A-1", "позже"));
        await h.IdleAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessStatus.NeedsAttention, (await h.ProcessAsync(id)).Status);

        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(1));
        Assert.Equal("Ответ: позже; действие: act", done.StatusMessage);
        // Повторён шаг упавшей ветви, а не ожидание другой.
        Assert.Contains(await h.Store.GetJournalAsync(id), j => j.Message.Contains("«Тестовое действие» будет выполнен ещё раз", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ending_in_one_branch_finishes_the_process_and_drops_the_other_wait()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(ReplyAndTimer(stopInBranch: true));
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Failed, advance: TimeSpan.FromMinutes(1));
        Assert.Equal("Остановлено во второй ветви", done.StatusMessage);
        Assert.All(await h.Store.GetTokensAsync(id), t => Assert.Equal(TokenState.Completed, t.State));
        Assert.All(await h.Store.GetSubscriptionsAsync(id), s => Assert.Equal(SubscriptionState.Cancelled, s.State));
        var ask = (await h.StepsAsync(id)).Single(s => s.NodeId == "ask");
        Assert.Equal(StepState.Cancelled, ask.State);

        // Ответ после завершения процесса никому не достаётся.
        var late = await h.Engine.PublishEventAsync(Reply("A-1", "поздно"));
        await h.IdleAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(EventState.Unmatched, (await h.Store.GetEventAsync(late.EventId))!.State);
    }

    [Fact]
    public async Task Ending_in_one_branch_stops_the_scenario_called_in_the_other()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(new ScenarioBuilder("Дочерний")
            .Node("start", "start")
            .Node("wait", "wait", new { amount = 30, unit = "minutes" })
            .Node("act", "test.action", new { key = "child-act" })
            .Node("end", "end")
            .Chain("start", "wait", "act", "end").Build());
        var parent = await h.RunAsync(new ScenarioBuilder("Родитель")
            .Node("start", "start")
            .Node("split", "parallel")
            .Node("pause", "wait", new { amount = 1, unit = "minutes" })
            .Node("stop", "end", new { message = "закончили в ветви 1" })
            .Node("check", "condition", new { expression = "true" })
            .Node("call", "scenario.call", new { scenario = child })
            .Node("join", "join")
            .Node("end", "end")
            .Edge("start", "split").Edge("split", "pause", "b1").Edge("split", "call", "b2")
            .Edge("pause", "check").Edge("check", "stop", Ports.True).Edge("check", "join", Ports.False).Edge("call", "join", Ports.Done)
            .Edge("join", "end")
            .Build());

        await h.WaitForAsync(parent, _ => Describe(h, parent).Contains("call:Waiting/Event", StringComparison.Ordinal));
        await h.WaitForStatusAsync(parent, ProcessStatus.Completed, advance: TimeSpan.FromSeconds(10));
        var called = (await h.Store.ListProcessesAsync(new ProcessQuery { Limit = 100 })).Single(p => p.ParentProcessId == parent);

        // Вызванный сценарий — часть процесса: он останавливается и своё действие уже не выполнит.
        await h.WaitForStatusAsync(called.Id, ProcessStatus.Cancelled);
        await h.IdleAsync(TimeSpan.FromMinutes(40), ticks: 40);
        Assert.Equal(0, h.World.Executions("child-act"));
        Assert.Contains(await h.Store.GetJournalAsync(parent), j => j.Message.Contains("Вызванный сценарий", StringComparison.Ordinal));
    }

    private static ScenarioDefinition Holder() => new ScenarioBuilder("Держатель")
        .Node("start", "start")
        .Node("lock", "resource.acquire", new { key = "R" })
        .Node("hold", "event.wait", new { source = "manual", correlationKey = "go" })
        .Node("free", "resource.release", new { key = "R" })
        .Node("end", "end")
        .Edge("start", "lock").Edge("lock", "hold").Edge("hold", "free", Ports.Event).Edge("free", "end").Build();

    private static ScenarioDefinition Queued(string name) => new ScenarioBuilder(name)
        .Node("start", "start")
        .Node("lock", "resource.acquire", new { key = "R" })
        .Node("act", "test.action", new { key = name })
        .Node("end", "end")
        .Chain("start", "lock", "act", "end").Build();

    [Fact]
    public async Task Resource_goes_to_the_first_in_queue_even_while_its_other_branch_is_working()
    {
        await using var h = await EngineHarness.CreateAsync();
        var holder = await h.RunAsync(Holder());
        await h.WaitForAsync(holder, _ => Describe(h, holder).Contains("hold:Waiting/Event", StringComparison.Ordinal));

        h.World.Block("slow");
        var first = await h.RunAsync(new ScenarioBuilder("Первый")
            .Node("start", "start")
            .Node("split", "parallel")
            .Node("lock", "resource.acquire", new { key = "R" })
            .Node("use", "test.action", new { key = "first-use" })
            .Node("pre", "wait", new { amount = 5, unit = "seconds" })
            .Node("slow", "test.action", new { key = "slow" })
            .Node("join", "join")
            .Node("end", "end")
            .Edge("start", "split").Edge("split", "lock", "b1").Edge("split", "pre", "b2")
            .Edge("lock", "use").Edge("use", "join").Edge("pre", "slow").Edge("slow", "join").Edge("join", "end")
            .Build());
        await h.WaitForAsync(first, _ => Describe(h, first).Contains("lock:Waiting/Resource", StringComparison.Ordinal));
        var second = await h.RunAsync(Queued("second"));
        await h.WaitForAsync(second, p => p.Status == ProcessStatus.Waiting);

        // У первого другая ветвь выполняет долгое действие — процесс «выполняется», а не «ждёт».
        h.Time.Advance(TimeSpan.FromSeconds(6));
        await h.World.EnteredAsync("slow");
        Assert.Equal(ProcessStatus.Running, (await h.ProcessAsync(first)).Status);

        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x", CorrelationKey = "go" });
        await h.WaitForStatusAsync(holder, ProcessStatus.Completed);
        Assert.Equal(first, (await h.Store.ListResourcesAsync()).Single(x => x.Key == "R").HolderProcessId);
        h.World.Release("slow");
        await h.WaitForStatusAsync(first, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("first-use"));
    }

    [Fact]
    public async Task Timeout_of_one_branch_keeps_the_queue_place_of_the_other()
    {
        await using var h = await EngineHarness.CreateAsync();
        var holder = await h.RunAsync(Holder());
        await h.WaitForAsync(holder, _ => Describe(h, holder).Contains("hold:Waiting/Event", StringComparison.Ordinal));

        var first = await h.RunAsync(new ScenarioBuilder("Первый")
            .Node("start", "start")
            .Node("split", "parallel")
            .Node("l1", "resource.acquire", new { key = "R", timeoutSeconds = 60 })
            .Node("l2", "resource.acquire", new { key = "R" })
            .Node("use", "test.action", new { key = "first-use" })
            .Node("join", "join")
            .Node("end", "end")
            .Edge("start", "split").Edge("split", "l1", "b1").Edge("split", "l2", "b2")
            .Edge("l1", "join").Edge("l1", "join", Ports.Timeout).Edge("l2", "use").Edge("use", "join").Edge("join", "end")
            .Build());
        await h.WaitForAsync(first, _ => h.Store.GetTokensAsync(first).Result.Count(t => t.WaitKind == WaitKind.Resource) == 2);
        var second = await h.RunAsync(Queued("second"));
        await h.WaitForAsync(second, p => p.Status == ProcessStatus.Waiting);

        // Ветвь 1 не дождалась ресурса — место ветви 2 в очереди остаётся.
        await h.WaitForAsync(first, _ => Describe(h, first).Contains("join:Waiting/Join", StringComparison.Ordinal), advance: TimeSpan.FromSeconds(10));
        h.World.Block("first-use");
        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x", CorrelationKey = "go" });
        await h.World.EnteredAsync("first-use");
        Assert.Equal(first, (await h.Store.ListResourcesAsync()).Single(x => x.Key == "R").HolderProcessId);
        Assert.Equal(0, h.World.Executions("second"));
        h.World.Release("first-use");
        await h.WaitForStatusAsync(first, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("first-use"));
    }

    [Fact]
    public async Task Nested_parallel_sections_join_in_order()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Вложенные")
            .Node("start", "start")
            .Node("outer", "parallel")
            .Node("inner", "parallel")
            .Node("a", "test.action", new { key = "a" })
            .Node("b", "test.action", new { key = "b" })
            .Node("innerJoin", "join")
            .Node("c", "test.action", new { key = "c" })
            .Node("outerJoin", "join")
            .Node("end", "end", new { message = "готово" })
            .Edge("start", "outer").Edge("outer", "inner", "b1").Edge("outer", "c", "b2")
            .Edge("inner", "a", "b1").Edge("inner", "b", "b2").Edge("a", "innerJoin").Edge("b", "innerJoin")
            .Edge("innerJoin", "outerJoin").Edge("c", "outerJoin").Edge("outerJoin", "end")
            .Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal((1, 1, 1), (h.World.Executions("a"), h.World.Executions("b"), h.World.Executions("c")));
        Assert.Single(await h.StepsAsync(id), s => s.NodeId == "end");
    }

    [Fact]
    public async Task Parallel_structure_is_checked_before_running()
    {
        await using var h = await EngineHarness.CreateAsync();
        string[] Codes(ScenarioDefinition scenario) => h.Engine.Validator.Validate(scenario).Issues
            .Where(i => i.Severity == Core.Validation.IssueSeverity.Error).Select(i => i.Code).ToArray();

        Assert.Empty(Codes(ReplyAndTimer()));

        // Ветвь не приходит к слиянию.
        Assert.Contains("parallel.branch.open", Codes(new ScenarioBuilder("Открытая ветвь")
            .Node("start", "start").Node("split", "parallel").Node("a", "log", new { message = "a" }).Node("b", "log", new { message = "b" })
            .Node("join", "join").Node("end", "end").Node("loop", "log", new { message = "loop" })
            .Edge("start", "split").Edge("split", "a", "b1").Edge("split", "b", "b2").Edge("a", "join").Edge("b", "loop").Edge("loop", "b")
            .Edge("join", "end").Build()));

        // Ветви сливаются раньше слияния.
        Assert.Contains("parallel.merge.early", Codes(new ScenarioBuilder("Раннее слияние")
            .Node("start", "start").Node("split", "parallel").Node("a", "log", new { message = "a" }).Node("b", "log", new { message = "b" })
            .Node("common", "log", new { message = "c" }).Node("join", "join").Node("end", "end")
            .Edge("start", "split").Edge("split", "a", "b1").Edge("split", "b", "b2").Edge("a", "common").Edge("b", "common")
            .Edge("common", "join").Edge("join", "end").Build()));

        // Вход в середину ветви снаружи и слияние без разделения.
        Assert.Contains("parallel.enter", Codes(new ScenarioBuilder("Вход снаружи")
            .Node("start", "start").Node("check", "condition", new { expression = "true" }).Node("split", "parallel")
            .Node("a", "log", new { message = "a" }).Node("b", "log", new { message = "b" }).Node("join", "join").Node("end", "end")
            .Edge("start", "check").Edge("check", "split", Ports.True).Edge("check", "b", Ports.False)
            .Edge("split", "a", "b1").Edge("split", "b", "b2").Edge("a", "join").Edge("b", "join").Edge("join", "end").Build()));
        Assert.Contains("parallel.join.orphan", Codes(new ScenarioBuilder("Слияние без разделения")
            .Node("start", "start").Node("join", "join").Node("end", "end").Edge("start", "join").Edge("join", "end").Build()));

        // Общий блок «Завершение» для ошибок ветви и основной схемы — допустим.
        Assert.Empty(Codes(new ScenarioBuilder("Общий конец")
            .Node("start", "start").Node("check", "condition", new { expression = "true" }).Node("split", "parallel")
            .Node("a", "test.action", new { key = "a" }).Node("b", "log", new { message = "b" }).Node("join", "join")
            .Node("end", "end").Node("fail", "end", new { outcome = "failure" })
            .Edge("start", "check").Edge("check", "split", Ports.True).Edge("check", "fail", Ports.False)
            .Edge("split", "a", "b1").Edge("split", "b", "b2").Edge("a", "join").Edge("a", "fail", Ports.Error).Edge("b", "join")
            .Edge("join", "end").Build()));
    }
}
