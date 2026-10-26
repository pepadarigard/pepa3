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
}
