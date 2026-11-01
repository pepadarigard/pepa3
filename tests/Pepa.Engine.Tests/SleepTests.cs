using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>
/// Сон компьютера и пропажа связи с источником (D-080): тайм-аут, наступивший, пока источник был слеп, ждёт, пока он догрузит
/// пропущенное, — но не бесконечно; ответ, пришедший вовремя, побеждает тайм-аут, а опоздавший — нет.
/// </summary>
public sealed class SleepTests
{
    private const string Source = "chat";

    private static EngineOptions WithSleep() => new()
    {
        MaxParallelProcesses = 4,
        ShutdownGracePeriod = TimeSpan.FromSeconds(2),
        SleepGap = TimeSpan.FromMinutes(2),
    };

    /// <summary>Ждёт ответ <paramref name="timeoutSeconds"/> секунд: ответ — «Ответ: …», тайм-аут — ошибка «Ответа нет».</summary>
    private static ScenarioDefinition WaitReply(int timeoutSeconds) => new ScenarioBuilder("Ждёт ответа")
        .Variable("reply", VariableType.Any)
        .Node("start", "start")
        .Node("w", "event.wait", new { source = Source, eventType = "reply", timeoutSeconds, saveTo = "reply" })
        .Node("ok", "end", new { message = "Ответ: {{ vars.reply.payload }}" })
        .Node("late", "end", new { outcome = "failure", message = "Ответа нет" })
        .Edge("start", "w")
        .Edge("w", "ok", Ports.Event)
        .Edge("w", "late", Ports.Timeout)
        .Build();

    private static EventInput Reply(string text, DateTimeOffset occurredAt) =>
        new() { Source = Source, Type = "reply", Payload = text, OccurredAt = occurredAt };

    [Fact]
    public async Task Reply_that_came_during_sleep_wins_over_the_timeout_that_expired_while_asleep()
    {
        await using var h = await EngineHarness.CreateAsync(WithSleep());
        (DateTimeOffset From, DateTimeOffset To)? woke = null;
        h.Engine.Woke += (from, to) =>
        {
            woke = (from, to);
            h.Engine.SourceCatchingUp(Source, "чат", from);
        };
        var id = await h.RunAsync(WaitReply(timeoutSeconds: 600));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        var askedAt = h.Time.GetUtcNow();

        // Компьютер спит 30 минут: срок ожидания (10 минут) истёк во сне, а ответ пришёл на 5-й минуте.
        h.Time.Advance(TimeSpan.FromMinutes(30));
        await h.IdleAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);
        Assert.NotNull(woke);
        Assert.True(woke.Value.To - woke.Value.From >= TimeSpan.FromMinutes(29));
        Assert.Equal(woke, h.Engine.LastSleep);

        await h.Engine.PublishEventAsync(Reply("да", askedAt.AddMinutes(5)));
        h.Engine.SourceCaughtUp(Source);
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("Ответ: да", done.StatusMessage);
    }

    [Fact]
    public async Task Timeout_after_sleep_waits_for_the_source_but_not_forever()
    {
        await using var h = await EngineHarness.CreateAsync(WithSleep());
        h.Engine.Woke += (from, _) => h.Engine.SourceCatchingUp(Source, "чат", from);
        var id = await h.RunAsync(WaitReply(timeoutSeconds: 600));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);

        h.Time.Advance(TimeSpan.FromMinutes(30));
        var wokeAt = h.Time.GetUtcNow();
        await h.IdleAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);

        // Источник так и не догрузил пропущенное: тайм-аут — не позже чем через MaxCatchUpWait после пробуждения.
        var failed = await h.WaitForStatusAsync(id, ProcessStatus.Failed, advance: TimeSpan.FromSeconds(10));
        Assert.Equal("Ответа нет", failed.StatusMessage);
        Assert.True(h.Time.GetUtcNow() >= wokeAt + h.Options.MaxCatchUpWait);
        Assert.True(h.Time.GetUtcNow() < wokeAt + h.Options.MaxCatchUpWait + TimeSpan.FromMinutes(1));
        var journal = await h.Store.GetJournalAsync(id);
        Assert.Contains(journal, j => j.Message.Contains("Тайм-аут отложен до конца догрузки", StringComparison.Ordinal) &&
                                      j.Message.Contains("компьютер спал", StringComparison.Ordinal) &&
                                      j.Message.Contains("(чат)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lost_connection_holds_the_timeout_and_a_reply_from_before_the_deadline_still_counts()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(WaitReply(timeoutSeconds: 60));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        var askedAt = h.Time.GetUtcNow();
        await h.IdleAsync(TimeSpan.FromSeconds(30));

        // Связь пропала на 30-й секунде; срок — на 60-й. Пока источник не догрузил пропущенное, тайм-аут не наступает.
        h.Engine.SourceCatchingUp(Source, "чат");
        await h.IdleAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);

        await h.Engine.PublishEventAsync(Reply("успел", askedAt.AddSeconds(50)));
        h.Engine.SourceCaughtUp(Source);
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("Ответ: успел", done.StatusMessage);
    }

    [Fact]
    public async Task Reply_after_the_deadline_does_not_win_while_the_timeout_is_held()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(WaitReply(timeoutSeconds: 60));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        var askedAt = h.Time.GetUtcNow();
        h.Engine.SourceCatchingUp(Source, "чат");
        await h.IdleAsync(TimeSpan.FromMinutes(2));

        // Догрузилось сообщение, отправленное уже после срока: ожидание его не принимает и уходит по тайм-ауту.
        await h.Engine.PublishEventAsync(Reply("поздно", askedAt.AddSeconds(90)));
        h.Engine.SourceCaughtUp(Source);
        var failed = await h.WaitForStatusAsync(id, ProcessStatus.Failed, advance: TimeSpan.FromSeconds(1));
        Assert.Equal("Ответа нет", failed.StatusMessage);
    }

    [Fact]
    public async Task Without_sleep_detection_a_clock_jump_is_just_time()
    {
        await using var h = await EngineHarness.CreateAsync();
        var woke = false;
        h.Engine.Woke += (_, _) => woke = true;
        var id = await h.RunAsync(WaitReply(timeoutSeconds: 600));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        h.Time.Advance(TimeSpan.FromMinutes(30));
        var failed = await h.WaitForStatusAsync(id, ProcessStatus.Failed, advance: TimeSpan.FromSeconds(1));
        Assert.Equal("Ответа нет", failed.StatusMessage);
        Assert.False(woke);
        Assert.Null(h.Engine.LastSleep);
    }

    [Fact]
    public async Task Wait_started_after_waking_up_times_out_on_time()
    {
        await using var h = await EngineHarness.CreateAsync(WithSleep());
        h.Time.Advance(TimeSpan.FromMinutes(30));
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(h.Engine.LastSleep);

        var id = await h.RunAsync(WaitReply(timeoutSeconds: 10));
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        var startedAt = h.Time.GetUtcNow();
        var failed = await h.WaitForStatusAsync(id, ProcessStatus.Failed, advance: TimeSpan.FromSeconds(1));
        Assert.Equal("Ответа нет", failed.StatusMessage);
        Assert.True(h.Time.GetUtcNow() < startedAt + TimeSpan.FromSeconds(40));
    }
}
