using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>События: сопоставление по правилам, несколько событий, неоднозначность, повторы, ожидание на паузе.</summary>
public sealed class EventTests
{
    private const string Action = TestActionNode.TypeName;

    /// <summary>Ждёт ответ по ключу заказа, затем выполняет действие «got-{order}».</summary>
    private static ScenarioDefinition WaitReply(int? timeoutSeconds = null, int priority = 0, string? match = null,
        int lookbackSeconds = 0, bool connectTimeout = true, string? correlationKey = "{{ vars.order }}")
    {
        var builder = new ScenarioBuilder("Ждать ответ")
            .Variable("order", VariableType.Text, "", isInput: true)
            .Variable("reply", VariableType.Any)
            .Node("start", "start")
            .Node("w", "event.wait", new
            {
                source = "manual", eventType = "reply", correlationKey, timeoutSeconds, priority, match, lookbackSeconds,
                saveTo = "reply",
            })
            .Node("a", Action, new { key = "got-{{ vars.order }}" })
            .Node("end", "end")
            .Edge("start", "w")
            .Edge("w", "a", Ports.Event)
            .Edge("a", "end");
        if (connectTimeout)
        {
            builder.Node("late", "end", new { outcome = "failure", message = "Ответ не пришёл" }).Edge("w", "late", Ports.Timeout);
        }

        return builder.Build();
    }

    private static EventInput Reply(string? order, string? externalId = null, JsonNode? payload = null) => new()
    {
        Source = "manual", Type = "reply", CorrelationKey = order, ExternalId = externalId, Payload = payload,
    };

    private static async Task<(EngineHarness H, string ScenarioId)> SetupAsync(ScenarioDefinition scenario)
    {
        var h = await EngineHarness.CreateAsync();
        return (h, await h.CreateScenarioAsync(scenario));
    }

    private static async Task<string> StartWaitingAsync(EngineHarness h, string scenarioId, string order)
    {
        var id = await h.Engine.StartProcessAsync(scenarioId, new JsonObject { ["order"] = order });
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        return id;
    }

    private static async Task<EventRecord> WaitEventStateAsync(EngineHarness h, long eventId, EventState state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var record = await h.Store.GetEventAsync(eventId);
            if (record?.State == state)
            {
                return record;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Событие в состоянии {record?.State}, ожидалось {state}. {record?.Note}");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Event_goes_only_to_the_process_with_matching_key()
    {
        var (h, scenario) = await SetupAsync(WaitReply());
        await using var _ = h;
        var first = await StartWaitingAsync(h, scenario, "A-1");
        var second = await StartWaitingAsync(h, scenario, "B-2");

        var published = await h.Engine.PublishEventAsync(Reply("B-2", payload: new JsonObject { ["text"] = "ок" }));
        var done = await h.WaitForStatusAsync(second, ProcessStatus.Completed);

        Assert.Equal("ок", done.Variables["reply"]?["payload"]?["text"]?.GetValue<string>());
        Assert.Equal(1, h.World.Executions("got-B-2"));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(first)).Status);
        Assert.Equal(0, h.World.Executions("got-A-1"));

        var record = await WaitEventStateAsync(h, published.EventId, EventState.Delivered);
        Assert.Equal(second, record.ProcessId);

        await h.Engine.PublishEventAsync(Reply("A-1"));
        await h.WaitForStatusAsync(first, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("got-A-1"));
    }

    [Fact]
    public async Task Event_without_waiting_process_is_stored_and_starts_nothing()
    {
        var (h, scenario) = await SetupAsync(WaitReply());
        await using var _ = h;
        var published = await h.Engine.PublishEventAsync(Reply("nobody"));

        var record = await WaitEventStateAsync(h, published.EventId, EventState.Unmatched);
        Assert.Null(record.ProcessId);
        Assert.Empty(await h.Store.ListProcessesAsync(new ProcessQuery()));
    }

