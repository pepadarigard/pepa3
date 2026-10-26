using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Pepa.Core;
using Pepa.Core.Expressions;
using Pepa.Core.Model;
using Pepa.Engine.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Scheduling;
using Pepa.Engine.Tests.Infrastructure;
using Pepa.Storage.Sqlite;

namespace Pepa.Engine.Tests;

/// <summary>Хранилище с перехватчиками после отдельных чтений — чтобы воспроизводить гонки детерминированно.</summary>
public sealed class HookStore(IPepaStore inner) : IPepaStore
{
    public Func<Task>? AfterListTriggers;
    public Func<EventQuery, IReadOnlyList<EventRecord>, Task>? AfterListEvents;

    public Task InitializeAsync(CancellationToken c = default) => inner.InitializeAsync(c);
    public Task<IReadOnlyList<StoredScenario>> ListScenariosAsync(CancellationToken c = default) => inner.ListScenariosAsync(c);
    public Task<StoredScenario?> GetScenarioAsync(string id, CancellationToken c = default) => inner.GetScenarioAsync(id, c);
    public Task<ProcessRecord?> GetProcessAsync(string id, CancellationToken c = default) => inner.GetProcessAsync(id, c);
    public Task<IReadOnlyList<ProcessRecord>> ListProcessesAsync(ProcessQuery q, CancellationToken c = default) => inner.ListProcessesAsync(q, c);
    public Task<IReadOnlyList<ProcessRecord>> GetChildProcessesAsync(string p, CancellationToken c = default) => inner.GetChildProcessesAsync(p, c);
    public Task<IReadOnlyList<TokenRecord>> GetTokensAsync(string p, CancellationToken c = default) => inner.GetTokensAsync(p, c);
    public Task<IReadOnlyList<TokenRecord>> GetTokensAsync(IReadOnlyCollection<string> p, CancellationToken c = default) => inner.GetTokensAsync(p, c);
    public Task<IReadOnlyList<StepRecord>> GetStepsAsync(string p, CancellationToken c = default) => inner.GetStepsAsync(p, c);
    public Task<StepRecord?> GetStepAsync(long s, CancellationToken c = default) => inner.GetStepAsync(s, c);
    public Task<IReadOnlyList<StepRecord>> GetOpenStepsAsync(string p, CancellationToken c = default) => inner.GetOpenStepsAsync(p, c);
    public Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string p, CancellationToken c = default) => inner.GetJournalAsync(p, c);
    public Task<IReadOnlyList<string>> GetRunnableProcessIdsAsync(DateTimeOffset n, CancellationToken c = default) => inner.GetRunnableProcessIdsAsync(n, c);
    public Task<IReadOnlyDictionary<string, ProcessRecord>> GetLatestProcessPerScenarioAsync(CancellationToken c = default) => inner.GetLatestProcessPerScenarioAsync(c);
    public Task<EventRecord?> GetEventAsync(long id, CancellationToken c = default) => inner.GetEventAsync(id, c);

    public async Task<IReadOnlyList<EventRecord>> ListEventsAsync(EventQuery q, CancellationToken c = default)
    {
        var result = await inner.ListEventsAsync(q, c);
        var hook = AfterListEvents;
        if (hook is not null)
        {
            await hook(q, result);
        }

        return result;
    }

    public Task<SubscriptionRecord?> GetSubscriptionAsync(long id, CancellationToken c = default) => inner.GetSubscriptionAsync(id, c);
    public Task<IReadOnlyList<SubscriptionRecord>> GetActiveSubscriptionsAsync(string s, CancellationToken c = default) => inner.GetActiveSubscriptionsAsync(s, c);
    public Task<IReadOnlyList<SubscriptionRecord>> GetSubscriptionsAsync(string p, CancellationToken c = default) => inner.GetSubscriptionsAsync(p, c);
    public Task<IReadOnlyList<ResourceRecord>> ListResourcesAsync(CancellationToken c = default) => inner.ListResourcesAsync(c);
    public Task<IReadOnlyList<ResourceWaiterRecord>> ListResourceWaitersAsync(CancellationToken c = default) => inner.ListResourceWaitersAsync(c);

    public async Task<IReadOnlyList<TriggerRecord>> ListTriggersAsync(string? s = null, CancellationToken c = default)
    {
        var result = await inner.ListTriggersAsync(s, c);
        var hook = Interlocked.Exchange(ref AfterListTriggers, null);
        if (hook is not null)
        {
            await hook();
        }

        return result;
    }

    public Task<TriggerRecord?> GetTriggerAsync(string id, CancellationToken c = default) => inner.GetTriggerAsync(id, c);
    public Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken c = default) => inner.GetSettingsAsync(c);
    public Func<Task>? BeforeBeginWrite;

    public async Task<IStoreWrite> BeginWriteAsync(CancellationToken c = default)
    {
        var hook = BeforeBeginWrite;
        if (hook is not null)
        {
            await hook();
        }

        return await inner.BeginWriteAsync(c);
    }
}

