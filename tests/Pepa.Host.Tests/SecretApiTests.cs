namespace Pepa.Host.Tests;

/// <summary>Секретные переменные не видны в подробностях процесса: ни значения, ни параметры шагов, ни журнал.</summary>
public sealed class SecretApiTests
{
    [Fact]
    public async Task Process_details_never_show_the_value_of_a_secret_variable()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Вход",
            variables = new object[]
            {
                new { name = "password", type = "text", isInput = true, secret = true },
                new { name = "pin", type = "text", secret = true, @default = "7391" },
                new { name = "login", type = "text", isInput = true },
            },
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "log", type = "log", config = new { message = "вход {{ vars.login }}: {{ vars.password }} / {{ vars.pin }}" } },
                new { id = "wait", type = "wait", config = new { mode = "duration", amount = 10, unit = "minutes" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "log" },
                new { id = "b", source = "log", sourcePort = "out", target = "wait" },
                new { id = "c", source = "wait", sourcePort = "out", target = "end" },
            },
        });
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenario!["id"]!.GetValue<string>()}/run",
            new { inputs = new { password = "S3cr3t-Pa55", login = "ivan" } });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");

        var text = await f.Client.GetStringAsync($"/api/processes/{id}");
        Assert.DoesNotContain("S3cr3t-Pa55", text);
        Assert.DoesNotContain("7391", text);
        var details = await f.GetAsync($"/api/processes/{id}");
        Assert.Equal("••••", details["variables"]!["password"]!.GetValue<string>());
        Assert.Equal("••••", details["variables"]!["pin"]!.GetValue<string>());
        Assert.Equal("ivan", details["variables"]!["login"]!.GetValue<string>());
        Assert.Contains(details["journal"]!.AsArray(), j => j!["message"]!.GetValue<string>() == "вход ivan: •••• / ••••");
        await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/cancel");
    }

    [Fact]
    public async Task Secret_input_of_a_scheduled_run_is_hidden_and_kept_when_saved_back()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Вход по расписанию",
            variables = new object[]
            {
                new { name = "password", type = "text", isInput = true, secret = true },
                new { name = "login", type = "text", isInput = true },
            },
            nodes = new object[] { new { id = "start", type = "start" }, new { id = "end", type = "end" } },
            edges = new object[] { new { id = "a", source = "start", sourcePort = "out", target = "end" } },
        });
        var scenarioId = scenario!["id"]!.GetValue<string>();
        var (status, trigger) = await f.SendAsync(HttpMethod.Post, "/api/triggers", new
        {
            scenarioId, kind = "schedule", enabled = true, config = new { mode = "daily", time = "09:00", days = Array.Empty<int>() },
            inputs = new { password = "S3cr3t-Pa55", login = "ivan" },
        });
        Assert.Equal(200, status);
        Assert.Equal("••••", trigger!["inputs"]!["password"]!.GetValue<string>());
        var id = trigger["id"]!.GetValue<string>();
        Assert.DoesNotContain("S3cr3t-Pa55", await f.Client.GetStringAsync("/api/triggers"));

        // Редактор отправляет «••••» обратно — сохранённый пароль не меняется; новое значение — меняется.
        Assert.Equal(200, (await f.SendAsync(HttpMethod.Put, $"/api/triggers/{id}", new
        {
            scenarioId, kind = "schedule", enabled = true, config = new { mode = "daily", time = "10:00", days = Array.Empty<int>() },
            inputs = new { password = "••••", login = "petr" },
        })).Status);
        var store = (Engine.Persistence.IPepaStore)f.Host.Services.GetService(typeof(Engine.Persistence.IPepaStore))!;
        var saved = await store.GetTriggerAsync(id);
        Assert.Equal("S3cr3t-Pa55", saved!.Inputs!["password"]!.GetValue<string>());
        Assert.Equal("petr", saved.Inputs["login"]!.GetValue<string>());
    }

    [Fact]
    public async Task Events_page_does_not_show_secrets_sent_by_a_process_or_returned_by_a_called_scenario()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, child) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Вызванный",
            variables = new object[] { new { name = "password", type = "text", isInput = true } },
            nodes = new object[] { new { id = "start", type = "start" }, new { id = "end", type = "end" } },
            edges = new object[] { new { id = "a", source = "start", sourcePort = "out", target = "end" } },
        });
        var (_, sender) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Отправитель",
            variables = new object[] { new { name = "password", type = "text", isInput = true, secret = true } },
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new
                {
                    id = "send", type = "event.send",
                    config = new { source = "process", eventType = "credentials", payload = "({ password: vars.password })", correlationKey = "{{ vars.password }}" },
                },
                new { id = "call", type = "scenario.call", config = new { scenario = child!["id"]!.GetValue<string>(), inputs = "({ password: vars.password })" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "send" },
                new { id = "b", source = "send", sourcePort = "out", target = "call" },
                new { id = "c", source = "call", sourcePort = "done", target = "end" },
            },
        });
        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{sender!["id"]!.GetValue<string>()}/run",
            new { inputs = new { password = "S3cr3t-Pa55" } });
        await f.WaitForProcessAsync(run!["processId"]!.GetValue<string>(), p => HostFixture.Status(p) == "completed");

        var events = await f.Client.GetStringAsync("/api/events");
        Assert.Contains("credentials", events);
        Assert.Contains("finished", events);
        Assert.DoesNotContain("S3cr3t-Pa55", events);
    }
}
