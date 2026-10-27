namespace Pepa.Host.Tests;

/// <summary>Слежение за папками через API: настройка, запуск сценария по новому файлу, файлы без Pepa — без автозапуска.</summary>
public sealed class FolderWatchApiTests
{
    private static async Task<string> CreateScenarioAsync(HostFixture f)
    {
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Разобрать входящие",
            variables = new object[]
            {
                new { name = "path", type = "text", isInput = true },
                new { name = "name", type = "text", isInput = true },
            },
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "move", type = "file.move", config = new { source = "{{ vars.path }}", destination = "обработано/{{ vars.name }}" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "move" },
                new { id = "b", source = "move", sourcePort = "out", target = "end" },
            },
        });
        var id = scenario!["id"]!.GetValue<string>();
        var (status, trigger) = await f.SendAsync(HttpMethod.Post, "/api/triggers", new
        {
            scenarioId = id, kind = "event", enabled = true,
            config = new { source = "files", eventType = "created", correlationKey = "входящие" },
            inputs = new { path = "event.payload.path", name = "event.payload.name" },
        });
        Assert.True(status == 200, trigger?.ToJsonString());
        return id;
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task New_file_in_a_watched_folder_starts_the_scenario_with_its_path()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, saved) = await f.SendAsync(HttpMethod.Put, "/api/watches", new
        {
            watches = new[] { new { folder = "входящие\\", pattern = "*.txt", subfolders = false, changes = false, enabled = true } },
        });
        Assert.Equal(200, status);
        var watch = saved!["watches"]!.AsArray().Single()!;
        Assert.Equal("входящие", watch["folder"]!.GetValue<string>());
        Assert.Matches("^[a-z0-9]+$", watch["id"]!.GetValue<string>());
        Assert.True(Directory.Exists(Path.Combine(f.Workspace, "входящие")));
        await CreateScenarioAsync(f);
        // Слежение началось: файл, который уже был, не событие.
        await WaitForAsync(async () => (await f.GetAsync("/api/watches"))["watches"]![0]!["status"] is { } s && s["lastScanAt"] is not null,
            "Папка не проверена.");

        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "входящие", "счёт.txt"), "оплатить");
        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "входящие", "картинка.png"), "не тот шаблон");
        var moved = Path.Combine(f.Workspace, "обработано", "счёт.txt");
        await WaitForAsync(() => Task.FromResult(File.Exists(moved)), "Сценарий по новому файлу не отработал.");
        Assert.Equal("оплатить", await File.ReadAllTextAsync(moved));
        var events = (await f.GetAsync("/api/events")).AsArray();
        var e = Assert.Single(events, x => x!["source"]!.GetValue<string>() == "files")!;
        Assert.Equal("входящие/счёт.txt", e["payload"]!["path"]!.GetValue<string>());
        Assert.Single(e["startedProcesses"]!.AsArray());
    }

    [Fact]
    public async Task File_that_appeared_while_Pepa_was_closed_does_not_start_anything_by_itself()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        await f.SendAsync(HttpMethod.Put, "/api/watches", new { watches = new[] { new { folder = "входящие", pattern = "*" } } });
        await CreateScenarioAsync(f);
        await WaitForAsync(async () => (await f.GetAsync("/api/watches"))["watches"]![0]!["status"] is { } s && s["lastScanAt"] is not null,
            "Папка не проверена.");

        await f.RestartAsync(() => File.WriteAllText(Path.Combine(f.Workspace, "входящие", "без-pepa.txt"), "пока закрыта"));

        await WaitForAsync(async () => (await f.GetAsync("/api/events")).AsArray().Any(x => x!["source"]!.GetValue<string>() == "files"),
            "Файл, появившийся без Pepa, не стал событием.");
        var e = (await f.GetAsync("/api/events")).AsArray().Single(x => x!["source"]!.GetValue<string>() == "files")!;
        Assert.Empty(e["startedProcesses"]!.AsArray());
        Assert.True(File.Exists(Path.Combine(f.Workspace, "входящие", "без-pepa.txt")));
        Assert.Empty((await f.GetAsync("/api/processes")).AsArray());
    }

    [Theory]
    [InlineData("../вне", "*", "за пределы рабочей папки")]
    [InlineData("входящие", "папка/*.pdf", "без папок")]
    [InlineData("", "*", "Путь не указан")]
    public async Task Bad_watch_is_refused_with_a_reason(string folder, string pattern, string reason)
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, error) = await f.SendAsync(HttpMethod.Put, "/api/watches", new { watches = new[] { new { folder, pattern } } });
        Assert.Equal(400, status);
        Assert.Contains(reason, error!["error"]!.GetValue<string>());
        Assert.Empty((await f.GetAsync("/api/watches"))["watches"]!.AsArray());
    }

    [Fact]
    public async Task Same_folder_twice_is_refused_and_general_settings_keep_the_watches()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, error) = await f.SendAsync(HttpMethod.Put, "/api/watches", new
        {
            watches = new[] { new { folder = "входящие", pattern = "*" }, new { folder = "входящие/", pattern = "*.pdf" } },
        });
        Assert.Equal(400, status);
        Assert.Contains("уже следит", error!["error"]!.GetValue<string>());

        Assert.Equal(200, (await f.SendAsync(HttpMethod.Put, "/api/watches", new { watches = new[] { new { folder = "входящие", pattern = "*" } } })).Status);
        var settings = await f.GetAsync("/api/settings");
        Assert.Equal(200, (await f.SendAsync(HttpMethod.Put, "/api/settings", settings)).Status);
        Assert.Single((await f.GetAsync("/api/watches"))["watches"]!.AsArray());
    }

    [Fact]
    public async Task Folder_inside_a_watch_with_subfolders_is_refused()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, error) = await f.SendAsync(HttpMethod.Put, "/api/watches", new
        {
            watches = new object[] { new { folder = "входящие", pattern = "*", subfolders = true }, new { folder = "входящие/сканы", pattern = "*.pdf" } },
        });
        Assert.Equal(400, status);
        Assert.Contains("уже входит в слежение", error!["error"]!.GetValue<string>());
        // Без вложенных папок — можно: файлы разные.
        Assert.Equal(200, (await f.SendAsync(HttpMethod.Put, "/api/watches", new
        {
            watches = new object[] { new { folder = "входящие", pattern = "*", subfolders = false }, new { folder = "входящие/сканы", pattern = "*.pdf" } },
        })).Status);
    }

    [Fact]
    public async Task Trigger_key_for_files_is_written_like_the_watch_and_must_be_watched()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        await f.SendAsync(HttpMethod.Put, "/api/watches", new { watches = new[] { new { folder = "входящие", pattern = "*" } } });
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Файл",
            nodes = new object[] { new { id = "start", type = "start" }, new { id = "end", type = "end" } },
            edges = new object[] { new { id = "a", source = "start", sourcePort = "out", target = "end" } },
        });
        var scenarioId = scenario!["id"]!.GetValue<string>();
        var (status, trigger) = await f.SendAsync(HttpMethod.Post, "/api/triggers", new
        {
            scenarioId, kind = "event", enabled = true, config = new { source = "files", eventType = "created", correlationKey = "входящие/" },
        });
        Assert.Equal(200, status);
        Assert.Equal("входящие", trigger!["config"]!["correlationKey"]!.GetValue<string>());
        Assert.Contains("с ключом «входящие»", trigger["description"]!.GetValue<string>());

        var (refused, why) = await f.SendAsync(HttpMethod.Post, "/api/triggers", new
        {
            scenarioId, kind = "event", enabled = true, config = new { source = "files", correlationKey = "загрузки" },
        });
        Assert.Equal(400, refused);
        Assert.Contains("не следит", why!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Watch_that_a_process_waits_on_cannot_be_turned_off()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        await f.SendAsync(HttpMethod.Put, "/api/watches", new { watches = new[] { new { folder = "входящие", pattern = "*" } } });
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Ждёт файл",
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "wait", type = "file.wait", config = new { folder = "входящие" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "wait" },
                new { id = "b", source = "wait", sourcePort = "event", target = "end" },
            },
        });
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario!["id"]!.GetValue<string>()}/run", new { });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");

        var (status, error) = await f.SendAsync(HttpMethod.Put, "/api/watches", new { watches = Array.Empty<object>() });
        Assert.Equal(409, status);
        Assert.Contains("ждут процессы", error!["error"]!.GetValue<string>());
        Assert.Single((await f.GetAsync("/api/watches"))["watches"]!.AsArray());

        await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/cancel");
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "cancelled");
        Assert.Equal(200, (await f.SendAsync(HttpMethod.Put, "/api/watches", new { watches = Array.Empty<object>() })).Status);
    }

    [Fact]
    public async Task Watch_used_by_a_trigger_cannot_be_turned_off_but_the_trigger_can()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        await f.SendAsync(HttpMethod.Put, "/api/watches", new { watches = new[] { new { folder = "входящие", pattern = "*" } } });
        var scenarioId = await CreateScenarioAsync(f);
        var trigger = (await f.GetAsync($"/api/triggers?scenarioId={scenarioId}")).AsArray().Single()!;
        var watchId = (await f.GetAsync("/api/watches"))["watches"]![0]!["id"]!.GetValue<string>();

        var (status, error) = await f.SendAsync(HttpMethod.Put, "/api/watches", new
        {
            watches = new[] { new { id = watchId, folder = "входящие", pattern = "*", enabled = false } },
        });
        Assert.Equal(409, status);
        Assert.Contains("автозапуски", error!["error"]!.GetValue<string>());
        // Отказ ничего не создаёт и не меняет.
        Assert.False(Directory.Exists(Path.Combine(f.Workspace, "другая")));
        Assert.Equal(409, (await f.SendAsync(HttpMethod.Put, "/api/watches", new
        {
            watches = new object[] { new { folder = "другая", pattern = "*" } },
        })).Status);
        Assert.False(Directory.Exists(Path.Combine(f.Workspace, "другая")));

        // Выключить автозапуск можно, потом — и слежение; выключенный автозапуск сохраняется и без слежения.
        var off = new
        {
            scenarioId, kind = "event", enabled = false,
            config = new { source = "files", eventType = "created", correlationKey = "входящие" },
            inputs = new { path = "event.payload.path", name = "event.payload.name" },
        };
        Assert.Equal(200, (await f.SendAsync(HttpMethod.Put, $"/api/triggers/{trigger["id"]!.GetValue<string>()}", off)).Status);
        Assert.Equal(200, (await f.SendAsync(HttpMethod.Put, "/api/watches", new
        {
            watches = new[] { new { id = watchId, folder = "входящие", pattern = "*", enabled = false } },
        })).Status);
        Assert.Equal(200, (await f.SendAsync(HttpMethod.Put, $"/api/triggers/{trigger["id"]!.GetValue<string>()}", off)).Status);
        // Включить по выключенному слежению — нельзя.
        var (refused, why) = await f.SendAsync(HttpMethod.Put, $"/api/triggers/{trigger["id"]!.GetValue<string>()}", off with { enabled = true });
        Assert.Equal(400, refused);
        Assert.Contains("выключено", why!["error"]!.GetValue<string>());
    }
}
