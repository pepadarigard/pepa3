using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>
/// Окно «пришедшие раньше» — от того, как процесс дошёл до ожидания (отправил вопрос), а не от записи подписки: ответ, пришедший
/// между отправкой и ожиданием (пауза, перезапуск, долгий шаг соседней ветви), не теряется.
/// </summary>
public sealed class WaitWindowTests
{
    private static ScenarioDefinition AskAndWait() => new ScenarioBuilder("Вопрос и ответ")
        .Node("start", "start")
        .Node("ask", TestActionNode.TypeName, new { key = "ask" })
        .Node("wait", "event.wait", new { source = "manual", correlationKey = "chat", lookbackSeconds = 30, timeoutSeconds = 1800 })
        .Node("got", TestActionNode.TypeName, new { key = "got" })
        .Node("end", "end")
        .Node("late", "end", new { outcome = "failure", message = "Ответ не пришёл" })
        .Edge("start", "ask").Edge("ask", "wait").Edge("wait", "got", Ports.Event).Edge("wait", "late", Ports.Timeout)
        .Edge("got", "end").Build();

    [Fact]
    public async Task Reply_that_came_while_the_process_was_paused_after_asking_is_taken_on_continue()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Block("ask");
        var id = await h.RunAsync(AskAndWait());
        await h.World.EnteredAsync("ask");
        await h.Engine.PauseAsync(id);
        h.World.Release("ask");
        await h.WaitForStatusAsync(id, ProcessStatus.Paused);

