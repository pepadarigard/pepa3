using Microsoft.Playwright;

namespace Pepa.Adapters.Browser.Tests;

/// <summary>Запись действий: «пользователь» (Playwright, настоящие события браузера) работает на сайте, Pepa записывает шаги.</summary>
public sealed class BrowserRecorderTests
{
    [Fact]
    public async Task Clicks_typing_choices_and_keys_become_steps_with_stable_selectors()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        await using var recorder = new BrowserRecorder(f.Sessions);
        await recorder.StartAsync(new RecorderStart(RecorderBrowser.Pepa, null, f.Site.Url("/form"), TestBrowser.Channel ?? "msedge", Headless: true),
            TestContext.Current.CancellationToken);
        var page = (await f.Sessions.GetAsync(BrowserRecorder.SessionOwner, "main", TestContext.Current.CancellationToken)).CurrentPage();

        await page.ClickAsync("#user");
        await page.Keyboard.TypeAsync("Пе");
        await page.Keyboard.TypeAsync("па");
        await page.ClickAsync("#go");
        await page.Locator("#done").WaitForAsync();
        await page.ClickAsync("#next");
        await page.WaitForURLAsync("**/second");
        await page.GotoAsync(f.Site.Url("/controls"));
        // Как человек: SelectOptionAsync меняет список программно, а запись слушает только настоящие действия.
        await page.FocusAsync("#city");
        await page.Keyboard.PressAsync("ArrowDown");
        await page.CheckAsync("#agree");
        await page.FillAsync("input[name=pass]", "секрет");
        await page.PressAsync("input[name=pass]", "Enter");
        await page.Locator(".opaque b").First.ClickAsync();

        // Селектор «по положению» указывает ровно на один элемент (проверяем, пока браузер открыт).
        await WaitUntilAsync(() => recorder.State.Actions.Count >= 10);
        var positional = recorder.State.Actions[^1].Selector!;
        Assert.Equal(1, await page.Locator(positional).CountAsync());

        var actions = await recorder.StopAsync(TestContext.Current.CancellationToken);
        var steps = actions.Select(a => $"{a.Kind} {a.Selector ?? a.Url} {a.Value}".Trim()).ToList();
        Assert.Equal(
        [
            $"goto {f.Site.Url("/form")}",
            "fill #user Пепа",
            "click #go",
            "click #next",
            $"goto {f.Site.Url("/controls")}",
            "select #city Санкт-Петербург",
            "check #agree",
            "fill input[name=\"pass\"]",
            "press input[name=\"pass\"] Enter",
        ], steps.Take(9));

        // Пароль не записывается; элемент без признаков — по положению, с пометкой.
        Assert.True(actions[7].Secret);
        Assert.Equal("", actions[7].Value);
        var last = actions[^1];
        Assert.Equal("click", last.Kind);
        Assert.Contains("nth-of-type", last.Selector);
        Assert.NotNull(last.Note);
        Assert.False(recorder.State.Recording);
    }

    [Fact]
    public async Task Typed_passwords_and_card_codes_never_reach_the_steps_or_their_labels()
    {
        // Поле без подписи и подсказки: раньше подпись шага бралась из значения поля, и пароль был виден в окне записи.
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        await using var recorder = new BrowserRecorder(f.Sessions);
        await recorder.StartAsync(new RecorderStart(RecorderBrowser.Pepa, null, f.Site.Url("/recorder-login"), TestBrowser.Channel ?? "msedge", Headless: true),
            TestContext.Current.CancellationToken);
        var page = (await f.Sessions.GetAsync(BrowserRecorder.SessionOwner, "main", TestContext.Current.CancellationToken)).CurrentPage();

        await page.ClickAsync("#pw");
        await page.Keyboard.TypeAsync("hunter2secret");
        await page.Keyboard.PressAsync("Enter");
        await page.ClickAsync("#cvc");
        await page.Keyboard.TypeAsync("731");
        await page.ClickAsync("#shown");
        await page.Keyboard.TypeAsync("Qwerty123");
        await page.ClickAsync("#note");
        await page.Keyboard.TypeAsync("обычный текст");
        await page.Keyboard.PressAsync("Enter");

        await WaitUntilAsync(() => recorder.State.Actions.Count(a => a.Kind == "press") >= 2);
        var actions = await recorder.StopAsync(TestContext.Current.CancellationToken);
        var fills = actions.Where(a => a.Kind == "fill").ToDictionary(a => a.Selector!);
        Assert.True(fills["#pw"].Secret);
        Assert.True(fills["#cvc"].Secret);
        Assert.True(fills["#shown"].Secret);
        Assert.All(new[] { "#pw", "#cvc", "#shown" }, s => Assert.Equal("", fills[s].Value));
        Assert.False(fills["#note"].Secret);
        Assert.Equal("обычный текст", fills["#note"].Value);
        foreach (var action in actions)
        {
            foreach (var secret in new[] { "hunter2secret", "hunter", "731", "Qwerty123", "обычный" })
            {
                Assert.DoesNotContain(secret, action.Label ?? "");
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Only_one_recording_at_a_time()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        await using var recorder = new BrowserRecorder(f.Sessions);
        await recorder.StartAsync(new RecorderStart(RecorderBrowser.Pepa, null, null, TestBrowser.Channel ?? "msedge", Headless: true),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RecorderException>(() =>
            recorder.StartAsync(new RecorderStart(RecorderBrowser.Pepa, null, null, TestBrowser.Channel ?? "msedge", Headless: true),
                TestContext.Current.CancellationToken));
        await recorder.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(recorder.State.Actions);
    }
}
