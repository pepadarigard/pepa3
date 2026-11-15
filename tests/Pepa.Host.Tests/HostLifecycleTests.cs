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
    public async Task Open_change_stream_does_not_hold_up_the_stop()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        // Окно Pepa держит поток изменений открытым всё время, даже свёрнутое в область уведомлений.
        using var stream = await f.Client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(200, (int)stream.StatusCode);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await f.Host.StopAsync();
        // Раньше сервер ждал открытый поток до своего тайм-аута (5 с) — при завершении сеанса Windows движок не успевал
        // остановиться.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Остановка заняла {watch.Elapsed}.");
    }

    [Fact]
    public async Task Second_stop_waits_for_the_same_stop()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var first = f.Host.StopAsync();
        var second = f.Host.StopAsync();
        Assert.Same(first, second);
        await second;
        Assert.True(first.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(0, 0, 0, "Pepa — процессов нет")]
    [InlineData(2, 0, 0, "Pepa — работают или ждут: 2")]
    [InlineData(0, 0, 3, "Pepa — на паузе (проверьте и возобновите): 3")]
    [InlineData(1, 2, 3, "Pepa — ждут решения: 2; на паузе (проверьте и возобновите): 3; работают или ждут: 1")]
    public void Tray_text_counts_paused_processes(int active, int attention, int paused, string expected)
    {
        var text = new TrayCounts(active, attention, paused).Text();
        Assert.Equal(expected, text);
        Assert.True(text.Length <= 127, "Windows показывает в подсказке не больше 127 символов.");
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
        // Значок в области уведомлений — часто единственное, что видно после автозапуска: «процессов нет» было бы неправдой.
        Assert.Equal(new TrayCounts(0, 0, 1), await f.Host.CountsAsync());

        var (status, _) = await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/cancel");
        Assert.Equal(200, status);
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "cancelled");
    }
}