        // Человек ответил, пока процесс стоял на паузе (ожидание ещё не записано).
        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Engine.ResumeAsync(id, ResumeAction.Continue);

        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("got"));
    }

    [Fact]
    public async Task Reply_that_came_while_the_other_branch_was_busy_is_taken_by_the_wait()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Block("site");
        var id = await h.RunAsync(new ScenarioBuilder("Ответ, пока работаем на сайте")
            .Node("start", "start")
            .Node("ask", TestActionNode.TypeName, new { key = "ask" })
            .Node("split", "parallel")
            .Node("wait", "event.wait", new { source = "manual", correlationKey = "chat", lookbackSeconds = 30, timeoutSeconds = 1800 })
            .Node("site", TestActionNode.TypeName, new { key = "site" })
            .Node("join", "join")
            .Node("end", "end")
            .Node("late", "end", new { outcome = "failure", message = "Ответ не пришёл" })
            .Edge("start", "ask").Edge("ask", "split").Edge("split", "wait", "b1").Edge("split", "site", "b2")
            .Edge("wait", "join", Ports.Event).Edge("wait", "late", Ports.Timeout).Edge("site", "join").Edge("join", "end")
            .Build());
        await h.World.EnteredAsync("site");

        // Ответ пришёл через 5 секунд, а долгий шаг соседней ветви идёт ещё минуту — ожидание запишется после него.
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
        h.Time.Advance(TimeSpan.FromSeconds(60));
        h.World.Release("site");

        var done = await h.WaitForAsync(id, p => p.IsTerminal);
        Assert.Equal(ProcessStatus.Completed, done.Status);
    }

    [Fact]
    public async Task Message_from_before_the_question_is_still_not_taken()
    {
        await using var h = await EngineHarness.CreateAsync();
        // Старое сообщение в том же чате — до вопроса и за пределами окна.
        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
        h.Time.Advance(TimeSpan.FromMinutes(10));
        var id = await h.RunAsync(AskAndWait());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.World.Executions("got"));
    }

    [Fact]
    public async Task Reply_received_before_the_deadline_but_not_yet_routed_beats_the_timeout()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Ответ в последний момент")
            .Node("start", "start")
            .Node("wait", "event.wait", new { source = "manual", correlationKey = "chat", timeoutSeconds = 60 })
            .Node("got", TestActionNode.TypeName, new { key = "got" })
            .Node("end", "end")
            .Node("late", "end", new { outcome = "failure", message = "Ответ не пришёл" })
            .Edge("start", "wait").Edge("wait", "got", Ports.Event).Edge("wait", "late", Ports.Timeout).Edge("got", "end").Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        // Сопоставление занято (после обрыва связи пришло много сообщений): ответ принят до срока, но ещё не разобран.
        var routing = (SemaphoreSlim)typeof(ProcessEngine).GetField("_routingLock",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(h.Engine)!;
        await routing.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            h.Time.Advance(TimeSpan.FromSeconds(59));
            await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
            await h.IdleAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);
        }
        finally
        {
            routing.Release();
        }

        var done = await h.WaitForAsync(id, p => p.IsTerminal, advance: TimeSpan.FromSeconds(1));
        Assert.Equal(ProcessStatus.Completed, done.Status);
        Assert.Equal(1, h.World.Executions("got"));
    }

    [Fact]
    public async Task Message_written_after_a_definitely_failed_question_is_not_taken_when_the_question_is_asked_again()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("ask", ActionMode.Fail, ActionMode.Succeed);
        var id = await h.RunAsync(AskAndWait());
        await h.WaitForAsync(id, p => p.Attention == AttentionKind.StepFailed);

        // Вопрос точно не ушёл; человек пишет «да» о чём-то своём. Через пару часов вопрос задают снова.
        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
        h.Time.Advance(TimeSpan.FromHours(2));
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.World.Executions("got"));
    }

    [Fact]
    public async Task Steps_without_actions_between_the_question_and_the_wait_do_not_move_the_window()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Block("ask");
        var id = await h.RunAsync(new ScenarioBuilder("Вопрос, переменная, ответ")
            .Variable("asked", VariableType.Boolean, false)
            .Node("start", "start")
            .Node("ask", TestActionNode.TypeName, new { key = "ask" })
            .Node("mark", "variable.set", new { variable = "asked", value = "true" })
            .Node("wait", "event.wait", new { source = "manual", correlationKey = "chat", lookbackSeconds = 30, timeoutSeconds = 1800 })
            .Node("got", TestActionNode.TypeName, new { key = "got" })
            .Node("end", "end")
            .Chain("start", "ask", "mark", "wait").Edge("wait", "got", Ports.Event).Edge("got", "end").Build());
        await h.World.EnteredAsync("ask");
        await h.Engine.PauseAsync(id);
        h.World.Release("ask");
        await h.WaitForStatusAsync(id, ProcessStatus.Paused);

        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
    }

    [Fact]
    public async Task Reply_is_found_among_many_unrelated_unmatched_events()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Block("ask");
        var id = await h.RunAsync(AskAndWait());
        await h.World.EnteredAsync("ask");
        await h.Engine.PauseAsync(id);
        h.World.Release("ask");
        await h.WaitForStatusAsync(id, ProcessStatus.Paused);

        // Ответ, а за ним — 520 сообщений из других чатов (загруженный аккаунт): никому не подошли.
        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
        for (var i = 0; i < 520; i++)
        {
            await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = $"other-{i}" });
        }

        await h.UntilAsync(async () => (await h.Store.ListEventsAsync(new EventQuery { States = [EventState.Unmatched], Limit = 1000 })).Count >= 521,
            "события разобраны");
        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
    }

    [Fact]
    public async Task Message_written_before_a_called_scenario_asks_is_not_taken_as_the_reply()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(new ScenarioBuilder("Спросить")
            .Node("start", "start")
            .Node("ask", TestActionNode.TypeName, new { key = "child-ask" })
            .Node("end", "end")
            .Chain("start", "ask", "end").Build());
        var id = await h.RunAsync(new ScenarioBuilder("Родитель спрашивает через вызов")
            .Node("start", "start")
            .Node("open", TestActionNode.TypeName, new { key = "open" })
            .Node("pause", "wait", new { mode = "duration", amount = 10, unit = "minutes" })
            .Node("call", "scenario.call", new { scenario = child })
            .Node("wait", "event.wait", new { source = "manual", correlationKey = "chat", lookbackSeconds = 30, timeoutSeconds = 1800 })
            .Node("got", TestActionNode.TypeName, new { key = "got" })
            .Node("end", "end")
            .Chain("start", "open", "pause", "call").Edge("call", "wait", Ports.Done)
            .Edge("wait", "got", Ports.Event).Edge("got", "end").Build());
        await h.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        // Сообщение во время таймера — до вопроса, который задаст вызванный сценарий.
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.UntilAsync(async () => (await h.Store.GetSubscriptionsAsync(id)).Any(x => x.State == SubscriptionState.Active && x.NodeId == "wait"),
            "ожидание записано", TimeSpan.FromSeconds(30));
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.World.Executions("got"));
    }

    [Fact]
    public async Task Message_written_before_the_split_is_not_taken_when_a_sibling_branch_asks()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Вопрос в соседней ветви")
            .Node("start", "start")
            .Node("open", TestActionNode.TypeName, new { key = "open" })
            .Node("pause", "wait", new { mode = "duration", amount = 10, unit = "minutes" })
            .Node("split", "parallel")
            .Node("ask", TestActionNode.TypeName, new { key = "ask" })
            .Node("wait", "event.wait", new { source = "manual", correlationKey = "chat", lookbackSeconds = 30, timeoutSeconds = 1800 })
            .Node("got", TestActionNode.TypeName, new { key = "got" })
            .Node("join", "join")
            .Node("end", "end")
            .Edge("start", "open").Edge("open", "pause").Edge("pause", "split")
            .Edge("split", "wait", "b1").Edge("split", "ask", "b2")
            .Edge("wait", "got", Ports.Event).Edge("got", "join").Edge("ask", "join").Edge("join", "end")
            .Build());
        await h.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
        h.Time.Advance(TimeSpan.FromMinutes(10));
        await h.UntilAsync(() => h.World.Executions("ask") == 1, "вопрос задан", TimeSpan.FromSeconds(30));
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.World.Executions("got"));
    }

    [Fact]
    public async Task Reply_during_a_timer_between_two_visits_of_the_same_wait_is_taken()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Ждать, подождать, ждать снова")
            .Node("start", "start")
            .Node("ask", TestActionNode.TypeName, new { key = "ask" })
            .Node("wait", "event.wait", new { source = "manual", correlationKey = "chat", lookbackSeconds = 30, timeoutSeconds = 600 })
            .Node("pause", "wait", new { mode = "duration", amount = 60, unit = "minutes" })
            .Node("got", TestActionNode.TypeName, new { key = "got" })
            .Node("end", "end")
            .Edge("start", "ask").Edge("ask", "wait").Edge("wait", "got", Ports.Event).Edge("wait", "pause", Ports.Timeout)
            .Edge("pause", "wait").Edge("got", "end").Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        // Первое ожидание истекло, идёт час таймера; ответ пришёл в это время — второе ожидание его берёт.
        h.Time.Advance(TimeSpan.FromMinutes(11));
        await h.UntilAsync(async () => (await h.StepsAsync(id)).Any(s => s.NodeId == "pause"), "таймер начался", TimeSpan.FromSeconds(30));
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "reply", CorrelationKey = "chat" });
        h.Time.Advance(TimeSpan.FromMinutes(60));
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("got"));
    }
}
