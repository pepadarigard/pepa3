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
    public async Task Restart_after_a_lost_tab_notice_continues_in_the_replacement_tab_without_a_second_notice()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(UseProfile(f, "k2", "замена вкладки", holdSeconds: 60));
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        await f.RestartAsync();
        await f.Ads!.CloseTabsAsync("k2", "/form");
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);
        var lost = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("Вкладку, в которой работал процесс", lost.StatusMessage);
        var tabs = (await f.Ads.TabUrlsAsync("k2")).Count;

        // Pepa перезапустилась до повтора (человек ещё готовил страницу): процесс знает вкладку-замену, а не закрытую.
        await f.RestartAsync();
        await f.H.Engine.ResumeAsync(id, ResumeAction.Retry);
        var again = await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.NeedsAttention && p.StatusMessage != lost.StatusMessage);
        Assert.DoesNotContain("Вкладку, в которой работал процесс", again.StatusMessage);
        Assert.Equal(tabs, (await f.Ads.TabUrlsAsync("k2")).Count);
    }

    [Fact]
    public async Task Second_profile_opened_under_the_same_name_does_not_lose_or_overwrite_the_first()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(new ScenarioBuilder("Два профиля под одним именем")
            .Node("start", "start")
            .Node("a", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form") })
            .Node("b", "adspower.open", new { profileId = "k2", url = f.Site.Url("/form") })
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            .Node("fill", "browser.fill", new { selector = "#user", text = "второй" })
            .Node("click", "browser.click", new { selector = "#go", expect = "#done" })
            .Node("end", "end")
            .Chain("start", "a", "b", "w", "fill", "click", "end")
            .Build());
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 4);
        Assert.Equal("k2", f.Sessions.FindInfo(id, "main")!.ProfileId);
        Assert.Equal("k1", f.Sessions.FindInfo(id, "main (прежний 1)")!.ProfileId);

        // В первом профиле открылась вкладка (сайт или человек): запись «main» — по-прежнему второго профиля.
        await f.Ads!.OpenTabAsync("k1", "about:blank");
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.Equal("k2", f.Sessions.FindInfo(id, "main")!.ProfileId);

        // После перезапуска процесс продолжает во втором профиле, а в конце закрываются оба (так задано при открытии).
        await f.RestartAsync();
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);
        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
        // Браузеры закрываются после завершения процесса, по одному.
        await f.H.UntilAsync(() => !f.Ads.IsOpen("k1") && !f.Ads.IsOpen("k2"), "оба профиля закрыты");
    }

    [Fact]
    public async Task Close_step_in_the_owners_profile_closes_only_the_process_tabs()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        await f.Ads!.OpenByOwnerAsync("k1");
        var id = await f.H.RunAsync(new ScenarioBuilder("Своя вкладка и закрытие")
            .Node("start", "start")
            .Node("open", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form"), ifOpen = "tab" })
            .Node("close", "browser.close", new { stopProfile = true })
            .Node("end", "end")
            .Chain("start", "open", "close", "end")
            .Build());
        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);

        // Окно владельца с его вкладками не закрыто; закрыта только вкладка процесса.
        Assert.True(f.Ads.IsOpen("k1"));
        Assert.DoesNotContain("POST /api/v2/browser-profile/stop", f.Ads.Requests);
        Assert.DoesNotContain(await f.Ads.TabUrlsAsync("k1"), url => url.Contains("/form", StringComparison.Ordinal));
        Assert.Contains(await f.H.Store.GetJournalAsync(id), j => j.Message.Contains("закрыты только вкладки процесса", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Process_tab_in_the_owners_profile_stays_when_the_process_just_ends()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        await f.Ads!.OpenByOwnerAsync("k1");
        var id = await f.H.RunAsync(new ScenarioBuilder("Своя вкладка без закрытия")
            .Node("start", "start")
            .Node("open", "adspower.open", new { profileId = "k1", url = f.Site.Url("/second"), ifOpen = "tab" })
            .Node("end", "end")
            .Chain("start", "open", "end")
            .Build());
        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        // «Не закрывать в конце»: вкладка процесса остаётся, на ней видно, где он остановился.
        Assert.Contains(await f.Ads.TabUrlsAsync("k1"), url => url.Contains("/second", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Close_step_after_a_restart_closes_the_process_tab_in_the_owners_profile()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        await f.Ads!.OpenByOwnerAsync("k1");
        var id = await f.H.RunAsync(new ScenarioBuilder("Своя вкладка, перезапуск, закрытие")
            .Node("start", "start")
            .Node("open", "adspower.open", new { profileId = "k1", url = f.Site.Url("/second"), ifOpen = "tab" })
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            .Node("close", "browser.close", new { stopProfile = true })
            .Node("end", "end")
            .Chain("start", "open", "w", "close", "end")
            .Build());
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);
        await f.H.UntilAsync(() => f.Sessions.FindInfo(id, "main")?.Endpoint is not null, "запуск браузера запомнен");

        await f.RestartAsync();
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);
        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.True(f.Ads.IsOpen("k1"));
        Assert.DoesNotContain(await f.Ads.TabUrlsAsync("k1"), url => url.Contains("/second", StringComparison.Ordinal));
        Assert.Contains(await f.H.Store.GetJournalAsync(id), j => j.Message.Contains("закрыты только вкладки процесса", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Profile_left_open_by_Pepa_closed_and_reopened_by_the_owner_is_not_taken_over()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var first = await f.H.RunAsync(FillInProfile(f, "первый", new { profileId = "k1", url = f.Site.Url("/form"), closeOnFinish = false }));
        await f.H.WaitForStatusAsync(first, ProcessStatus.Completed);

        // Профиль закрыли без Pepa (перезагрузка, AdsPower), а потом владелец открыл его сам и работает в нём.
        await f.RestartAsync();
        f.Ads!.CloseExternally("k1");
        await f.Ads.OpenByOwnerAsync("k1");
        await f.Ads.OpenTabAsync("k1", f.Site.Url("/second?owner=1"));

        var second = await f.H.RunAsync(UseProfile(f, "k1", "второй"));
        var stopped = await f.H.WaitForStatusAsync(second, ProcessStatus.NeedsAttention);
        Assert.Contains("открыла его не Pepa", stopped.StatusMessage);
        await f.H.Engine.CancelAsync(second);
        Assert.True(f.Ads.IsOpen("k1"));
        Assert.Contains(await f.Ads.TabUrlsAsync("k1"), url => url.Contains("owner=1", StringComparison.Ordinal));
        Assert.Equal(1, f.Site.Submits);
    }

    [Fact]
    public async Task After_a_reboot_the_owners_new_window_is_not_closed_when_the_old_process_is_stopped()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(UseProfile(f, "k1", "до перезагрузки", holdSeconds: 600));
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        // Перезагрузка: профиль закрылся вместе с Windows, после неё владелец открыл его сам.
        await f.RestartAsync();
        f.Ads!.CloseExternally("k1");
        await f.Ads.OpenByOwnerAsync("k1");

        // «Продолжить» — процесс не работает в окне владельца, а сообщает, что страница потеряна. И при повторе — так же:
        // ни вкладки в окне владельца, ни работы в нём.
        f.H.Time.Advance(TimeSpan.FromSeconds(601));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);
        var lost = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("открыт заново не Pepa", lost.StatusMessage);
        var tabs = (await f.Ads.TabUrlsAsync("k1")).Count;
        for (var retry = 0; retry < 2; retry++)
        {
            await f.H.Engine.ResumeAsync(id, ResumeAction.Retry);
            var again = await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.NeedsAttention && p.StepCount > lost.StepCount + retry);
            Assert.Contains("открыт заново не Pepa", again.StatusMessage);
        }

        Assert.Equal(tabs, (await f.Ads.TabUrlsAsync("k1")).Count);

        // Остановка процесса окно владельца не закрывает.
        await f.H.Engine.CancelAsync(id);
        await f.H.WaitForStatusAsync(id, ProcessStatus.Cancelled);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.True(f.Ads.IsOpen("k1"));
        Assert.Equal(0, f.Site.Submits);
    }

    [Fact]
    public async Task Failed_reopen_keeps_the_old_launch_so_the_owners_window_is_not_taken_over()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(new ScenarioBuilder("Открыть снова")
            .Node("start", "start")
            .Node("a", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form") })
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            .Node("b", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form") })
            .Node("fill", "browser.fill", new { selector = "#user", text = "в чужом окне" })
            .Node("end", "end")
            .Chain("start", "a", "w", "b", "fill", "end")
            .Build());
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        // Профиль закрыли без Pepa; повторное открытие не удаётся (прокси) — а потом владелец открывает профиль сам.
        f.Ads!.CloseExternally("k1");
        f.Ads.StartErrors["k1"] = "proxy check failed";
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.NeedsAttention);
        f.Ads.StartErrors.Remove("k1");
        await f.Ads.OpenByOwnerAsync("k1");
        await f.Ads.OpenTabAsync("k1", f.Site.Url("/second?owner=1"));

        await f.H.Engine.ResumeAsync(id, ResumeAction.Retry);
        var refused = await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.NeedsAttention && p.StatusMessage!.Contains("открыла его не Pepa"));
        Assert.NotNull(refused);
        Assert.Contains(await f.Ads.TabUrlsAsync("k1"), url => url.Contains("owner=1", StringComparison.Ordinal));
        await f.H.Engine.CancelAsync(id);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.True(f.Ads.IsOpen("k1"));
    }

    [Fact]
    public async Task Failed_close_at_the_end_is_reported_and_the_next_process_really_closes_the_profile()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        f.Ads!.StopErrors["k1"] = "Too many request per second, please check";
        var first = await f.H.RunAsync(FillInProfile(f, "первый", new { profileId = "k1", url = f.Site.Url("/form") }));
        await f.H.WaitForStatusAsync(first, ProcessStatus.Completed);
        await f.H.UntilAsync(async () => (await f.H.Store.GetJournalAsync(first)).Any(j => j.Message.Contains("мог остаться открытым", StringComparison.Ordinal)),
            "о незакрытом профиле сказано в журнале процесса");
        Assert.True(f.Ads.IsOpen("k1"));

        // Следующий процесс с этим профилем закрывает его по-настоящему, а не «уже у другого процесса».
        f.Ads.StopErrors.Remove("k1");
        var second = await f.H.RunAsync(UseProfile(f, "k1", "второй"));
        await f.H.WaitForStatusAsync(second, ProcessStatus.Completed);
        Assert.False(f.Ads.IsOpen("k1"));
    }

    [Fact]
    public async Task Profile_opened_again_under_its_name_has_one_record_and_is_closed_once()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(new ScenarioBuilder("A, B, снова A")
            .Node("start", "start")
            .Node("a", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form") })
            .Node("b", "adspower.open", new { profileId = "k2", url = f.Site.Url("/form") })
            .Node("again", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form"), closeOnFinish = false })
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            .Node("end", "end")
            .Chain("start", "a", "b", "again", "w", "end")
            .Build());
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 5);

        // Прежняя запись первого профиля снята: последняя настройка («не закрывать») — единственная.
        Assert.Equal("k1", f.Sessions.FindInfo(id, "main")!.ProfileId);
        Assert.Null(f.Sessions.FindInfo(id, "main (прежний 1)"));
        Assert.Equal("k2", f.Sessions.FindInfo(id, "main (прежний 2)")!.ProfileId);

        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        await f.H.UntilAsync(() => !f.Ads!.IsOpen("k2"), "второй профиль закрыт");
        Assert.True(f.Ads!.IsOpen("k1"));
    }

    [Fact]
    public async Task Profile_released_to_another_process_is_not_closed_when_the_first_process_ends()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var first = await f.H.RunAsync(new ScenarioBuilder("Отдаёт профиль")
            .Node("start", "start")
            .Node("a", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form") })
            .Node("b", "adspower.open", new { profileId = "k2", url = f.Site.Url("/form") })
            .Node("free", "resource.release", new { key = "adspower:k1" })
            .Node("w", "wait", new { mode = "duration", amount = 30, unit = "seconds" })
            .Node("end", "end")
            .Chain("start", "a", "b", "free", "w", "end")
            .Build());
        await f.H.WaitForAsync(first, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 5);

        var second = await f.H.RunAsync(UseProfile(f, "k1", "второй процесс", holdSeconds: 60, close: false));
        await f.H.WaitForAsync(second, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        // Первый процесс завершается: его профили закрываются, но не тот, что уже у второго.
        f.H.Time.Advance(TimeSpan.FromSeconds(31));
        await f.H.WaitForStatusAsync(first, ProcessStatus.Completed);
        await f.H.UntilAsync(() => !f.Ads!.IsOpen("k2"), "второй профиль первого процесса закрыт");
        Assert.True(f.Ads!.IsOpen("k1"));

        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.WaitForStatusAsync(second, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
        await f.H.UntilAsync(() => !f.Ads.IsOpen("k1"), "профиль закрыт вторым процессом");
    }

    [Fact]
    public async Task Process_waiting_for_a_profile_works_in_it_after_the_holder_with_two_profiles_ends()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var first = await f.H.RunAsync(new ScenarioBuilder("Два профиля")
            .Node("start", "start")
            .Node("a", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form") })
            .Node("b", "adspower.open", new { profileId = "k2", url = f.Site.Url("/form") })
            .Node("w", "wait", new { mode = "duration", amount = 30, unit = "seconds" })
            .Node("end", "end")
            .Chain("start", "a", "b", "w", "end")
            .Build());
        await f.H.WaitForAsync(first, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 4);
        var second = await f.H.RunAsync(UseProfile(f, "k2", "после двух", close: false));
        await f.H.WaitForStatusAsync(second, ProcessStatus.Waiting);

        // Ждущий получает профиль, только когда браузеры первого закрыты все: к закрываемому профилю он не подключается.
        f.H.Time.Advance(TimeSpan.FromSeconds(31));
        await f.H.WaitForStatusAsync(first, ProcessStatus.Completed);
        await f.H.WaitForStatusAsync(second, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
    }

    [Fact]
    public async Task Open_browser_after_a_profile_under_the_same_name_opens_a_browser_not_the_profile()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(new ScenarioBuilder("Профиль, потом браузер")
            .Node("start", "start")
            .Node("ads", "adspower.open", new { profileId = "k1", url = f.Site.Url("/controls"), closeOnFinish = false })
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/form")))
            .Node("fill", "browser.fill", new { selector = "#user", text = "в браузере" })
            .Node("click", "browser.click", new { selector = "#go", expect = "#done" })
            .Node("end", "end")
            .Chain("start", "ads", "open", "fill", "click", "end")
            .Build());
        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
        // Аккаунт профиля не тронут: форма открывалась и отправлялась в обычном браузере.
        Assert.DoesNotContain(await f.Ads!.TabUrlsAsync("k1"), url => url.Contains("/form", StringComparison.Ordinal) ||
                                                                     url.Contains("/done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Browser_stays_usable_when_a_profile_opened_under_its_name_fails_to_start()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        f.Ads!.StartErrors["k1"] = "user account does not exist";
        var id = await f.H.RunAsync(new ScenarioBuilder("Браузер, потом неудачный профиль")
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/form")))
            .Node("ads", "adspower.open", new { profileId = "k1" })
            .Node("fill", "browser.fill", new { selector = "#user", text = "после ошибки" })
            .Node("click", "browser.click", new { selector = "#go", expect = "#done" })
            .Node("end", "end")
            .Edge("start", "open").Edge("open", "ads").Edge("ads", "fill", Ports.Error).Edge("ads", "end", Ports.Out)
            .Edge("fill", "click").Edge("click", "end")
            .Build());
        // Профиль точно не открылся — браузер снова «main» и работает дальше по ветке «Ошибка».
        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
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
    public async Task Dialog_in_a_tab_of_the_owner_is_not_answered_and_does_not_stop_the_process()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        await f.Ads!.OpenByOwnerAsync("k1");
        var id = await f.H.RunAsync(new ScenarioBuilder("Своя вкладка, окно у владельца")
            .Node("start", "start")
            .Node("open", "adspower.open", new { profileId = "k1", url = f.Site.Url("/form"), ifOpen = "tab" })
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            .Node("fill", "browser.fill", new { selector = "#user", text = "своя" })
            .Node("click", "browser.click", new { selector = "#go", expect = "#done" })
            .Node("end", "end")
            .Chain("start", "open", "w", "fill", "click", "end")
            .Build());
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 3);

        // У владельца в его вкладке сайт показал окно: оно не процесса — Pepa на него не отвечает и шаг из-за него не падает.
        await f.Ads.OpenTabAsync("k1", f.Site.Url("/owner-alert"));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        var done = await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
        Assert.DoesNotContain(await f.H.Store.GetJournalAsync(id), j => j.Message.Contains("Окно владельца", StringComparison.Ordinal));
        Assert.NotNull(done);
    }

    [Fact]
    public async Task After_restart_a_window_the_process_opened_in_the_owners_profile_is_still_its_own()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        await f.Ads!.OpenByOwnerAsync("k1");
        var id = await f.H.RunAsync(new ScenarioBuilder("Окно процесса")
            .Node("start", "start")
            .Node("open", "adspower.open", new { profileId = "k1", url = f.Site.Url("/controls"), ifOpen = "tab" })
            .Node("popup", "browser.click", new { selector = "#popup" })
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            // По адресу: после переподключения порядок вкладок уже не тот, что при открытии.
            .Node("tab", "browser.tab", new { which = "url", urlContains = "from=popup", waitSeconds = 3 })
            .Node("switched", "end", new { message = "окно процесса" })
            .Node("none", "end", new { message = "окна нет" })
            .Chain("start", "open", "popup", "w", "tab")
            .Edge("tab", "switched", Ports.Out).Edge("tab", "none", Ports.Timeout)
            .Build());
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 4);
        // Окно открывается уже после нажатия: дождаться, что Pepa запомнила его как вкладку процесса.
        await f.H.UntilAsync(() => f.Sessions.FindInfo(id, "main")?.OwnedTargets?.Count == 2, "окно процесса запомнено");

        // Pepa перезапущена: запомнена вкладка процесса, а окно, открытое с неё, — тоже процесса (не владельца).
        await f.RestartAsync();
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);
        Assert.Equal("окно процесса", (await f.H.WaitForStatusAsync(id, ProcessStatus.Completed)).StatusMessage);
    }

    [Fact]
    public async Task After_restart_the_newest_tab_is_still_the_one_opened_last()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        var id = await f.H.RunAsync(new ScenarioBuilder("Новая вкладка после перезапуска")
            .Node("start", "start")
            .Node("open", "adspower.open", new { profileId = "k1", url = f.Site.Url("/controls"), closeOnFinish = false })
            .Node("popup", "browser.click", new { selector = "#popup" })
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            .Node("tab", "browser.tab", new { which = "newest", waitSeconds = 3 })
            .Node("end", "end")
            .Chain("start", "open", "popup", "w")
            .Edge("w", "tab").Edge("tab", "end", Ports.Out)
            .Build());
        await f.H.WaitForAsync(id, p => p.Status == ProcessStatus.Waiting && p.StepCount >= 4);
        await f.H.UntilAsync(() => f.Sessions.FindInfo(id, "main")?.TabOrder?.Count == 2, "порядок вкладок запомнен");

        // После перезапуска браузер отдаёт вкладки в своём порядке; «новая» — всё равно открытая последней (окно с /second).
        await f.RestartAsync();
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);
        var done = await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        var tab = (await f.H.StepsAsync(id)).Last(s => s.NodeId == "tab");
        Assert.Contains("from=popup", tab.Output!["url"]!.GetValue<string>());
        Assert.NotNull(done);
    }

    [Fact]
    public async Task Profile_that_adspower_says_is_still_opening_stays_pepas_and_is_used_once_it_opens()
    {
        TestBrowser.RequireCdpBrowser();
        await using var f = await BrowserFixture.CreateAsync(withAdsPower: true);
        f.Ads!.StartErrors["k1"] = "The browser is opening, please wait";
        var id = await f.H.RunAsync(UseProfile(f, "k1", "открывается"));
        // «Ещё открывается» — итог неизвестен; проверка видит, что профиль пока не открыт, и шаг ждёт решения.
        await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains(await f.H.Store.GetJournalAsync(id), j => j.Message.Contains("ещё открывает", StringComparison.Ordinal));

        // Профиль открылся от того запроса Pepa: он остаётся профилем Pepa — повтор работает в нём, а не «открыт не Pepa».
        f.Ads.StartErrors.Remove("k1");
        await f.Ads.OpenByOwnerAsync("k1");
        await f.H.Engine.ResumeAsync(id, ResumeAction.Retry);
        await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, f.Site.Submits);
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
