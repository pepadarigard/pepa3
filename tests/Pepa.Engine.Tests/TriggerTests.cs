using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Scheduling;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Расписания и запуск по событиям: без двойных запусков, без автозапуска пропущенного.</summary>
public sealed class TriggerTests
{
    private const string Action = TestActionNode.TypeName;

    private static ScenarioDefinition Simple(string key, int waitSeconds = 0)
    {
        var builder = new ScenarioBuilder("По расписанию")
            .Variable("orderId", VariableType.Any, null, isInput: true)
            .Node("start", "start")
            .Node("a", Action, new { key })
            .Node("end", "end");
        if (waitSeconds > 0)
        {
            builder.Node("w", "wait", new { mode = "duration", amount = waitSeconds, unit = "seconds" }).Chain("start", "a", "w", "end");
        }
        else
        {
            builder.Chain("start", "a", "end");
        }

        return builder.Build();
    }

    private static TriggerRecord Every(string scenarioId, int minutes, string overlap = "skip") => new()
    {
        Id = "",
        ScenarioId = scenarioId,
        Kind = TriggerKind.Schedule,
        Name = "Каждые N минут",
        Config = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = minutes },
        Overlap = overlap,
        CreatedAt = default,
        UpdatedAt = default,
    };

    private static async Task<IReadOnlyList<ProcessRecord>> ProcessesAsync(EngineHarness h, string scenarioId) =>
        await h.Store.ListProcessesAsync(new ProcessQuery { ScenarioId = scenarioId, Limit = 100 });

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, EngineHarness h, TimeSpan? advance = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Условие не выполнилось.");
            }

            if (advance is { } step)
            {
                h.Time.Advance(step);
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Interval_schedule_starts_one_process_per_occurrence()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(Simple("tick"));
        var trigger = await h.Engine.SaveTriggerAsync(Every(scenario, 30));
        Assert.Equal(EngineHarness.StartTime.AddMinutes(30), trigger.NextRunAt);

        await h.IdleAsync(TimeSpan.FromMinutes(29));
        Assert.Empty(await ProcessesAsync(h, scenario));

        await WaitUntilAsync(async () => h.World.Executions("tick") == 1, h, TimeSpan.FromSeconds(10));
        await h.IdleAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(1, h.World.Executions("tick"));

        await WaitUntilAsync(async () => h.World.Executions("tick") == 2, h, TimeSpan.FromSeconds(30));
        var processes = await ProcessesAsync(h, scenario);
        Assert.Equal(2, processes.Count);
        Assert.All(processes, p => Assert.StartsWith($"schedule:{trigger.Id}:", p.OriginKey));
    }

    [Fact]
    public async Task Overlapping_run_is_skipped_while_previous_is_still_working()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(Simple("long", waitSeconds: 45 * 60));
        var trigger = await h.Engine.SaveTriggerAsync(Every(scenario, 30));

        await WaitUntilAsync(async () => (await ProcessesAsync(h, scenario)).Count == 1, h, TimeSpan.FromSeconds(30));
        await WaitUntilAsync(async () => (await h.Store.GetTriggerAsync(trigger.Id))!.SkippedRuns == 1, h, TimeSpan.FromSeconds(30));
        Assert.Single(await ProcessesAsync(h, scenario));
    }

    [Fact]
    public async Task Runs_missed_while_closed_are_counted_but_not_started()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(Simple("missed"));
        var trigger = await h.Engine.SaveTriggerAsync(Every(scenario, 30));

        await h.Engine.StopAsync();
        h.Time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5));
        await h.RestartAsync();

        Assert.Equal(4, h.LastRecovery!.MissedRuns);
        var saved = (await h.Store.GetTriggerAsync(trigger.Id))!;
        Assert.Equal(4, saved.MissedRuns);
        Assert.True(saved.NextRunAt > h.Time.GetUtcNow());
        await h.IdleAsync(TimeSpan.FromMinutes(5));
        Assert.Empty(await ProcessesAsync(h, scenario));

        // Пользователь решает запустить вручную.
        var id = await h.Engine.RunTriggerNowAsync(trigger.Id);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(0, (await h.Store.GetTriggerAsync(trigger.Id))!.MissedRuns);
        Assert.Equal(1, h.World.Executions("missed"));
    }

    [Fact]
    public async Task Schedule_with_invalid_settings_is_rejected()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(Simple("x"));
        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.SaveTriggerAsync(Every(scenario, 0)));
        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.SaveTriggerAsync(Every(scenario, 30) with
        {
            Config = new JsonObject { ["mode"] = "once", ["at"] = "2020-01-01T10:00" },
        }));
        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.SaveTriggerAsync(Every("missing", 30)));
    }

    [Fact]
    public async Task Event_trigger_starts_process_once_with_values_from_event()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(Simple("order-{{ vars.orderId }}"));
        await h.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "",
            ScenarioId = scenario,
            Kind = TriggerKind.Event,
            Name = "Новый заказ",
            Config = new JsonObject { ["source"] = "http", ["eventType"] = "order", ["filter"] = "event.payload.total > 100" },
            Inputs = new JsonObject { ["orderId"] = "event.payload.id" },
            CreatedAt = default,
            UpdatedAt = default,
        });

        var small = await h.Engine.PublishEventAsync(new EventInput
        {
            Source = "http", Type = "order", ExternalId = "o-1", Payload = new JsonObject { ["id"] = 1, ["total"] = 50 },
        });
        var big = await h.Engine.PublishEventAsync(new EventInput
        {
            Source = "http", Type = "order", ExternalId = "o-2", Payload = new JsonObject { ["id"] = 2, ["total"] = 500 },
        });
        await h.Engine.PublishEventAsync(new EventInput
        {
            Source = "http", Type = "order", ExternalId = "o-2", Payload = new JsonObject { ["id"] = 2, ["total"] = 500 },
        });

        await WaitUntilAsync(async () => h.World.Executions("order-2") == 1, h);
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Single(await ProcessesAsync(h, scenario));
        Assert.Equal(0, h.World.Executions("order-1"));
        Assert.Equal(EventState.Unmatched, (await h.Store.GetEventAsync(small.EventId))!.State);
        var delivered = (await h.Store.GetEventAsync(big.EventId))!;
        Assert.Equal(EventState.Delivered, delivered.State);
        Assert.Single(delivered.StartedProcesses!);
    }

    [Fact]
    public async Task Schedule_that_cannot_start_reports_the_failure_once_until_the_error_changes()
    {
        await using var h = await EngineHarness.CreateAsync();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<TriggerFailure>();
        h.Engine.TriggerFailed += failures.Enqueue;
        var scenario = await h.CreateScenarioAsync(Simple("tick"));
        var trigger = await h.Engine.SaveTriggerAsync(Every(scenario, 30));
        // Сценарий стал черновиком с ошибками — автозапуск не может запустить процесс.
        var stored = (await h.Store.GetScenarioAsync(scenario))!;
        await h.Scenarios.UpdateAsync(scenario, new ScenarioBuilder("По расписанию").Node("end", "end").Build(), stored.Version);

        await WaitUntilAsync(async () => (await h.Store.GetTriggerAsync(trigger.Id))!.LastError is not null, h, TimeSpan.FromSeconds(10));
        var failure = Assert.Single(failures);
        Assert.Equal(trigger.Id, failure.TriggerId);
        Assert.Equal("Каждые N минут", failure.TriggerName);
        Assert.Equal("По расписанию", failure.ScenarioName);
        Assert.Contains("ошибки", failure.Error);

        // Следующий запуск с той же ошибкой — не новость.
        var lastRun = (await h.Store.GetTriggerAsync(trigger.Id))!.LastRunAt;
        await WaitUntilAsync(async () => (await h.Store.GetTriggerAsync(trigger.Id))!.LastRunAt > lastRun, h, TimeSpan.FromSeconds(10));
        Assert.Single(failures);
        Assert.Empty(await ProcessesAsync(h, scenario));
    }

    [Fact]
    public async Task Event_that_matched_a_failing_autostart_says_so_instead_of_nothing_matched()
    {
        await using var h = await EngineHarness.CreateAsync();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<TriggerFailure>();
        h.Engine.TriggerFailed += failures.Enqueue;
        var scenario = await h.CreateScenarioAsync(Simple("order"));
        await h.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "",
            ScenarioId = scenario,
            Kind = TriggerKind.Event,
            Name = "Новый заказ",
            Config = new JsonObject { ["source"] = "http", ["eventType"] = "order" },
            Inputs = new JsonObject { ["orderId"] = "event.payload.missing.id" },
            CreatedAt = default,
            UpdatedAt = default,
        });

        var published = await h.Engine.PublishEventAsync(new EventInput
        {
            Source = "http", Type = "order", ExternalId = "o-9", Payload = new JsonObject { ["id"] = 9 },
        });
        await WaitUntilAsync(async () => (await h.Store.GetEventAsync(published.EventId))!.State == EventState.Unmatched, h);
        var record = (await h.Store.GetEventAsync(published.EventId))!;
        Assert.StartsWith("Автозапуск не запустил процесс — «Новый заказ»:", record.Note);
        Assert.Contains("«Сопоставить снова»", record.Note);
        var failure = Assert.Single(failures);
        Assert.Equal(TriggerKind.Event, failure.Kind);
        Assert.Empty(await ProcessesAsync(h, scenario));
    }

    private static TriggerRecord OnOrder(string scenarioId, string name, JsonObject? inputs = null, string? filter = null) => new()
    {
        Id = "",
        ScenarioId = scenarioId,
        Kind = TriggerKind.Event,
        Name = name,
        Config = filter is null ? new JsonObject { ["source"] = "shop" } : new JsonObject { ["source"] = "shop", ["filter"] = filter },
        Inputs = inputs,
        CreatedAt = default,
        UpdatedAt = default,
    };

    [Fact]
    public async Task Event_that_started_one_autostart_still_tells_which_other_failed()
    {
        await using var h = await EngineHarness.CreateAsync();
        var good = await h.CreateScenarioAsync(Simple("good"));
        var bad = await h.CreateScenarioAsync(Simple("bad"));
        await h.Engine.SaveTriggerAsync(OnOrder(good, "Хороший"));
        await h.Engine.SaveTriggerAsync(OnOrder(bad, "Плохой", new JsonObject { ["orderId"] = "event.payload.missing.id" }));

        var published = await h.Engine.PublishEventAsync(new EventInput { Source = "shop", ExternalId = "s-1", Payload = new JsonObject { ["id"] = 1 } });
        var record = await h.WaitForEventAsync(published.EventId, EventState.Delivered);
        Assert.StartsWith("Запущено процессов по триггерам: 1. Не запустились — «Плохой»:", record.Note);
    }

    [Fact]
    public async Task Filter_that_cannot_be_evaluated_is_not_called_a_match()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(Simple("order"));
        await h.Engine.SaveTriggerAsync(OnOrder(scenario, "Крупный заказ", filter: "event.payload.order.total > 1000"));

        var ping = await h.Engine.PublishEventAsync(new EventInput { Source = "shop", ExternalId = "p-1", Payload = new JsonObject { ["ping"] = true } });
        var record = await h.WaitForEventAsync(ping.EventId, EventState.Unmatched);
        Assert.Contains("«Крупный заказ»: условие автозапуска не вычислилось — подходит ли событие, неизвестно", record.Note);
        Assert.DoesNotContain("Подошло", record.Note);

        var result = await h.Engine.RerouteEventAsync(ping.EventId);
        Assert.StartsWith("Автозапуск не запустил процесс", result.Message);
    }

    [Fact]
    public async Task Manual_run_after_a_failure_clears_the_error()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(Simple("tick"));
        var trigger = await h.Engine.SaveTriggerAsync(Every(scenario, 30));
        var stored = (await h.Store.GetScenarioAsync(scenario))!;
        var broken = await h.Scenarios.UpdateAsync(scenario, new ScenarioBuilder("По расписанию").Node("end", "end").Build(), stored.Version);
        await WaitUntilAsync(async () => (await h.Store.GetTriggerAsync(trigger.Id))!.LastError is not null, h, TimeSpan.FromSeconds(10));

        await h.Scenarios.UpdateAsync(scenario, Simple("tick"), broken.Version);
        await h.Engine.RunTriggerNowAsync(trigger.Id);
        Assert.Null((await h.Store.GetTriggerAsync(trigger.Id))!.LastError);
    }

    [Fact]
    public async Task Waiting_process_takes_event_before_triggers()
    {
        await using var h = await EngineHarness.CreateAsync();
        var waiter = await h.CreateScenarioAsync(new ScenarioBuilder("Ждёт")
            .Node("start", "start").Node("w", "event.wait", new { source = "http", eventType = "order" }).Node("end", "end")
            .Edge("start", "w").Edge("w", "end", Ports.Event).Build());
        var started = await h.CreateScenarioAsync(Simple("by-trigger"));
        await h.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "", ScenarioId = started, Kind = TriggerKind.Event, Config = new JsonObject { ["source"] = "http" },
            CreatedAt = default, UpdatedAt = default,
        });

        var waiting = await h.Engine.StartProcessAsync(waiter);
        await h.WaitForStatusAsync(waiting, ProcessStatus.Waiting);
        await h.Engine.PublishEventAsync(new EventInput { Source = "http", Type = "order" });
        await h.WaitForStatusAsync(waiting, ProcessStatus.Completed);
        await h.IdleAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, h.World.Executions("by-trigger"));
    }

    [Fact]
    public async Task Event_trigger_with_a_key_starts_only_for_events_with_that_key()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = await h.CreateScenarioAsync(Simple("file-{{ vars.orderId }}"));
        await h.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "", ScenarioId = scenario, Kind = TriggerKind.Event,
            Config = new JsonObject { ["source"] = "files", ["eventType"] = "created", ["correlationKey"] = "входящие" },
            Inputs = new JsonObject { ["orderId"] = "event.payload.name" },
            CreatedAt = default, UpdatedAt = default,
        });

        var other = await h.Engine.PublishEventAsync(new EventInput
        {
            Source = "files", Type = "created", CorrelationKey = "архив", Payload = new JsonObject { ["name"] = "a" },
        });
        await h.Engine.PublishEventAsync(new EventInput
        {
            Source = "files", Type = "created", CorrelationKey = "входящие", Payload = new JsonObject { ["name"] = "b" },
        });

        await WaitUntilAsync(async () => h.World.Executions("file-b") == 1, h);
        await h.IdleAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, h.World.Executions("file-a"));
        Assert.Equal(EventState.Unmatched, (await h.Store.GetEventAsync(other.EventId))!.State);
    }

    [Fact]
    public void Schedule_calculator_handles_days_and_one_time_runs()
    {
        var utc = TimeZoneInfo.Utc;
        var monday = new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero); // понедельник
        var weekdays = new JsonObject { ["mode"] = "daily", ["time"] = "09:30", ["days"] = new JsonArray(1, 2, 3, 4, 5) };
        Assert.Equal(new DateTimeOffset(2026, 3, 3, 9, 30, 0, TimeSpan.Zero), ScheduleCalculator.Next(weekdays, monday, utc));

        var friday = new DateTimeOffset(2026, 3, 6, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 9, 30, 0, TimeSpan.Zero), ScheduleCalculator.Next(weekdays, friday, utc));

        var once = new JsonObject { ["mode"] = "once", ["at"] = "2026-03-02T12:00" };
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero), ScheduleCalculator.Next(once, monday, utc));
        Assert.Null(ScheduleCalculator.Next(once, monday.AddHours(3), utc));

        var hourly = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = 60 };
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 11, 0, 0, TimeSpan.Zero), ScheduleCalculator.Next(hourly, monday, utc));
        Assert.Equal(3, ScheduleCalculator.CountBetween(hourly, monday, monday.AddHours(3), utc));

        // Интервал отсчитывается от местной полуночи: в зоне +05:30 «каждый час» — в :00 по местному времени.
        var india = TimeZoneInfo.CreateCustomTimeZone("test+0530", TimeSpan.FromMinutes(330), "test", "test");
        Assert.Equal(new DateTimeOffset(2026, 3, 2, 11, 0, 0, TimeSpan.FromMinutes(330)),
            ScheduleCalculator.Next(hourly, new DateTimeOffset(2026, 3, 2, 10, 10, 0, TimeSpan.FromMinutes(330)), india));

        Assert.NotNull(ScheduleCalculator.Validate(new JsonObject { ["mode"] = "daily", ["time"] = "25:00" }));
        Assert.NotNull(ScheduleCalculator.Validate(new JsonObject { ["mode"] = "daily", ["time"] = "09:00", ["days"] = new JsonArray(8) }));
        Assert.Null(ScheduleCalculator.Validate(weekdays));
        Assert.Equal("пн, вт, ср, чт, пт в 09:30", ScheduleCalculator.Describe(weekdays));
    }

    /// <summary>Зона с переводом часов, как в Европе: +1, летом +2; вперёд — в последнее воскресенье марта в 02:00, назад — октября в 03:00.</summary>
    private static TimeZoneInfo Europe() => TimeZoneInfo.CreateCustomTimeZone("test-dst", TimeSpan.FromHours(1), "test", "зима", "лето",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
    ]);

    private static List<string> Runs(JsonObject schedule, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var runs = new List<string>();
        var cursor = from;
        while (ScheduleCalculator.Next(schedule, cursor, zone) is { } next && next <= to)
        {
            runs.Add(TimeZoneInfo.ConvertTime(next, zone).ToString("dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));
            cursor = next;
        }

        return runs;
    }

    [Fact]
    public void Interval_dividing_the_day_keeps_local_hours_on_clock_change_days()
    {
        // «Каждые 6 часов» в день перевода часов — по-прежнему 00:00, 06:00, 12:00, 18:00 по местному времени. Раньше запуски
        // сдвигались на час, а при переводе назад два запуска выпадали на 23:00 и 00:00.
        var zone = Europe();
        var six = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = 360 };
        var spring = new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.FromHours(1));
        Assert.Equal(["29 00:00", "29 06:00", "29 12:00", "29 18:00", "30 00:00"], Runs(six, spring, spring.AddHours(25), zone));
        var autumn = new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal(["25 00:00", "25 06:00", "25 12:00", "25 18:00", "26 00:00"], Runs(six, autumn, autumn.AddHours(26), zone));

        var twelve = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = 720 };
        Assert.Equal(["25 00:00", "25 12:00", "26 00:00"], Runs(twelve, autumn, autumn.AddHours(26), zone));

        // Несуществующее 02:00 (часы переведены вперёд) — в 03:00; дальше — как обычно.
        var two = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = 120 };
        Assert.Equal(["29 00:00", "29 03:00", "29 04:00", "29 06:00"], Runs(two, spring, spring.AddHours(7), zone));

        // Частый интервал идёт ровной сеткой и через повторяющийся час: за 25 часов дня перевода назад — 50 запусков по 30 минут.
        var half = new JsonObject { ["mode"] = "interval", ["everyMinutes"] = 30 };
        var midnight = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal(50, ScheduleCalculator.CountBetween(half, midnight, midnight.AddHours(25), zone));
    }

    // ── Цепочки запусков по событиям процессов (D-090) ──

    private static ScenarioDefinition Sender(string key, string eventType) =>
        new ScenarioBuilder($"Отправитель {key}")
            .Node("start", "start")
            .Node("a", Action, new { key })
            .Node("send", "event.send", new { source = "process", eventType })
            .Node("end", "end")
            .Chain("start", "a", "send", "end")
            .Build();

    private static TriggerRecord OnProcessEvent(string scenarioId, string name, string? eventType) => new()
    {
        Id = "",
        ScenarioId = scenarioId,
        Kind = TriggerKind.Event,
        Name = name,
        Config = eventType is null ? new JsonObject { ["source"] = "process" } : new JsonObject { ["source"] = "process", ["eventType"] = eventType },
        CreatedAt = default,
        UpdatedAt = default,
    };

    [Fact]
    public async Task Event_trigger_does_not_restart_the_scenario_that_sent_the_event()
    {
        // Сценарий отправляет событие, а его же автозапуск ждёт события этого источника без уточнения вида. Раньше каждый
        // процесс запускал следующий — сотни процессов и повторов внешнего действия за секунды.
        await using var h = await EngineHarness.CreateAsync();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<TriggerFailure>();
        h.Engine.TriggerFailed += failures.Enqueue;
        var scenario = await h.CreateScenarioAsync(Sender("x", "ready"));
        var trigger = await h.Engine.SaveTriggerAsync(OnProcessEvent(scenario, "Сам себя", null));

        var id = await h.Engine.StartProcessAsync(scenario);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        await WaitUntilAsync(async () => (await h.Store.GetTriggerAsync(trigger.Id))!.LastError is not null, h);
        await h.IdleAsync(TimeSpan.FromSeconds(5));

        Assert.Single(await ProcessesAsync(h, scenario));
        Assert.Equal(1, h.World.Executions("x"));
        Assert.Contains("запустил бы сам себя", (await h.Store.GetTriggerAsync(trigger.Id))!.LastError);
        Assert.Contains("запустил бы сам себя", Assert.Single(failures).Error);
    }

    [Fact]
    public async Task Event_triggers_do_not_bounce_between_two_scenarios_forever()
    {
        await using var h = await EngineHarness.CreateAsync();
        var a = await h.CreateScenarioAsync(Sender("a", "ping"));
        var b = await h.CreateScenarioAsync(Sender("b", "pong"));
        await h.Engine.SaveTriggerAsync(OnProcessEvent(b, "На ping", "ping"));
        var back = await h.Engine.SaveTriggerAsync(OnProcessEvent(a, "На pong", "pong"));

        var id = await h.Engine.StartProcessAsync(a);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        await WaitUntilAsync(async () => (await h.Store.GetTriggerAsync(back.Id))!.LastError is not null, h);
        await h.IdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, h.World.Executions("a"));
        Assert.Equal(1, h.World.Executions("b"));
        var started = Assert.Single(await ProcessesAsync(h, b));
        Assert.Equal(1, started.Depth); // запущен событием процесса — продолжает его цепочку
        Assert.Contains("запустил бы сам себя", (await h.Store.GetTriggerAsync(back.Id))!.LastError);
    }

    [Fact]
    public async Task Event_trigger_chain_longer_than_the_call_depth_limit_stops_with_an_error()
    {
        // Разные сценарии запускают друг друга по цепочке: как и вложенные вызовы, цепочка не длиннее 8.
        await using var h = await EngineHarness.CreateAsync();
        var scenarios = new List<string>();
        for (var i = 0; i <= 9; i++)
        {
            scenarios.Add(await h.CreateScenarioAsync(Sender($"s{i}", $"e{i}")));
        }

        var triggers = new List<TriggerRecord>();
        for (var i = 1; i <= 9; i++)
        {
            triggers.Add(await h.Engine.SaveTriggerAsync(OnProcessEvent(scenarios[i], $"Звено {i}", $"e{i - 1}")));
        }

        await h.Engine.StartProcessAsync(scenarios[0]);
        await WaitUntilAsync(async () => (await h.Store.GetTriggerAsync(triggers[^1].Id))!.LastError is not null, h);
        await h.IdleAsync(TimeSpan.FromSeconds(5));

        for (var i = 0; i <= 8; i++)
        {
            Assert.Equal(1, h.World.Executions($"s{i}"));
        }

        Assert.Equal(0, h.World.Executions("s9"));
        Assert.Contains("слишком длинная цепочка", (await h.Store.GetTriggerAsync(triggers[^1].Id))!.LastError);
    }
}