/// <summary>
/// Гонки и дыры в состояниях, найденные ревью этапа 2. Каждый тест воспроизводит сценарий, в котором раньше
/// происходило незапланированное действие или терялось событие, и проверяет правильное поведение.
/// </summary>
public sealed class ReviewRegressionTests(ITestOutputHelper output)
{
    private const string Action = TestActionNode.TypeName;

    private sealed class Rig : IAsyncDisposable
    {
        public required SqliteStore Sqlite;
        public required HookStore Store;
        public required ProcessEngine Engine;
        public required ScenarioService Scenarios;
        public required FakeTimeProvider Time;
        public required ExternalWorld World;

        public async ValueTask DisposeAsync() => await Engine.DisposeAsync();
    }

    private static async Task<Rig> RigAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pepa-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var sqlite = new SqliteStore(Path.Combine(dir, "pepa.db"));
        await sqlite.InitializeAsync();
        var store = new HookStore(sqlite);
        var time = new FakeTimeProvider(EngineHarness.StartTime);
        var world = new ExternalWorld();
        var registry = new Pepa.Core.Nodes.NodeRegistry().RegisterLogicNodes().RegisterFlowNodes().Register(new TestActionNode(world));
        var engine = new ProcessEngine(store, registry, new JintExpressionEvaluator(),
            new EngineOptions { MaxParallelProcesses = 4, ShutdownGracePeriod = TimeSpan.FromSeconds(2) }, time);
        var scenarios = new ScenarioService(store, engine.Validator, time);
        await engine.StartAsync();
        return new Rig { Sqlite = sqlite, Store = store, Engine = engine, Scenarios = scenarios, Time = time, World = world };
    }

    private static ScenarioDefinition Simple(string key) => new ScenarioBuilder("S")
        .Node("start", "start").Node("a", Action, new { key }).Node("end", "end").Chain("start", "a", "end").Build();

    private static ScenarioDefinition Waiter(string type, int lookback) => new ScenarioBuilder("W")
        .Variable("ev", VariableType.Any)
        .Node("start", "start")
        .Node("w", "event.wait", new { source = "manual", eventType = type, lookbackSeconds = lookback, saveTo = "ev" })
        .Node("end", "end")
        .Edge("start", "w").Edge("w", "end", Ports.Event).Build();

    private static async Task Until(Func<Task<bool>> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public void Intervals_that_do_not_divide_a_day_keep_their_period()
    {
        foreach (var minutes in new[] { 36 * 60, 25 * 60, 1000, 7 })
        {
            var cfg = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = minutes };
            var t = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
            var list = new List<DateTimeOffset>();
            for (var i = 0; i < 6; i++)
            {
                t = ScheduleCalculator.Next(cfg, t, TimeZoneInfo.Utc)!.Value;
                list.Add(t);
            }

            output.WriteLine($"{minutes}: " + string.Join(" | ", list.Select(x => x.ToString("u"))));
            for (var i = 1; i < list.Count; i++)
            {
                Assert.Equal(TimeSpan.FromMinutes(minutes), list[i] - list[i - 1]);
            }
        }
    }

    [Fact]
    public async Task Event_received_before_restart_does_not_start_trigger_process_by_itself()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(Simple("auto"));
        await h.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "", ScenarioId = scenario, Kind = TriggerKind.Event, Config = new JsonObject { ["source"] = "http" },
            CreatedAt = default, UpdatedAt = default,
        });
        await h.Engine.StopAsync();

        // Событие принято (записано), но приложение закрылось раньше, чем его сопоставили.
        long eventId;
        await using (var w = await h.Store.BeginWriteAsync())
        {
            eventId = (await w.TryInsertEventAsync(ProcessEngine.NewEventRecord(new EventInput { Source = "http", Type = "order" }, h.Time.GetUtcNow())))!.Value;
            await w.CommitAsync();
        }

        await h.RestartAsync();
        await Until(async () => (await h.Store.GetEventAsync(eventId))!.State == EventState.Unmatched);
        await h.IdleAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, h.World.Executions("auto"));
        Assert.Contains("до перезапуска", (await h.Store.GetEventAsync(eventId))!.Note);

        // Решение пользователя — «Сопоставить снова»: теперь триггер срабатывает.
        await h.Engine.RerouteEventAsync(eventId);
        await Until(async () => h.World.Executions("auto") == 1);
    }

    [Fact]
    public async Task Schedule_deleted_while_loop_is_running_never_fires_and_is_not_resurrected()
    {
        await using var r = await RigAsync();
        var scenario = (await r.Scenarios.CreateAsync(Simple("tick"))).Id;
        var trigger = await r.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "", ScenarioId = scenario, Kind = TriggerKind.Schedule, Name = "x",
            Config = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = 30 }, CreatedAt = default, UpdatedAt = default,
        });

        // Пользователь удаляет расписание сразу после того, как цикл прочитал список.
        r.Store.AfterListTriggers = async () => Assert.True(await r.Engine.DeleteTriggerAsync(trigger.Id));
        r.Time.Advance(TimeSpan.FromMinutes(31));
        for (var i = 0; i < 60; i++)
        {
            r.Time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20);
        }

        Assert.Null(await r.Sqlite.GetTriggerAsync(trigger.Id));
        Assert.Equal(0, r.World.Executions("tick"));
    }

    [Fact]
    public async Task Disabled_schedule_is_not_reenabled_by_a_stale_loop_write()
    {
        await using var r = await RigAsync();
        var scenario = (await r.Scenarios.CreateAsync(Simple("tick"))).Id;
        var trigger = await r.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "", ScenarioId = scenario, Kind = TriggerKind.Schedule, Name = "x",
            Config = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = 30 }, CreatedAt = default, UpdatedAt = default,
        });
        r.Store.AfterListTriggers = async () => await r.Engine.SaveTriggerAsync(trigger with { Enabled = false });
        r.Time.Advance(TimeSpan.FromMinutes(31));
        for (var i = 0; i < 60; i++)
        {
            r.Time.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(20);
        }

        Assert.False((await r.Sqlite.GetTriggerAsync(trigger.Id))!.Enabled);
        Assert.Equal(0, r.World.Executions("tick"));
    }

    [Fact]
    public async Task Event_assigned_by_user_during_reclaim_goes_to_exactly_one_process()
    {
        await using var r = await RigAsync();
        var ev = await r.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x" });
        await Until(async () => (await r.Sqlite.GetEventAsync(ev.EventId))!.State == EventState.Unmatched);

        var q = await r.Engine.StartProcessAsync((await r.Scenarios.CreateAsync(Waiter("other", 0))).Id);
        await Until(async () => (await r.Sqlite.GetProcessAsync(q))!.Status == ProcessStatus.Waiting);
        var qSub = (await r.Sqlite.GetSubscriptionsAsync(q)).Single(s => s.State == SubscriptionState.Active);

        Task? resolve = null;
        var armed = 1;
        r.Store.AfterListEvents = (query, list) =>
        {
            if (query.States?.Contains(EventState.Unmatched) == true && list.Any(e => e.Id == ev.EventId) &&
                Interlocked.Exchange(ref armed, 0) == 1)
            {
                // Пользователь отдаёт событие процессу Q в тот момент, когда новое ожидание P его подбирает.
                resolve = Task.Run(() => r.Engine.ResolveEventAsync(ev.EventId, qSub.Id));
            }

            return Task.CompletedTask;
        };

        var p = await r.Engine.StartProcessAsync((await r.Scenarios.CreateAsync(Waiter("x", 3600))).Id);
        await Until(async () => resolve is not null);
        try
        {
            await resolve!;
        }
        catch (EngineOperationException)
        {
            // Событие уже досталось P — назначить его ещё и Q нельзя.
        }

        await Task.Delay(500);
        var pe = (await r.Sqlite.GetProcessAsync(p))!.Variables["ev"]?["id"]?.GetValue<long>();
        var qe = (await r.Sqlite.GetProcessAsync(q))!.Variables["ev"]?["id"]?.GetValue<long>();
        output.WriteLine($"event {ev.EventId}: P got {pe}, Q got {qe}");
        Assert.True((pe == ev.EventId) ^ (qe == ev.EventId), "Событие должно достаться ровно одному процессу.");
    }

    [Fact]
    public async Task Lookback_waiter_gets_event_even_if_router_was_marking_it_unmatched()
    {
        await using var r = await RigAsync();
        var waiterScenario = (await r.Scenarios.CreateAsync(Waiter("x", 3600))).Id;
        string? p = null;
        await Task.Delay(300);
        r.Store.AfterListTriggers = async () =>
        {
            p = await r.Engine.StartProcessAsync(waiterScenario);
            await Until(async () => (await r.Sqlite.GetProcessAsync(p))!.Status == ProcessStatus.Waiting);
        };
        var ev = await r.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x" });
        await Until(async () => p is not null && (await r.Sqlite.GetProcessAsync(p))!.Status == ProcessStatus.Completed);
        Assert.Equal(EventState.Delivered, (await r.Sqlite.GetEventAsync(ev.EventId))!.State);
    }

    [Fact]
    public async Task Child_needing_resource_held_by_waiting_parent_is_reported_as_deadlock()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(new ScenarioBuilder("C")
            .Node("start", "start").Node("acq", "resource.acquire", new { key = "profile" }).Node("end", "end")
            .Chain("start", "acq", "end").Build());
        var parent = await h.RunAsync(new ScenarioBuilder("P")
            .Node("start", "start").Node("acq", "resource.acquire", new { key = "profile" })
            .Node("call", "scenario.call", new { scenario = child }).Node("end", "end")
            .Edge("start", "acq").Edge("acq", "call").Edge("call", "end", Ports.Done).Build());
        await h.WaitForStatusAsync(parent, ProcessStatus.Waiting);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        ProcessRecord? kid = null;
        while (DateTime.UtcNow < deadline && kid?.Status != ProcessStatus.NeedsAttention)
        {
            kid = (await h.Store.GetChildProcessesAsync(parent)).SingleOrDefault();
            await Task.Delay(20);
        }

        Assert.Equal(ProcessStatus.NeedsAttention, kid!.Status);
        Assert.Contains("Взаимная блокировка", kid.StatusMessage);
    }

    [Fact]
    public async Task Retry_after_engine_error_during_timeout_leaves_a_single_active_wait()
    {
        await using var h = await EngineHarness.CreateAsync(withFaults: true);
        h.Faults.FailEverythingAfterFirstFault = false;
        var fired = 0;
        h.Faults.FailWhenStep = step => step.NodeType == "event.wait" && step.Port == "timeout" && Interlocked.Exchange(ref fired, 1) == 0;
        var id = await h.RunAsync(new ScenarioBuilder("W")
            .Node("start", "start")
            .Node("w", "event.wait", new { source = "manual", eventType = "x", timeoutSeconds = 60 })
            .Node("end", "end").Node("late", "end")
            .Edge("start", "w").Edge("w", "end", Ports.Event).Edge("w", "late", Ports.Timeout).Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention, advance: TimeSpan.FromSeconds(10));

        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        var subs = await h.Store.GetSubscriptionsAsync(id);
        output.WriteLine("subscriptions: " + string.Join(", ", subs.Select(x => $"#{x.Id}:{x.State}")));
        Assert.Single(subs, x => x.State == SubscriptionState.Active);

        await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x" });
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(ProcessStatus.Completed, done.Status);
    }

    [Fact]
    public async Task Failed_delivery_can_be_continued_and_the_event_is_applied()
    {
        await using var h = await EngineHarness.CreateAsync(withFaults: true);
        h.Faults.FailEverythingAfterFirstFault = false;
        var fired = 0;
        h.Faults.FailWhenStep = step => step.NodeType == "event.wait" && step.Port == "event" && Interlocked.Exchange(ref fired, 1) == 0;
        var id = await h.RunAsync(new ScenarioBuilder("W")
            .Node("start", "start")
            .Node("w", "event.wait", new { source = "manual", eventType = "x" })
            .Node("end", "end")
            .Edge("start", "w").Edge("w", "end", Ports.Event).Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        var ev = await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x" });
        var attention = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        var allowed = h.Engine.GetAllowedActions(attention, await h.Store.GetTokensAsync(id));
        Assert.Contains(Pepa.Engine.ProcessAction.Continue, allowed);

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(EventState.Delivered, (await h.Store.GetEventAsync(ev.EventId))!.State);
    }

    [Fact]
    public async Task Retry_after_failed_delivery_returns_event_instead_of_stranding_it()
    {
        await using var h = await EngineHarness.CreateAsync(withFaults: true);
        h.Faults.FailEverythingAfterFirstFault = false;
        var fired = 0;
        h.Faults.FailWhenStep = step => step.NodeType == "event.wait" && step.Port == "event" && Interlocked.Exchange(ref fired, 1) == 0;
        var id = await h.RunAsync(new ScenarioBuilder("W")
            .Node("start", "start")
            .Node("w", "event.wait", new { source = "manual", eventType = "x" })
            .Node("end", "end")
            .Edge("start", "w").Edge("w", "end", Ports.Event).Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        var ev = await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x" });
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        // Пользователь решает через минуту: новое ожидание без окна «пришедшие раньше» старое событие не берёт.
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Engine.ResumeAsync(id, ResumeAction.Retry);
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await Task.Delay(300);
        var rec = (await h.Store.GetEventAsync(ev.EventId))!;
        var subs = await h.Store.GetSubscriptionsAsync(id);
        output.WriteLine($"event={rec.State}; subs: " + string.Join(", ", subs.Select(x => $"#{x.Id}:{x.State}")));
        Assert.Equal(EventState.Unmatched, rec.State);
        Assert.Single(subs, x => x.State == SubscriptionState.Active);
        Assert.Contains(subs, x => x.State == SubscriptionState.Cancelled);
    }

    [Fact]
    public async Task Unrelated_lookback_wait_does_not_make_new_trigger_fire_for_old_event()
    {
        await using var h = await EngineHarness.CreateAsync();
        var ev = await h.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "x" });
        await Until(async () => (await h.Store.GetEventAsync(ev.EventId))!.State == EventState.Unmatched);

        await h.IdleAsync(TimeSpan.FromMinutes(30));
        var scenario = await h.CreateScenarioAsync(Simple("trig"));
        await h.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "", ScenarioId = scenario, Kind = TriggerKind.Event, Config = new JsonObject { ["source"] = "manual", ["eventType"] = "x" },
            CreatedAt = default, UpdatedAt = default,
        });
        await h.RunAsync(Waiter("y", 3600));
        await h.RunAsync(Waiter("x-other", 3600));
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, h.World.Executions("trig"));
        Assert.Equal(EventState.Unmatched, (await h.Store.GetEventAsync(ev.EventId))!.State);
    }

    [Fact]
    public async Task Event_sent_while_waiter_subscribes_is_not_missed()
    {
        await using var r = await RigAsync();
        var senderScenario = (await r.Scenarios.CreateAsync(new ScenarioBuilder("A")
            .Node("start", "start").Node("pause", "wait", new { mode = "duration", amount = 10, unit = "seconds" })
            .Node("send", "event.send", new { source = "manual", eventType = "x" }).Node("end", "end")
            .Chain("start", "pause", "send", "end").Build())).Id;
        var waiterScenario = (await r.Scenarios.CreateAsync(Waiter("x", 0))).Id;
        var a = await r.Engine.StartProcessAsync(senderScenario);
        await Until(async () => (await r.Sqlite.GetProcessAsync(a))!.Status == ProcessStatus.Waiting);
        string? b = null;
        var armed = 1;
        r.Store.BeforeBeginWrite = async () =>
        {
            if (Volatile.Read(ref armed) == 1 && Environment.StackTrace.Contains("CommitResultAsync") &&
                (await r.Sqlite.GetTokensAsync(a)).Single().NodeId == "send" && Interlocked.Exchange(ref armed, 0) == 1)
            {
                r.Time.Advance(TimeSpan.FromMilliseconds(5));
                b = await r.Engine.StartProcessAsync(waiterScenario);
                await Until(async () => (await r.Sqlite.GetProcessAsync(b))!.Status == ProcessStatus.Waiting);
            }
        };
        r.Time.Advance(TimeSpan.FromSeconds(11));
        await Until(async () => (await r.Sqlite.GetProcessAsync(a))!.Status == ProcessStatus.Completed);
        await Until(async () => b is not null && (await r.Sqlite.GetProcessAsync(b))!.Status == ProcessStatus.Completed);
    }

    [Fact]
    public async Task Platform_event_source_cannot_be_faked()
    {
        await using var h = await EngineHarness.CreateAsync();
        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h.Engine.PublishEventAsync(new EventInput { Source = "Pepa.Process", Type = "finished", CorrelationKey = "x" }));
        var id = await h.RunAsync(new ScenarioBuilder("S")
            .Node("start", "start").Node("send", "event.send", new { source = "pepa.process", eventType = "finished" }).Node("end", "end")
            .Chain("start", "send", "end").Build());
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("служебный", process.StatusMessage);
    }

    [Fact]
    public async Task Empty_correlation_key_does_not_turn_into_match_anything()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("K")
            .Variable("order", VariableType.Any)
            .Node("start", "start").Node("w", "event.wait", new { source = "manual", correlationKey = "{{ vars.order }}" }).Node("end", "end")
            .Edge("start", "w").Edge("w", "end", Ports.Event).Build());
        var process = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("получился пустым", process.StatusMessage);
    }

    [Fact]
    public async Task Event_source_matching_ignores_case()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("C")
            .Node("start", "start").Node("w", "event.wait", new { source = "Telegram" }).Node("end", "end")
            .Edge("start", "w").Edge("w", "end", Ports.Event).Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await h.Engine.PublishEventAsync(new EventInput { Source = "TELEGRAM", Type = "message" });
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
    }

    [Fact]
    public async Task Schedule_much_later_than_planned_is_counted_as_missed_not_run()
    {
        await using var h = await EngineHarness.CreateAsync(new EngineOptions
        {
            MaxParallelProcesses = 2, ShutdownGracePeriod = TimeSpan.FromSeconds(2), TimerInterval = TimeSpan.FromHours(3),
        });
        var scenario = await h.CreateScenarioAsync(Simple("late"));
        var trigger = await h.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "", ScenarioId = scenario, Kind = TriggerKind.Schedule, Name = "x",
            Config = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = 60 }, CreatedAt = default, UpdatedAt = default,
        });

        // Компьютер «спал»: цикл расписаний проснулся через 3 часа.
        h.Time.Advance(TimeSpan.FromHours(3));
        await Until(async () => (await h.Store.GetTriggerAsync(trigger.Id))!.MissedRuns > 0);
        await Task.Delay(300);
        Assert.Equal(0, h.World.Executions("late"));
        Assert.Equal(3, (await h.Store.GetTriggerAsync(trigger.Id))!.MissedRuns);
    }
}