    [Fact]
    public async Task Several_events_each_go_to_one_waiter_and_extra_ones_stay_unmatched()
    {
        var (h, scenario) = await SetupAsync(WaitReply());
        await using var _ = h;
        var id = await StartWaitingAsync(h, scenario, "X");

        var firstEvent = await h.Engine.PublishEventAsync(Reply("X", payload: 1));
        var secondEvent = await h.Engine.PublishEventAsync(Reply("X", payload: 2));
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal(1, done.Variables["reply"]?["payload"]?.GetValue<int>());
        await WaitEventStateAsync(h, firstEvent.EventId, EventState.Delivered);
        await WaitEventStateAsync(h, secondEvent.EventId, EventState.Unmatched);
        Assert.Equal(1, h.World.Executions("got-X"));
    }

    [Fact]
    public async Task Ambiguous_event_is_not_given_to_anyone_until_user_decides()
    {
        // Ключ не задан — событие одинаково подходит обоим процессам.
        var (h, scenario) = await SetupAsync(WaitReply(correlationKey: null));
        await using var _ = h;
        var first = await StartWaitingAsync(h, scenario, "1");
        var second = await StartWaitingAsync(h, scenario, "2");

        var published = await h.Engine.PublishEventAsync(Reply(null));
        var conflict = await WaitEventStateAsync(h, published.EventId, EventState.Conflict);
        Assert.Equal(2, conflict.Candidates!.Count);

        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(first)).Status);
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(second)).Status);
        Assert.Equal(0, h.World.Executions("got-1") + h.World.Executions("got-2"));

        var subscription = (await h.Store.GetSubscriptionsAsync(second)).Single(s => s.State == SubscriptionState.Active);
        await h.Engine.ResolveEventAsync(published.EventId, subscription.Id);
        await h.WaitForStatusAsync(second, ProcessStatus.Completed);

        Assert.Equal(1, h.World.Executions("got-2"));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(first)).Status);
        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.ResolveEventAsync(published.EventId, null));
    }

    [Fact]
    public async Task Higher_priority_wins_without_conflict()
    {
        await using var h = await EngineHarness.CreateAsync();
        var low = await h.CreateScenarioAsync(WaitReply(correlationKey: null, priority: 0));
        var high = await h.CreateScenarioAsync(WaitReply(correlationKey: null, priority: 5));
        var lowProcess = await StartWaitingAsync(h, low, "low");
        var highProcess = await StartWaitingAsync(h, high, "high");

        await h.Engine.PublishEventAsync(Reply(null));
        await h.WaitForStatusAsync(highProcess, ProcessStatus.Completed);
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(lowProcess)).Status);
    }

    [Fact]
    public async Task Repeated_event_with_same_external_id_is_processed_once()
    {
        var (h, scenario) = await SetupAsync(WaitReply());
        await using var _ = h;
        var first = await StartWaitingAsync(h, scenario, "D");
        var original = await h.Engine.PublishEventAsync(Reply("D", externalId: "msg-100"));
        await h.WaitForStatusAsync(first, ProcessStatus.Completed);

        var second = await StartWaitingAsync(h, scenario, "D");
        var repeat = await h.Engine.PublishEventAsync(Reply("D", externalId: "msg-100"));

        Assert.True(repeat.Duplicate);
        Assert.Equal(original.EventId, repeat.EventId);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(second)).Status);
        Assert.Equal(1, h.World.Executions("got-D"));
    }

    [Fact]
    public async Task Earlier_event_is_taken_only_within_lookback_window()
    {
        await using var h = await EngineHarness.CreateAsync();
        var strict = await h.CreateScenarioAsync(WaitReply());
        var lenient = await h.CreateScenarioAsync(WaitReply(lookbackSeconds: 120));

        var early = await h.Engine.PublishEventAsync(Reply("E"));
        await WaitEventStateAsync(h, early.EventId, EventState.Unmatched);
        h.Time.Advance(TimeSpan.FromSeconds(60));

        var strictProcess = await StartWaitingAsync(h, strict, "E");
        await h.IdleAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(strictProcess)).Status);
        await h.Engine.CancelAsync(strictProcess);

        var lenientProcess = await h.Engine.StartProcessAsync(lenient, new JsonObject { ["order"] = "E" });
        await h.WaitForStatusAsync(lenientProcess, ProcessStatus.Completed);
        var record = await WaitEventStateAsync(h, early.EventId, EventState.Delivered);
        Assert.Equal(lenientProcess, record.ProcessId);
    }

    [Fact]
    public async Task Match_condition_filters_events()
    {
        var (h, scenario) = await SetupAsync(WaitReply(match: "event.payload.code >= 5"));
        await using var _ = h;
        var id = await StartWaitingAsync(h, scenario, "M");

        var small = await h.Engine.PublishEventAsync(Reply("M", payload: new JsonObject { ["code"] = 3 }));
        await WaitEventStateAsync(h, small.EventId, EventState.Unmatched);
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);

        await h.Engine.PublishEventAsync(Reply("M", payload: new JsonObject { ["code"] = 7 }));
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(7, done.Variables["reply"]?["payload"]?["code"]?.GetValue<int>());
    }

    [Fact]
    public async Task Event_for_paused_process_is_applied_only_after_resume()
    {
        var (h, scenario) = await SetupAsync(WaitReply());
        await using var _ = h;
        var id = await StartWaitingAsync(h, scenario, "P");
        await h.Engine.PauseAsync(id);

        var published = await h.Engine.PublishEventAsync(Reply("P"));
        var matched = await WaitEventStateAsync(h, published.EventId, EventState.Matched);
        Assert.Equal(id, matched.ProcessId);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(id)).Status);
        Assert.Equal(0, h.World.Executions("got-P"));

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        await WaitEventStateAsync(h, published.EventId, EventState.Delivered);
        Assert.Equal(1, h.World.Executions("got-P"));
    }

    [Fact]
    public async Task After_restart_waiting_process_gets_event_only_after_manual_resume()
    {
        var (h, scenario) = await SetupAsync(WaitReply());
        await using var _ = h;
        var id = await StartWaitingAsync(h, scenario, "R");

        await h.RestartAsync();
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(id)).Status);
        var published = await h.Engine.PublishEventAsync(Reply("R"));
        await WaitEventStateAsync(h, published.EventId, EventState.Matched);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(id)).Status);

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("got-R"));
    }

    [Fact]
    public async Task Timeout_follows_timeout_branch_and_late_event_is_not_taken()
    {
        var (h, scenario) = await SetupAsync(WaitReply(timeoutSeconds: 60));
        await using var _ = h;
        var id = await StartWaitingAsync(h, scenario, "T");

        var failed = await h.WaitForStatusAsync(id, ProcessStatus.Failed, advance: TimeSpan.FromSeconds(5));
        Assert.Equal("Ответ не пришёл", failed.StatusMessage);

        var late = await h.Engine.PublishEventAsync(Reply("T"));
        await WaitEventStateAsync(h, late.EventId, EventState.Unmatched);
        Assert.Equal(0, h.World.Executions("got-T"));
    }

    [Fact]
    public async Task Timeout_without_connected_branch_stops_with_clear_reason()
    {
        var (h, scenario) = await SetupAsync(WaitReply(timeoutSeconds: 30, connectTimeout: false));
        await using var _ = h;
        var id = await StartWaitingAsync(h, scenario, "U");

        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention, advance: TimeSpan.FromSeconds(5));
        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Contains("тайм-аут", process.StatusMessage);
        Assert.Contains("не подключён", process.StatusMessage);

        // Решение пользователя: ждать снова.
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await h.Engine.PublishEventAsync(Reply("U"));
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
    }

    [Fact]
    public async Task Process_can_signal_another_process_with_send_event()
    {
        await using var h = await EngineHarness.CreateAsync();
        var waiter = await h.CreateScenarioAsync(new ScenarioBuilder("Ждёт сигнала")
            .Node("start", "start")
            .Node("w", "event.wait", new { source = "process", eventType = "ready", correlationKey = "batch-7" })
            .Node("a", Action, new { key = "after-signal" })
            .Node("end", "end")
            .Edge("start", "w").Edge("w", "a", Ports.Event).Edge("a", "end")
            .Build());
        var sender = await h.CreateScenarioAsync(new ScenarioBuilder("Отправляет сигнал")
            .Node("start", "start")
            .Node("s", "event.send", new { source = "process", eventType = "ready", correlationKey = "batch-7", payload = "({ n: 1 })" })
            .Node("end", "end")
            .Chain("start", "s", "end")
            .Build());

        var waiting = await h.Engine.StartProcessAsync(waiter);
        await h.WaitForStatusAsync(waiting, ProcessStatus.Waiting);
        var sending = await h.Engine.StartProcessAsync(sender);
        await h.WaitForStatusAsync(sending, ProcessStatus.Completed);
        await h.WaitForStatusAsync(waiting, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("after-signal"));
    }

    [Fact]
    public async Task Cancelled_waiter_returns_assigned_event_for_rematching()
    {
        await using var h = await EngineHarness.CreateAsync();
        var strict = await h.CreateScenarioAsync(WaitReply());
        var lenient = await h.CreateScenarioAsync(WaitReply(lookbackSeconds: 600));
        var first = await StartWaitingAsync(h, strict, "C");
        await h.Engine.PauseAsync(first);
        var published = await h.Engine.PublishEventAsync(Reply("C"));
        await WaitEventStateAsync(h, published.EventId, EventState.Matched);

        var second = await StartWaitingAsync(h, lenient, "C");
        await h.IdleAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(second)).Status);

        // Первый процесс остановлен, не применив событие, — событие возвращается на сопоставление и достаётся второму.
        await h.Engine.CancelAsync(first);
        await h.WaitForStatusAsync(second, ProcessStatus.Completed);
        var record = await WaitEventStateAsync(h, published.EventId, EventState.Delivered);
        Assert.Equal(second, record.ProcessId);
        Assert.Equal(1, h.World.Executions("got-C"));
    }

    [Fact]
    public async Task Old_handled_events_lose_their_data_keep_stopping_repeats_and_conflicts_are_never_touched()
    {
        var (h, scenario) = await SetupAsync(WaitReply(correlationKey: null));
        await using var _ = h;
        // Другой тип — чтобы ждущие ниже процессы не забрали это событие по окну «смотреть назад».
        var unmatched = await h.Engine.PublishEventAsync(Reply("никому", externalId: "m-1", payload: new JsonObject { ["text"] = "большое письмо" }) with { Type = "notice" });
        await WaitEventStateAsync(h, unmatched.EventId, EventState.Unmatched);
        var waiting = await StartWaitingAsync(h, scenario, "A");
        var delivered = await h.Engine.PublishEventAsync(Reply(null, externalId: "m-2", payload: new JsonObject { ["text"] = "ответ" }));
        await h.WaitForStatusAsync(waiting, ProcessStatus.Completed);
        await StartWaitingAsync(h, scenario, "B");
        await StartWaitingAsync(h, scenario, "C");
        var conflict = await h.Engine.PublishEventAsync(Reply(null, externalId: "m-3", payload: new JsonObject { ["text"] = "кому?" }));
        await WaitEventStateAsync(h, conflict.EventId, EventState.Conflict);

        var now = h.Time.GetUtcNow();
        var later = now.AddDays(1);
        var (cleared, deleted) = await h.Store.PruneEventsAsync(later, now.AddDays(-1), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((2, 0), (cleared, deleted));
        var old = (await h.Store.GetEventAsync(unmatched.EventId))!;
        Assert.Null(old.Payload);
        Assert.Contains("по сроку хранения", old.Note);
        Assert.Null((await h.Store.GetEventAsync(delivered.EventId))!.Payload);
        Assert.Equal("кому?", (await h.Store.GetEventAsync(conflict.EventId))!.Payload!["text"]!.GetValue<string>());
        // Строка осталась — повторно пришедшее событие узнаётся.
        Assert.True((await h.Engine.PublishEventAsync(Reply("никому", externalId: "m-1") with { Type = "notice" })).Duplicate);

        // Через год строки удаляются — кроме событий, которые получил процесс, и ждущих решения.
        (cleared, deleted) = await h.Store.PruneEventsAsync(later, later, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal((0, 1), (cleared, deleted));
        Assert.Null(await h.Store.GetEventAsync(unmatched.EventId));
        Assert.NotNull(await h.Store.GetEventAsync(delivered.EventId));
        Assert.NotNull(await h.Store.GetEventAsync(conflict.EventId));
    }
}
