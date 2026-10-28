using System.Text.Json.Nodes;

namespace Pepa.Host.Tests;

/// <summary>Блоки таблиц в собранном приложении: есть в конструкторе и работают в примере.</summary>
public sealed class TableApiTests
{
    [Fact]
    public async Task Table_blocks_are_in_the_constructor_and_the_run_log_sample_grows_by_one_row_each_run()
    {
        await using var f = await HostFixture.StartAsync();
        var catalog = await f.GetAsync("/api/node-types");
        var types = catalog["nodeTypes"]!.AsArray().Select(t => t!["type"]!.GetValue<string>()).ToList();
        Assert.Contains("table.read", types);
        Assert.Contains("table.find", types);
        Assert.Contains("table.append", types);
        Assert.Contains("table.update", types);
        Assert.Contains(catalog["categories"]!.AsArray(), c => c!["id"]!.GetValue<string>() == "tables");

        var scenarioId = await f.ScenarioIdAsync("Пример 15.");
        foreach (var expected in new[] { 1, 2 })
        {
            var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenarioId}/run", new { inputs = new JsonObject { ["name"] = "проверка" } });
            var done = (await f.WaitForProcessAsync(run!["processId"]!.GetValue<string>(),
                p => HostFixture.Status(p) is "completed" or "needsAttention"))["process"]!;
            Assert.Equal("completed", HostFixture.Status(done));
            Assert.Equal($"В журнале строк: {expected}.", done["statusMessage"]!.GetValue<string>());
        }

        Assert.True(File.Exists(Path.Combine(f.Workspace, "примеры", "журнал запусков.xlsx")));
    }
}
