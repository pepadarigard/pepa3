using Pepa.Core.Model;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Browser.Tests;

/// <summary>Профили AdsPower: Local API, подключение по CDP, один процесс на профиль, переподключение после перезапуска.</summary>
public sealed class AdsPowerTests
{
    private static ScenarioDefinition UseProfile(BrowserFixture f, string profile, string user, int holdSeconds = 0, bool close = true)
    {
        var builder = new ScenarioBuilder($"AdsPower {user}")
            .Node("start", "start")
            .Node("open", "adspower.open", new { profileId = profile, url = f.Site.Url("/form") })
            .Node("fill", "browser.fill", new { selector = "#user", text = user })
            .Node("click", "browser.click", new { selector = "#go", expect = "#done" })
            .Node("end", "end");
        var chain = new List<string> { "start", "open" };
        if (holdSeconds > 0)
        {
            builder.Node("w", "wait", new { mode = "duration", amount = holdSeconds, unit = "seconds" });
            chain.Add("w");
        }

        chain.AddRange(["fill", "click"]);
        if (close)
        {
            builder.Node("close", "browser.close", new { });
            chain.Add("close");
        }

        chain.Add("end");
        return builder.Chain([.. chain]).Build();
    }

    [Fact]
    public async Task Profile_is_opened_through_local_api_used_and_closed()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true, apiKey: "secret");
        var id = await f.H.RunAsync(UseProfile(f, "k1", "Анна"));

        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
        Assert.Equal(1, f.Ads!.Starts);
        Assert.False(f.Ads.IsOpen("k1"));
        Assert.Contains("POST /api/v2/browser-profile/start", f.Ads.Requests);
        Assert.Contains("POST /api/v2/browser-profile/stop", f.Ads.Requests);
        Assert.Null((await f.H.Store.ListResourcesAsync()).Single(r => r.Key == "adspower:k1").HolderProcessId);
    }

    private static ScenarioDefinition FillInProfile(BrowserFixture f, string user, object open) => new ScenarioBuilder($"Профиль: {user}")
        .Node("start", "start")
        .Node("open", "adspower.open", open)
        .Node("fill", "browser.fill", new { selector = "#user", text = user })
        .Node("click", "browser.click", new { selector = "#go", expect = "#done" })
        .Node("end", "end")
        .Chain("start", "open", "fill", "click", "end")
        .Build();

    [Fact]
    public async Task Profile_opened_outside_Pepa_is_not_taken_over_unless_asked()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        await f.Ads!.OpenByOwnerAsync("k1");

        var stopped = await f.H.RunAsync(UseProfile(f, "k1", "чужое окно"));
        var process = await f.H.WaitForStatusAsync(stopped, ProcessStatus.NeedsAttention);
        Assert.Contains("открыла его не Pepa", process.StatusMessage);
        await f.H.Engine.CancelAsync(stopped);
        Assert.True(f.Ads.IsOpen("k1"));
        Assert.Equal(0, f.Site.Submits);

        // «Работать в новой вкладке»: своя вкладка, профиль владельца в конце остаётся открытым.
        var tab = await f.H.RunAsync(FillInProfile(f, "своя вкладка", new { profileId = "k1", url = f.Site.Url("/form"), ifOpen = "tab" }));
        await f.H.WaitForStatusAsync(tab, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
        Assert.True(f.Ads.IsOpen("k1"));
        Assert.DoesNotContain("POST /api/v2/browser-profile/stop", f.Ads.Requests);
        Assert.Equal(1, f.Ads.Starts);
    }

    [Fact]
    public async Task Profile_left_open_by_Pepa_is_used_by_the_next_process_after_a_restart()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var first = await f.H.RunAsync(FillInProfile(f, "первый", new { profileId = "k1", url = f.Site.Url("/form"), closeOnFinish = false }));
        await f.H.WaitForStatusAsync(first, ProcessStatus.Completed);
        Assert.True(f.Ads!.IsOpen("k1"));

        // Pepa перезапущена — она всё равно помнит, что этот профиль открыла она.
        await f.RestartAsync();
        var second = await f.H.RunAsync(FillInProfile(f, "второй", new { profileId = "k1", url = f.Site.Url("/form") }));
        await f.H.WaitForStatusAsync(second, ProcessStatus.Completed);
        Assert.Equal(2, f.Site.Submits);
        Assert.Equal(1, f.Ads.Starts);

        // Второй процесс закрывает профиль, когда завершается (это идёт сразу после завершения, не в нём).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (f.Ads.IsOpen("k1"))
        {
            Assert.True(DateTime.UtcNow < deadline, "Профиль не закрылся после завершения процесса.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Second_process_with_same_profile_waits_for_the_first()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var first = await f.H.RunAsync(UseProfile(f, "k1", "first", holdSeconds: 60, close: false));
        await f.H.WaitForAsync(first, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);
        var second = await f.H.RunAsync(UseProfile(f, "k1", "second"));
        await f.H.WaitForAsync(second, p => p.Status == ProcessStatus.Waiting);

        await Task.Delay(500);
        Assert.Equal(1, f.Ads!.Starts);
        Assert.Equal(first, (await f.H.Store.ListResourcesAsync()).Single(r => r.Key == "adspower:k1").HolderProcessId);

        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.WaitForStatusAsync(first, ProcessStatus.Completed);
        await f.H.WaitForStatusAsync(second, ProcessStatus.Completed);
        Assert.Equal(2, f.Site.Submits);
        Assert.Equal(2, f.Ads.Starts); // первый процесс закрыл профиль при завершении, второй открыл его снова
    }

    [Fact]
    public async Task After_restart_open_profile_is_reconnected_and_work_continues()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(UseProfile(f, "k2", "после", holdSeconds: 60));
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        await f.RestartAsync();
        Assert.True(f.Ads!.IsOpen("k2")); // Pepa закрылась, профиль AdsPower остался открытым
        Assert.Equal(ProcessStatus.Paused, (await f.H.ProcessAsync(id)).Status);
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);

        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
        Assert.Equal(1, f.Ads.Starts); // профиль не перезапускался — Pepa подключилась к открытому
    }

    [Fact]
    public async Task After_restart_the_process_continues_in_its_own_tab_not_in_a_tab_opened_meanwhile()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(UseProfile(f, "k2", "своя вкладка", holdSeconds: 60));
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        // Пока Pepa была выключена, владелец открыл в профиле другую вкладку — она последняя, но она не процесса.
        await f.RestartAsync();
        await f.Ads!.OpenTabAsync("k2", "about:blank");
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);

        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
    }

    [Fact]
    public async Task Tab_of_the_process_closed_while_Pepa_was_off_stops_the_step()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(UseProfile(f, "k2", "закрыли вкладку", holdSeconds: 60));
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        await f.RestartAsync();
        await f.Ads!.OpenTabAsync("k2", "about:blank");
        await f.Ads.CloseTabsAsync("k2", "/form");
        // Кто-то открыл в профиле ту же форму — это не страница процесса.
        await f.Ads.OpenTabAsync("k2", f.Site.Url("/form"));
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);

        // В другой вкладке Pepa молча не продолжает: шаг остановлен с понятной причиной.
        var process = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("Вкладку, в которой работал процесс", process.StatusMessage);
        Assert.Contains("новую пустую вкладку", process.StatusMessage);
        Assert.Equal(0, f.Site.Submits);
        Assert.True(f.Ads.IsOpen("k2"));

        // И при повторе — тоже: шаг выполняется в новой вкладке процесса (там формы нет), а не в чужой с формой.
        await f.H.Engine.ResumeAsync(id, ResumeAction.Retry);
        var again = await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.NeedsAttention && p.StatusMessage != process.StatusMessage);
        Assert.DoesNotContain("Вкладку, в которой работал процесс", again.StatusMessage);
        Assert.Equal(0, f.Site.Submits);
    }

    [Fact]
    public async Task In_the_owners_profile_the_process_does_not_switch_to_a_tab_the_owner_opened()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        await f.Ads!.OpenByOwnerAsync("k1");
        var id = await f.H.RunAsync(new ScenarioBuilder("Своя вкладка")
            .Node("start", "start")
            .Node("open", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form"), ifOpen = "tab" })
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            .Node("tab", "browser.tab", new { which = "newest", waitSeconds = 1 })
            .Node("switched", "end", new { message = "переключились" })
            .Node("none", "end", new { message = "новой вкладки процесса нет" })
            .Chain("start", "open", "w", "tab")
            .Edge("tab", "switched", Ports.Out).Edge("tab", "none", Ports.Timeout)
            .Build());
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        // Владелец открыл в своём профиле ещё одну вкладку — она «новая», но не процесса.
        await f.Ads.OpenTabAsync("k1", f.Site.Url("/second"));
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        var done = await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("новой вкладки процесса нет", done.StatusMessage);
    }

    [Fact]
    public async Task Profile_closed_by_user_is_reported_instead_of_reopened_silently()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(UseProfile(f, "k1", "закрыли", holdSeconds: 60));
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        await f.RestartAsync();
        f.Ads!.CloseExternally("k1");
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);

        var process = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("закрыт", process.StatusMessage);
        Assert.Equal(0, f.Site.Submits);
        Assert.Equal(1, f.Ads.Starts);
    }

    [Theory]
    [InlineData("user account does not exist", "не нашёл профиль k9", false)]
    [InlineData("Profile does not exist", "не нашёл профиль k9", false)]
    [InlineData("Too many request per second, please check", "ограничил частоту запросов", true)]
    [InlineData("The proxy check failed, please check the proxy", "прокси профиля не прошёл проверку", true)]
    [InlineData("Please login first", "не вошёл в аккаунт", false)]
    [InlineData("Exceeding the maximum number of open browsers", "больше профилей одновременно", true)]
    [InlineData("Browser kernel is downloading", "скачивает или обновляет браузер", true)]
    [InlineData("The profile is being used by another member", "у другого участника команды", true)]
    [InlineData("api key is invalid", "Проверьте ключ API", false)]
    [InlineData("что-то новое", "AdsPower отказал: «что-то новое»", true)]
    public void AdsPower_refusals_are_explained_and_keep_the_original_text(string answer, string expected, bool retryable)
    {
        var error = AdsPowerClient.Explain(answer, "k9");
        Assert.Contains(expected, error.Message);
        Assert.Contains(answer, error.Message);
        Assert.Equal(retryable, error.Retryable);
    }

    [Fact]
    public async Task Missing_profile_stops_the_step_with_an_explanation_and_without_retries()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        f.Ads!.StartErrors["k1"] = "user account does not exist";
        var scenario = UseProfile(f, "k1", "нет профиля");
        scenario = scenario with
        {
            Nodes = [.. scenario.Nodes.Select(n => n.Id == "open" ? n with { Settings = n.Settings with { RetryCount = 3, RetryDelaySeconds = 0 } } : n)],
        };
        var id = await f.H.RunAsync(scenario);

        var process = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Contains("AdsPower не нашёл профиль k1", process.StatusMessage);
        Assert.Contains("user account does not exist", process.StatusMessage);
        // Повтор не поможет — нужен человек: попытка одна.
        Assert.Single(await f.H.StepsAsync(id), s => s.NodeId == "open" && s.State == StepState.Failed);
        Assert.Equal(0, f.Ads.Starts);
    }

    [Fact]
    public async Task AdsPower_not_running_gives_clear_reason()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var id = await f.H.RunAsync(new ScenarioBuilder("Нет AdsPower")
            .Node("start", "start").Node("open", "adspower.open", new { profileId = "k1" }).Node("end", "end")
            .Chain("start", "open", "end").Build());

        var process = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Contains("Нет связи с AdsPower", process.StatusMessage);
    }

    [Fact]
    public async Task Client_uses_api_key_and_falls_back_to_v1()
    {
        TestBrowser.RequireCdpBrowser();
        using var ads = new FakeAdsPower(TestBrowser.ExecutableForCdp!, apiKey: "secret", v1Only: true);
        using var client = new AdsPowerClient(() => new AdsPowerSettings { BaseUrl = ads.BaseUrl, ApiKey = "secret", MinInterval = TimeSpan.Zero });

        var profiles = await client.ListProfilesAsync(1, 50, CancellationToken.None);
        Assert.Equal(["k1", "k2"], profiles.Select(p => p.Id));
        var started = await client.StartAsync("k1", CancellationToken.None);
        Assert.StartsWith("ws://", started.WsEndpoint);
        Assert.True((await client.GetStateAsync("k1", CancellationToken.None)).Active);
        await client.StopAsync("k1", CancellationToken.None);
        Assert.False((await client.GetStateAsync("k1", CancellationToken.None)).Active);
        Assert.Contains("POST /api/v2/browser-profile/start", ads.Requests);
        Assert.Contains("GET /api/v1/browser/start", ads.Requests);

        using var wrongKey = new AdsPowerClient(() => new AdsPowerSettings { BaseUrl = ads.BaseUrl, ApiKey = "wrong", MinInterval = TimeSpan.Zero });
        var error = await Assert.ThrowsAsync<AdsPowerException>(() => wrongKey.CheckAsync(CancellationToken.None));
        Assert.Contains("ключ API", error.Message);
    }

    [Fact]
    public async Task Requests_are_spaced_to_respect_rate_limit()
    {
        TestBrowser.RequireCdpBrowser();
        using var ads = new FakeAdsPower(TestBrowser.ExecutableForCdp!);
        using var client = new AdsPowerClient(() => new AdsPowerSettings { BaseUrl = ads.BaseUrl, MinInterval = TimeSpan.FromMilliseconds(300) });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.CheckAsync(CancellationToken.None)));
        Assert.True(watch.ElapsedMilliseconds >= 850, $"Запросы шли слишком часто: {watch.ElapsedMilliseconds} мс на 4 запроса.");
    }
}
