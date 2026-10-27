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
}
