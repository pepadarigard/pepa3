using Microsoft.Extensions.Logging.Abstractions;
using Pepa.Host.Backups;
using Pepa.Storage.Sqlite;

namespace Pepa.Host.Tests;

/// <summary>Резервные копии базы: ежедневная при запуске, по кнопке, старые удаляются.</summary>
public sealed class BackupApiTests
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public async Task Daily_copy_is_made_at_start_and_a_copy_can_be_made_by_hand()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        System.Text.Json.Nodes.JsonArray items;
        while ((items = (await f.GetAsync("/api/backups"))["items"]!.AsArray()).Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal("daily", Assert.Single(items)!["kind"]!.GetValue<string>());
        var (status, manual) = await f.SendAsync(HttpMethod.Post, "/api/backups");
        Assert.Equal(200, status);
        Assert.Equal("manual", manual!["kind"]!.GetValue<string>());
        var list = await f.GetAsync("/api/backups");
        Assert.Equal(2, list["items"]!.AsArray().Count);
        Assert.Null(list["lastError"]);
        Assert.Equal(Path.Combine(f.DataDirectory, "backups"), list["directory"]!.GetValue<string>());
    }

    [Fact]
    public async Task Daily_copy_is_made_once_a_day_and_old_copies_are_removed()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pepa-backups-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var store = new SqliteStore(Path.Combine(dir, "pepa.db"));
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            var time = new ManualTime(DateTimeOffset.UtcNow);
            await using var backups = new DatabaseBackups(store, time, NullLogger.Instance);
            var ct = TestContext.Current.CancellationToken;

            await backups.EnsureDailyAsync(ct);
            await backups.EnsureDailyAsync(ct);
            Assert.Single(backups.List());
            time.Now += TimeSpan.FromHours(25);
            await backups.EnsureDailyAsync(ct);
            Assert.Equal(2, backups.List().Count(b => b.Kind == "daily"));

            // Сделанных вручную хранится 10 — самые новые; в одну секунду — разные имена.
            for (var i = 0; i < 12; i++)
            {
                await backups.CreateAsync("manual", ct);
            }

            Assert.Equal(10, backups.List().Count(b => b.Kind == "manual"));
            Assert.Equal(2, backups.List().Count(b => b.Kind == "daily"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Restored_copy_stops_unfinished_processes_until_the_user_decides()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Ждёт",
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "wait", type = "wait", config = new { mode = "duration", amount = 10, unit = "minutes" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "wait" },
                new { id = "b", source = "wait", sourcePort = "out", target = "end" },
            },
        });
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario!["id"]!.GetValue<string>()}/run", new { });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");
        var (_, copy) = await f.SendAsync(HttpMethod.Post, "/api/backups");
        var copyPath = Path.Combine(f.DataDirectory, "backups", copy!["name"]!.GetValue<string>());

        await f.RestartAsync(() =>
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var db = Path.Combine(f.DataDirectory, "pepa.db");
            File.Copy(copyPath, db, overwrite: true);
            File.Delete(db + "-wal");
            File.Delete(db + "-shm");
        });

        Assert.Equal(1, (await f.GetAsync("/api/info"))["recovery"]!["restored"]!.GetValue<int>());
        var process = (await f.GetAsync($"/api/processes/{id}"))["process"]!;
        Assert.Equal("needsAttention", HostFixture.Status(process));
        Assert.Equal("restoredFromBackup", process["attention"]!.GetValue<string>());
        Assert.Equal(["continue", "cancel"], process["allowedActions"]!.AsArray().Select(a => a!.GetValue<string>()));

        // Отметка снята: при следующем запуске это обычная база, процесс по-прежнему ждёт решения.
        await f.RestartAsync();
        Assert.Equal(0, (await f.GetAsync("/api/info"))["recovery"]!["restored"]!.GetValue<int>());
        Assert.Equal("needsAttention", HostFixture.Status((await f.GetAsync($"/api/processes/{id}"))["process"]!));
    }
}
