using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Pepa.Host.Tests;

public sealed class HostLifecycleTests
{
    [Fact]
    public async Task Second_instance_on_same_data_directory_is_refused()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);

        var error = await Assert.ThrowsAsync<InstanceAlreadyRunningException>(() =>
            PepaHost.StartAsync(HostFixture.Options(f.Root, seedSamples: false)));
        Assert.Contains("уже запущена", error.Message);
    }

    [Fact]
    public async Task Settings_are_validated_and_persisted()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var current = await f.GetAsync("/api/settings");
        Assert.Equal(Path.GetFullPath(f.Workspace), current["workspaceRoot"]!.GetValue<string>());

        object With(string? workspace = null, int parallel = 4) => new
        {
            workspaceRoot = workspace ?? current["workspaceRoot"]!.GetValue<string>(),
            maxParallelProcesses = parallel,
            stepBudget = 10_000,
            actionTimeoutSeconds = 120,
            restartRequired = false,
        };

        Assert.Equal(400, (await f.SendAsync(HttpMethod.Put, "/api/settings", With("относительный/путь"))).Status);
        Assert.Equal(400, (await f.SendAsync(HttpMethod.Put, "/api/settings", With(Path.GetPathRoot(f.Root)))).Status);
        Assert.Equal(400, (await f.SendAsync(HttpMethod.Put, "/api/settings", With(f.Root))).Status); // содержит папку данных
        Assert.Equal(400, (await f.SendAsync(HttpMethod.Put, "/api/settings", With(parallel: 0))).Status);

        var newWorkspace = Path.Combine(f.Root, "другая папка");
        var (status, saved) = await f.SendAsync(HttpMethod.Put, "/api/settings", With(newWorkspace, parallel: 2));
        Assert.Equal(200, status);
        Assert.True(saved!["restartRequired"]!.GetValue<bool>());
        Assert.True(Directory.Exists(newWorkspace));

        await f.RestartAsync();
        var afterRestart = await f.GetAsync("/api/settings");
        Assert.Equal(newWorkspace, afterRestart["workspaceRoot"]!.GetValue<string>());
        Assert.Equal(2, afterRestart["maxParallelProcesses"]!.GetValue<int>());
        Assert.False(afterRestart["restartRequired"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Workspace_chosen_on_first_run_does_not_change_silently()
    {
        var root = Path.Combine(Path.GetTempPath(), "pepa-host-tests", Guid.NewGuid().ToString("N"));
        var first = await PepaHost.StartAsync(HostFixture.Options(root, seedSamples: false));
        await first.StopAsync();

        // Второй запуск предлагает другую папку по умолчанию — должна остаться прежняя.
        var second = await PepaHost.StartAsync(HostFixture.Options(root, seedSamples: false) with
        {
            DefaultWorkspaceRoot = Path.Combine(root, "другая"),
        });
        try
        {
            using var client = new HttpClient { BaseAddress = second.BaseUrl };
            client.DefaultRequestHeaders.Add("X-Pepa-Token", HostFixture.Token);
            var settings = await client.GetFromJsonAsync<JsonNode>("/api/settings");
            Assert.Equal(Path.Combine(root, "workspace"), settings!["workspaceRoot"]!.GetValue<string>());
        }
        finally
        {
            await second.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Waiting_process_is_paused_after_restart_and_resumes_manually()
    {
        await using var f = await HostFixture.StartAsync();
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{await f.ScenarioIdAsync("Пример 3")}/run", new { });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");

        await f.RestartAsync();

        var info = await f.GetAsync("/api/info");
        Assert.Equal(1, info["recovery"]!["paused"]!.GetValue<int>());
        var details = await f.GetAsync($"/api/processes/{id}");
        var process = details["process"]!;
        Assert.Equal("paused", HostFixture.Status(process));
        Assert.Equal("restart", process["pauseReason"]!.GetValue<string>());
        Assert.Equal(["continue", "cancel"], process["allowedActions"]!.AsArray().Select(a => a!.GetValue<string>()));

        var (status, _) = await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/cancel");
        Assert.Equal(200, status);
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "cancelled");
    }
}
