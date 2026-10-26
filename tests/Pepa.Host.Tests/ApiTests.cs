using System.Net;
using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Serialization;
using Pepa.Host.Samples;

namespace Pepa.Host.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task Api_requires_token_and_local_host()
    {
        await using var f = await HostFixture.StartAsync();
        using var anonymous = new HttpClient { BaseAddress = f.Host.BaseUrl };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/info")).StatusCode);

        using var wrongToken = new HttpRequestMessage(HttpMethod.Get, "/api/info");
        wrongToken.Headers.Add("X-Pepa-Token", "guess");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(wrongToken)).StatusCode);

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/info");
        rebinding.Headers.Host = "attacker.example";
        rebinding.Headers.Add("X-Pepa-Token", HostFixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.SendAsync(rebinding)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/api/info")).StatusCode);
        Assert.Contains("#token=", f.Host.LaunchUrl.ToString());
    }

    [Fact]
    public async Task Unknown_api_route_is_json_404_and_ui_fallback_is_explained()
    {
        await using var f = await HostFixture.StartAsync();

        var (status, body) = await f.SendAsync(HttpMethod.Get, "/api/nothing-here");
        Assert.Equal(404, status);
        Assert.NotNull(body!["error"]);

        var page = await f.Client.GetStringAsync("/");
        Assert.Contains("Интерфейс не собран", page);
    }

    [Fact]
    public async Task Samples_are_seeded_once_and_valid()
    {
        await using var f = await HostFixture.StartAsync();
        var list = (await f.GetAsync("/api/scenarios")).AsArray();
        Assert.Equal(SampleScenarios.All().Count, list.Count);
        Assert.StartsWith("Пример 1", list[0]!["name"]!.GetValue<string>());
        Assert.All(list, s =>
        {
            Assert.Equal(0, s!["errorCount"]!.GetValue<int>());
            Assert.Equal(0, s["warningCount"]!.GetValue<int>());
        });

        await f.RestartAsync();
        Assert.Equal(SampleScenarios.All().Count, (await f.GetAsync("/api/scenarios")).AsArray().Count);
    }

    [Theory]
    [InlineData("Пример 1")]
    [InlineData("Пример 2")]
    [InlineData("Пример 4")]
    public async Task Sample_runs_to_completion(string sample)
    {
        await using var f = await HostFixture.StartAsync();
        var scenarioId = await f.ScenarioIdAsync(sample);

        var (status, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenarioId}/run", new { });
        Assert.Equal(200, status);
        var details = await f.WaitForProcessAsync(run!["processId"]!.GetValue<string>(),
            p => HostFixture.Status(p) is "completed" or "failed" or "needsAttention");

        Assert.Equal("completed", HostFixture.Status(details["process"]!));
        Assert.NotEmpty(details["steps"]!.AsArray());
        Assert.NotEmpty(details["journal"]!.AsArray());
    }

    [Fact]
    public async Task Sample_with_inputs_uses_given_values()
    {
        await using var f = await HostFixture.StartAsync();
        var scenarioId = await f.ScenarioIdAsync("Пример 1");

        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenarioId}/run",
            new { inputs = new { name = "Тест" } });
        await f.WaitForProcessAsync(run!["processId"]!.GetValue<string>(), p => HostFixture.Status(p) == "completed");

        var text = await File.ReadAllTextAsync(Path.Combine(f.Workspace, "примеры", "привет.txt"));
        Assert.StartsWith("Привет, Тест!", text);

        var (badStatus, bad) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenarioId}/run",
            new { inputs = new { name = 5 } });
        Assert.Equal(409, badStatus);
        Assert.Contains("тип", bad!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Scenario_crud_with_version_conflict()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var definition = new ScenarioDefinition
        {
            Name = "Черновик",
            Nodes = [new NodeDefinition { Id = "s", Type = "start" }],
        };

        var (createStatus, created) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", definition);
        Assert.Equal(200, createStatus);
        var id = created!["id"]!.GetValue<string>();
        Assert.False(created["validation"]!["isValid"]!.GetValue<bool>());

        var fixedDefinition = definition with
        {
            Id = id,
            Nodes = [new NodeDefinition { Id = "s", Type = "start" }, new NodeDefinition { Id = "e", Type = "end" }],
            Edges = [new EdgeDefinition { Id = "e1", Source = "s", Target = "e" }],
        };
        var (updateStatus, updated) = await f.SendAsync(HttpMethod.Put, $"/api/scenarios/{id}",
            new { definition = fixedDefinition, expectedVersion = 1 });
        Assert.Equal(200, updateStatus);
        Assert.Equal(2, updated!["version"]!.GetValue<int>());
        Assert.True(updated["validation"]!["isValid"]!.GetValue<bool>());

        var (conflict, _) = await f.SendAsync(HttpMethod.Put, $"/api/scenarios/{id}",
            new { definition = fixedDefinition, expectedVersion = 1 });
        Assert.Equal(409, conflict);

        var (duplicateStatus, copy) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{id}/duplicate");
        Assert.Equal(200, duplicateStatus);
        Assert.Equal("Черновик (копия)", copy!["name"]!.GetValue<string>());

        var (deleteStatus, _) = await f.SendAsync(HttpMethod.Delete, $"/api/scenarios/{id}");
        Assert.Equal(200, deleteStatus);
        var (missing, _) = await f.SendAsync(HttpMethod.Get, $"/api/scenarios/{id}");
        Assert.Equal(404, missing);
    }

    [Fact]
    public async Task Invalid_scenario_run_returns_validation_report()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, created) = await f.SendAsync(HttpMethod.Post, "/api/scenarios",
            new ScenarioDefinition { Name = "Пустой", Nodes = [new NodeDefinition { Id = "s", Type = "start" }] });

        var (status, body) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{created!["id"]}/run", new { });

        Assert.Equal(422, status);
        Assert.NotEmpty(body!["validation"]!["issues"]!.AsArray());
    }

    [Fact]
    public async Task Validate_endpoint_checks_without_saving()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, report) = await f.SendAsync(HttpMethod.Post, "/api/scenarios/validate",
            new ScenarioDefinition { Name = "x" });

        Assert.Equal(200, status);
        Assert.False(report!["isValid"]!.GetValue<bool>());
        Assert.Empty((await f.GetAsync("/api/scenarios")).AsArray());
    }

    [Fact]
    public async Task Disallowed_resume_action_is_conflict()
    {
        await using var f = await HostFixture.StartAsync();
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{await f.ScenarioIdAsync("Пример 1")}/run", new { });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "completed");

        var (status, body) = await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/resume", new { action = "retry" });
        Assert.Equal(409, status);
        Assert.NotNull(body!["error"]);
        var (pause, _) = await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/pause");
        Assert.Equal(409, pause);
    }

    [Fact]
    public async Task Node_types_describe_fields_and_ports()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var types = await f.GetAsync("/api/node-types");

        var write = types["nodeTypes"]!.AsArray().Single(t => t!["type"]!.GetValue<string>() == "file.write")!;
        Assert.Equal("external", write["effect"]!.GetValue<string>());
        Assert.True(write["supportsVerification"]!.GetValue<bool>());
        Assert.Contains(write["outputs"]!.AsArray(), p => p!["kind"]!.GetValue<string>() == "error");
        Assert.Contains(types["categories"]!.AsArray(), c => c!["id"]!.GetValue<string>() == "files");
    }

    [Fact]
    public async Task Overview_counts_processes()
    {
        await using var f = await HostFixture.StartAsync();
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{await f.ScenarioIdAsync("Пример 1")}/run", new { });
        await f.WaitForProcessAsync(run!["processId"]!.GetValue<string>(), p => HostFixture.Status(p) == "completed");

        var overview = await f.GetAsync("/api/overview");
        Assert.Equal(1, overview["counts"]!["completedToday"]!.GetValue<int>());
        Assert.Single(overview["recent"]!.AsArray());
    }

    [Fact]
    public async Task Change_events_are_streamed()
    {
        await using var f = await HostFixture.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events");
        using var response = await f.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{await f.ScenarioIdAsync("Пример 1")}/run", new { });
        var processId = run!["processId"]!.GetValue<string>();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (line is not null && line.StartsWith("data: ", StringComparison.Ordinal) && line.Contains(processId))
            {
                Assert.Contains("\"type\":\"process\"", line);
                break;
            }
        }
    }

    [Fact]
    public async Task Exported_definition_round_trips()
    {
        await using var f = await HostFixture.StartAsync();
        var scenario = await f.GetAsync($"/api/scenarios/{await f.ScenarioIdAsync("Пример 2")}");
        var definition = PepaJson.Deserialize<ScenarioDefinition>(scenario["definition"]!.ToJsonString());

        var (status, imported) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", definition with { Name = "Импорт" });

        Assert.Equal(200, status);
        Assert.NotEqual(scenario["id"]!.GetValue<string>(), imported!["id"]!.GetValue<string>());
        Assert.True(imported["validation"]!["isValid"]!.GetValue<bool>());
        Assert.Equal(definition.Nodes.Count, imported["definition"]!["nodes"]!.AsArray().Count);
    }
}
