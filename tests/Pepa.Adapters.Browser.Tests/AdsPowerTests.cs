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
