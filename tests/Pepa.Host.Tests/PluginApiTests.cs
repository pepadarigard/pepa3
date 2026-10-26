using System.Text.Json.Nodes;

namespace Pepa.Host.Tests;

/// <summary>Расширения: блоки из сборки в папке plugins работают как встроенные; ошибки расширений видны и не мешают Pepa.</summary>
public sealed class PluginApiTests
{
    private static string Sample => Path.Combine(AppContext.BaseDirectory, "sample-plugin", "Pepa.Plugins.Sample.dll");

    [Fact]
    public async Task Plugin_blocks_appear_in_the_constructor_and_run_with_verification()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false, plugins: new Dictionary<string, string[]> { ["sample"] = [Sample] });

        var plugins = await f.GetAsync("/api/plugins");
        var plugin = Assert.Single(plugins["plugins"]!.AsArray())!;
        Assert.Equal("Пример расширения", plugin["name"]!.GetValue<string>());
        Assert.Null(plugin["error"]);
        Assert.Equal(["sample.textcase", "sample.appendline"], plugin["nodeTypes"]!.AsArray().Select(t => t!.GetValue<string>()));

        var types = await f.GetAsync("/api/node-types");
        Assert.Contains(types["categories"]!.AsArray(), c => c!["id"]!.GetValue<string>() == "sample");
        Assert.Contains(types["nodeTypes"]!.AsArray(), t => t!["type"]!.GetValue<string>() == "sample.appendline");

        var (created, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Блоки расширения",
            variables = new[] { new { name = "loud", type = "text" } },
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "case", type = "sample.textcase", config = new { text = "привет, мир", mode = "upper", saveTo = "loud" } },
                new { id = "append", type = "sample.appendline", config = new { path = "журнал.txt", line = "{{ vars.loud }}" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "case" },
                new { id = "b", source = "case", sourcePort = "out", target = "append" },
                new { id = "c", source = "append", sourcePort = "out", target = "end" },
            },
        });
        Assert.Equal(200, created);
        Assert.True(scenario!["validation"]!["isValid"]!.GetValue<bool>(), scenario["validation"]!.ToJsonString());
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario["id"]!.GetValue<string>()}/run", new { });
        var done = await f.WaitForProcessAsync(run!["processId"]!.GetValue<string>(), p => HostFixture.Status(p) is "completed" or "needsAttention");
        Assert.Equal("completed", HostFixture.Status(done["process"]!));
        Assert.Equal("ПРИВЕТ, МИР" + Environment.NewLine, await File.ReadAllTextAsync(Path.Combine(f.Workspace, "журнал.txt")));
    }

    [Fact]
    public async Task Broken_or_conflicting_plugins_are_reported_and_the_rest_works()
    {
        var broken = Path.Combine(Path.GetTempPath(), "pepa-broken-" + Guid.NewGuid().ToString("N") + ".dll");
        await File.WriteAllTextAsync(broken, "это не сборка");
        try
        {
            await using var f = await HostFixture.StartAsync(seedSamples: false, plugins: new Dictionary<string, string[]>
            {
                ["a-sample"] = [Sample],
                ["b-copy"] = [Sample],
                ["c-broken"] = [broken],
                ["d-empty"] = [],
            });

            var plugins = (await f.GetAsync("/api/plugins"))["plugins"]!.AsArray().ToDictionary(p => p!["id"]!.GetValue<string>(), p => p!);
            Assert.Null(plugins["a-sample"]["error"]);
            Assert.Contains("уже есть", plugins["b-copy"]["error"]!.GetValue<string>());
            Assert.Contains("не удалось загрузить", plugins["c-broken"]["error"]!.GetValue<string>());
            Assert.Contains("нет сборок", plugins["d-empty"]["error"]!.GetValue<string>());

            // Pepa работает: встроенные блоки и блоки исправного расширения на месте.
            var types = (await f.GetAsync("/api/node-types"))["nodeTypes"]!.AsArray().Select(t => t!["type"]!.GetValue<string>()).ToList();
            Assert.Contains("log", types);
            Assert.Single(types, t => t == "sample.textcase");
        }
        finally
        {
            File.Delete(broken);
        }
    }
}
