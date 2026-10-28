using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pepa.Host.Backups;
using Pepa.Host.Snapshots;
using Pepa.Storage.Sqlite;

namespace Pepa.Host.Tests;

/// <summary>Снимки экрана при ошибках: папка процесса, лимит, выдача по API, удаление вместе с историей.</summary>
public sealed class SnapshotApiTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    [Fact]
    public async Task Process_keeps_a_limited_number_of_snapshots_and_names_cannot_leave_its_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "pepa-snapshots", Guid.NewGuid().ToString("N"));
        var folder = new SnapshotFolder(root);
        var id = Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var names = new List<string?>();
            for (var i = 0; i < SnapshotFolder.MaxPerProcess; i++)
            {
                names.Add(await folder.SaveAsync(id, Jpeg, ".jpg", ct));
            }

            Assert.Equal("001.jpg", names[0]);
            Assert.Equal($"{SnapshotFolder.MaxPerProcess:000}.jpg", names[^1]);
            Assert.Null(await folder.SaveAsync(id, Jpeg, ".jpg", ct));
            Assert.Null(await folder.SaveAsync("../other", Jpeg, ".jpg", ct));
            Assert.Null(await folder.SaveAsync(Guid.NewGuid().ToString("N"), Jpeg, ".exe", ct));
            Assert.Null(await folder.SaveAsync(Guid.NewGuid().ToString("N"), [], ".jpg", ct));

            Assert.NotNull(folder.Find(id, "001.jpg"));
            Assert.Null(folder.Find(id, "../001.jpg"));
            Assert.Null(folder.Find(id, "999.jpg"));
            Assert.Null(folder.Find("..", "001.jpg"));

            // Процесса больше нет в истории — его снимки удаляются; остальные остаются.
            var kept = Guid.NewGuid().ToString("N");
            await folder.SaveAsync(kept, Jpeg, ".png", ct);
            Assert.Equal(1, await folder.DeleteOrphansAsync(p => Task.FromResult(p == kept), ct));
            Assert.Null(folder.Find(id, "001.jpg"));
            Assert.NotNull(folder.Find(kept, "001.png"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Snapshot_is_served_for_its_process_and_removed_with_the_history()
    {
        await using var f = await HostFixture.StartAsync();
        var scenario = await f.ScenarioIdAsync("Пример 1.");
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario}/run", new { inputs = new { name = "Снимок" } });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "completed");
        var folder = f.Host.Services.GetRequiredService<SnapshotFolder>();
        var name = await folder.SaveAsync(id, Jpeg, ".jpg", TestContext.Current.CancellationToken);

        var response = await f.Client.GetAsync($"/api/processes/{id}/snapshots/{name}", TestContext.Current.CancellationToken);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Jpeg, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(404, (await f.SendAsync(HttpMethod.Get, $"/api/processes/{id}/snapshots/002.jpg")).Status);
        Assert.Equal(404, (await f.SendAsync(HttpMethod.Get, $"/api/processes/{id}/snapshots/..%2Fpepa.db")).Status);

        var store = (SqliteStore)f.Host.Services.GetRequiredService<Pepa.Engine.Persistence.IPepaStore>();
        await using var retention = new HistoryRetention(store, () => 90, new ManualTime(DateTimeOffset.UtcNow.AddDays(91)),
            NullLogger.Instance, folder);
        Assert.Equal(1, await retention.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Null(folder.Find(id, name!));
    }
}
