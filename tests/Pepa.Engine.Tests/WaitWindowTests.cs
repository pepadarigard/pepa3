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
}
