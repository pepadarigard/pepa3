using System.Text.Json.Nodes;

namespace Pepa.Host.Tests;

/// <summary>API событий, ресурсов и запусков по расписанию — на настоящем экземпляре Pepa.</summary>
public sealed class PlatformApiTests
{
    private static async Task<string> RunAsync(HostFixture f, string scenarioId, object? inputs = null)
    {
        var (status, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenarioId}/run", new { inputs });
        Assert.Equal(200, status);
        return run!["processId"]!.GetValue<string>();
    }

    [Fact]
    public async Task Reply_sample_waits_for_event_holds_resource_and_saves_answer()
    {
        await using var f = await HostFixture.StartAsync();
        var scenarioId = await f.ScenarioIdAsync("Пример 5");
        var processId = await RunAsync(f, scenarioId, new { order = "Z-7" });

        var waiting = await f.WaitForProcessAsync(processId, p => HostFixture.Status(p) == "waiting" && p["waitKind"]?.GetValue<string>() == "event");
        Assert.Contains("Z-7", waiting["process"]!["waitingFor"]!.GetValue<string>());
        Assert.Equal("пример:профиль-1", waiting["heldResources"]![0]!.GetValue<string>());
        Assert.Equal("active", waiting["waits"]![0]!["state"]!.GetValue<string>());

        var resources = (await f.GetAsync("/api/resources")).AsArray();
        Assert.Equal(processId, resources.Single()!["holder"]!["id"]!.GetValue<string>());

        var (status, published) = await f.SendAsync(HttpMethod.Post, "/api/events", new
        {
            source = "manual", type = "reply", correlationKey = "Z-7", externalId = "reply-1", payload = "согласовано",
        });
        Assert.Equal(200, status);
        Assert.False(published!["duplicate"]!.GetValue<bool>());

        await f.WaitForProcessAsync(processId, p => HostFixture.Status(p) == "completed");
        var text = await File.ReadAllTextAsync(Path.Combine(f.Workspace, "примеры", "ответы", "Z-7.txt"));
        Assert.Equal("Ответ по заказу Z-7: согласовано", text);
        Assert.Null((await f.GetAsync("/api/resources")).AsArray().Single()!["holder"]);

        var (_, repeat) = await f.SendAsync(HttpMethod.Post, "/api/events", new
        {
            source = "manual", type = "reply", correlationKey = "Z-7", externalId = "reply-1", payload = "ещё раз",
        });
        Assert.True(repeat!["duplicate"]!.GetValue<bool>());

        var events = (await f.GetAsync("/api/events")).AsArray();
        var delivered = events.Single()!;
        Assert.Equal("delivered", delivered["state"]!.GetValue<string>());
        Assert.Equal(processId, delivered["process"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Conflicting_event_is_listed_and_resolved_by_user()
    {
        await using var f = await HostFixture.StartAsync();
        var scenarioId = await f.ScenarioIdAsync("Пример 5");
        // Ресурс пропускает только один процесс; второй ждёт в очереди — конфликт создаём другим сценарием без ресурса.
        var definition = (await f.GetAsync($"/api/scenarios/{scenarioId}"))["definition"]!.AsObject();
        definition.Remove("id");
        definition["name"] = "Ждать без ключа";
        var nodes = definition["nodes"]!.AsArray();
        nodes.Single(n => n!["id"]!.GetValue<string>() == "wait")!["config"]!.AsObject().Remove("correlationKey");
        nodes.Single(n => n!["id"]!.GetValue<string>() == "lock")!["config"]!["key"] = "{{ vars.order }}";
        nodes.Single(n => n!["id"]!.GetValue<string>() == "free")!["config"]!["key"] = "{{ vars.order }}";
        var (createStatus, created) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", definition);
        Assert.Equal(200, createStatus);
        var noKey = created!["id"]!.GetValue<string>();

        var first = await RunAsync(f, noKey, new { order = "1" });
        var second = await RunAsync(f, noKey, new { order = "2" });
        await f.WaitForProcessAsync(first, p => p["waitKind"]?.GetValue<string>() == "event");
        await f.WaitForProcessAsync(second, p => p["waitKind"]?.GetValue<string>() == "event");

        var (_, published) = await f.SendAsync(HttpMethod.Post, "/api/events", new { source = "manual", type = "reply", payload = "кому?" });
        var eventId = published!["eventId"]!.GetValue<long>();
        JsonNode conflict;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var open = (await f.GetAsync("/api/events?view=conflict")).AsArray();
            if (open.Count == 1)
            {
                conflict = open[0]!;
                break;
            }

            Assert.True(DateTime.UtcNow < deadline, "Конфликт не появился.");
            await Task.Delay(50);
        }

        Assert.Equal(1, (await f.GetAsync("/api/overview"))["eventConflicts"]!.GetValue<int>());
        var candidates = conflict["candidates"]!.AsArray();
        Assert.Equal(2, candidates.Count);
        var chosen = candidates.Single(c => c!["process"]!["id"]!.GetValue<string>() == second)!;

        var (resolveStatus, _) = await f.SendAsync(HttpMethod.Post, $"/api/events/{eventId}/resolve",
            new { subscriptionId = chosen["subscriptionId"]!.GetValue<long>() });
        Assert.Equal(200, resolveStatus);
        await f.WaitForProcessAsync(second, p => HostFixture.Status(p) == "completed");
        Assert.Equal("waiting", HostFixture.Status((await f.GetAsync($"/api/processes/{first}"))["process"]!));

        var (againStatus, _) = await f.SendAsync(HttpMethod.Post, $"/api/events/{eventId}/resolve", new { subscriptionId = (long?)null });
        Assert.Equal(409, againStatus);
    }

    [Fact]
    public async Task Triggers_can_be_created_listed_run_and_deleted()
    {
        await using var f = await HostFixture.StartAsync();
        var scenarioId = await f.ScenarioIdAsync("Пример 1.");

        var (badStatus, bad) = await f.SendAsync(HttpMethod.Post, "/api/triggers", new
        {
            scenarioId, kind = "schedule", enabled = true, config = new { mode = "daily", time = "9:3" },
        });
        Assert.Equal(409, badStatus);
        Assert.Contains("ЧЧ:ММ", bad!["error"]!.GetValue<string>());

        var (status, created) = await f.SendAsync(HttpMethod.Post, "/api/triggers", new
        {
            scenarioId, kind = "schedule", enabled = true, name = "Утром", config = new { mode = "daily", time = "09:30", days = new[] { 1, 2, 3, 4, 5 } },
            inputs = new { name = "расписание" },
        });
        Assert.Equal(200, status);
        var triggerId = created!["id"]!.GetValue<string>();
        Assert.Equal("пн, вт, ср, чт, пт в 09:30", created["description"]!.GetValue<string>());
        Assert.NotNull(created["nextRunAt"]);
        Assert.Single((await f.GetAsync($"/api/triggers?scenarioId={scenarioId}")).AsArray());
        Assert.Single((await f.GetAsync("/api/overview"))["upcomingTriggers"]!.AsArray());

        var (runStatus, run) = await f.SendAsync(HttpMethod.Post, $"/api/triggers/{triggerId}/run");
        Assert.Equal(200, runStatus);
        await f.WaitForProcessAsync(run!["processId"]!.GetValue<string>(), p => HostFixture.Status(p) == "completed");
        var text = await File.ReadAllTextAsync(Path.Combine(f.Workspace, "примеры", "привет.txt"));
        Assert.StartsWith("Привет, расписание!", text);

        var (updateStatus, updated) = await f.SendAsync(HttpMethod.Put, $"/api/triggers/{triggerId}", new
        {
            scenarioId, kind = "schedule", enabled = false, name = "Выключено", config = new { mode = "interval", everyMinutes = 15 },
        });
        Assert.Equal(200, updateStatus);
        Assert.Null(updated!["nextRunAt"]);

        Assert.Equal(200, (await f.SendAsync(HttpMethod.Delete, $"/api/triggers/{triggerId}")).Status);
        Assert.Empty((await f.GetAsync("/api/triggers")).AsArray());
        Assert.Equal(404, (await f.SendAsync(HttpMethod.Delete, $"/api/triggers/{triggerId}")).Status);
    }

    [Fact]
    public async Task Event_trigger_starts_scenario_from_api_event()
    {
        await using var f = await HostFixture.StartAsync();
        var scenarioId = await f.ScenarioIdAsync("Пример 1.");
        var (status, _) = await f.SendAsync(HttpMethod.Post, "/api/triggers", new
        {
            scenarioId, kind = "event", enabled = true, config = new { source = "http", eventType = "hello" },
            inputs = new { name = "event.payload.who" },
        });
        Assert.Equal(200, status);

        await f.SendAsync(HttpMethod.Post, "/api/events", new { source = "http", type = "hello", payload = new { who = "вебхук" } });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var path = Path.Combine(f.Workspace, "примеры", "привет.txt");
        while (!File.Exists(path) || !(await File.ReadAllTextAsync(path)).StartsWith("Привет, вебхук!", StringComparison.Ordinal))
        {
            Assert.True(DateTime.UtcNow < deadline, "Процесс по событию не отработал.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Resource_can_be_force_released()
    {
        await using var f = await HostFixture.StartAsync();
        var scenarioId = await f.ScenarioIdAsync("Пример 5");
        var holder = await RunAsync(f, scenarioId, new { order = "H" });
        await f.WaitForProcessAsync(holder, p => p["waitKind"]?.GetValue<string>() == "event");
        await f.SendAsync(HttpMethod.Post, $"/api/processes/{holder}/pause");
        var queued = await RunAsync(f, scenarioId, new { order = "Q" });
        await f.WaitForProcessAsync(queued, p => p["waitKind"]?.GetValue<string>() == "resource");
        Assert.Single((await f.GetAsync("/api/resources")).AsArray().Single()!["queue"]!.AsArray());

        var (status, message) = await f.SendAsync(HttpMethod.Post, "/api/resources/release", new { key = "пример:профиль-1" });
        Assert.Equal(200, status);
        Assert.Contains("передан", message!["message"]!.GetValue<string>());
        await f.WaitForProcessAsync(queued, p => p["waitKind"]?.GetValue<string>() == "event");

        Assert.Equal(409, (await f.SendAsync(HttpMethod.Post, "/api/resources/release", new { key = "нет-такого" })).Status);
    }
}

/// <summary>Отладка через API: пошаговый запуск и «Продолжить без остановок».</summary>
public sealed class DebugApiTests
{
    [Fact]
    public async Task Step_mode_run_pauses_and_can_continue_without_pauses()
    {
        await using var f = await HostFixture.StartAsync();
        var scenarioId = await f.ScenarioIdAsync("Пример 1.");
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenarioId}/run", new { stepMode = true });
        var id = run!["processId"]!.GetValue<string>();

        var paused = (await f.WaitForProcessAsync(id, p => p["status"]!.GetValue<string>() == "paused"))["process"]!;
        Assert.Equal("breakpoint", paused["pauseReason"]!.GetValue<string>());
        Assert.Contains(paused["allowedActions"]!.AsArray(), a => a!.GetValue<string>() == "runWithoutPauses");

        var (status, _) = await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/resume", new { action = "runWithoutPauses" });
        Assert.Equal(200, status);
        await f.WaitForProcessAsync(id, p => p["status"]!.GetValue<string>() == "completed");
    }
}
