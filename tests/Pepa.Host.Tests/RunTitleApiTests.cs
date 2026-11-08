namespace Pepa.Host.Tests;

/// <summary>Название запуска и входные данные для «Запустить снова» в API (D-081).</summary>
public sealed class RunTitleApiTests
{
    [Fact]
    public async Task Lists_and_details_show_the_run_title_and_details_give_inputs_without_secrets()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (status, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Вход в аккаунт",
            runTitle = "{{ vars.login }} · {{ vars.profile }}",
            variables = new object[]
            {
                new { name = "login", type = "text", isInput = true },
                new { name = "password", type = "text", isInput = true, secret = true },
                new { name = "profile", type = "text", isInput = true, lookup = "adspower-profiles" },
            },
            nodes = new object[]
            {
                new { id = "start", type = "start" },
                new { id = "wait", type = "event.wait", config = new { source = "manual", eventType = "go" } },
                new { id = "end", type = "end" },
            },
            edges = new object[]
            {
                new { id = "a", source = "start", sourcePort = "out", target = "wait" },
                new { id = "b", source = "wait", sourcePort = "event", target = "end" },
            },
        });
        Assert.Equal(200, status);
        var scenarioId = scenario!["id"]!.GetValue<string>();
        var saved = await f.GetAsync($"/api/scenarios/{scenarioId}");
        Assert.Equal("adspower-profiles", saved["definition"]!["variables"]![2]!["lookup"]!.GetValue<string>());

        var (_, run) = await f.SendAsync(HttpMethod.Post, $"/api/scenarios/{scenarioId}/run",
            new { inputs = new { login = "anna", password = "pw-9f8e7d6c", profile = "k1a2" } });
        var id = run!["processId"]!.GetValue<string>();
        await f.WaitForProcessAsync(id, p => HostFixture.Status(p) == "waiting");

        var list = await f.GetAsync("/api/processes?view=active");
        Assert.Equal("anna · k1a2", list.AsArray().Single(p => p!["id"]!.GetValue<string>() == id)!["title"]!.GetValue<string>());
        var overview = await f.GetAsync("/api/overview");
        Assert.Equal("anna · k1a2", overview["active"]!.AsArray().Single()!["title"]!.GetValue<string>());

        var details = await f.GetAsync($"/api/processes/{id}");
        Assert.Equal("anna · k1a2", details["process"]!["title"]!.GetValue<string>());
        var inputs = details["runInputs"]!.AsObject();
        Assert.Equal("anna", inputs["login"]!.GetValue<string>());
        Assert.Equal("k1a2", inputs["profile"]!.GetValue<string>());
        Assert.False(inputs.ContainsKey("password"));
        Assert.DoesNotContain("pw-9f8e7d6c", await f.Client.GetStringAsync($"/api/processes/{id}"));
        await f.SendAsync(HttpMethod.Post, $"/api/processes/{id}/cancel");
    }
}

/// <summary>Сбой автозапуска виден на «Главной» и в пояснении события.</summary>
public sealed class FailedTriggerApiTests
{
    [Fact]
    public async Task Home_lists_an_autostart_that_failed_and_the_event_says_why()
    {
        await using var f = await HostFixture.StartAsync(seedSamples: false);
        var (_, scenario) = await f.SendAsync(HttpMethod.Post, "/api/scenarios", new
        {
            name = "Заказ",
            variables = new object[] { new { name = "order", type = "text", isInput = true } },
            nodes = new object[] { new { id = "start", type = "start" }, new { id = "end", type = "end" } },
            edges = new object[] { new { id = "a", source = "start", sourcePort = "out", target = "end" } },
        });
        var (status, _) = await f.SendAsync(HttpMethod.Post, "/api/triggers", new
        {
            scenarioId = scenario!["id"]!.GetValue<string>(),
            kind = "event",
            enabled = true,
            name = "Новый заказ",
            config = new { source = "shop", eventType = "order" },
            inputs = new { order = "event.payload.missing.id" },
        });
        Assert.Equal(200, status);
        Assert.Empty((await f.GetAsync("/api/overview"))["failedTriggers"]!.AsArray());

        var (_, published) = await f.SendAsync(HttpMethod.Post, "/api/events", new { source = "shop", type = "order", payload = new { id = 5 } });
        var eventId = published!["eventId"]!.GetValue<long>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while ((await f.GetAsync("/api/overview"))["failedTriggers"]!.AsArray().Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "сбой автозапуска не появился на «Главной»");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var failed = (await f.GetAsync("/api/overview"))["failedTriggers"]!.AsArray().Single()!;
        Assert.Equal("Новый заказ", failed["name"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(failed["lastError"]!.GetValue<string>()));
        var events = await f.GetAsync("/api/events?view=all");
        var note = events.AsArray().Single(e => e!["id"]!.GetValue<long>() == eventId)!["note"]!.GetValue<string>();
        Assert.StartsWith("Подошло правило автозапуска", note);
    }
}
