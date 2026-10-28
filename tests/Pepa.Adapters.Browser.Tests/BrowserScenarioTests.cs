using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Browser.Tests;

/// <summary>Блоки браузера на настоящем Chromium и тестовом сайте.</summary>
public sealed class BrowserScenarioTests
{
    [Fact]
    public async Task Open_fill_click_read_screenshot_close()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var id = await f.H.RunAsync(new ScenarioBuilder("Сайт")
            .Variable("answer", VariableType.Any).Variable("items", VariableType.Any).Variable("title", VariableType.Any)
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/form")))
            .Node("fill", "browser.fill", new { selector = "#user", text = "Пепа" })
            .Node("click", "browser.click", new { selector = "#go", expect = "#done" })
            .Node("read", "browser.read", new { what = "text", selector = "#done", saveTo = "answer" })
            .Node("list", "browser.read", new { what = "texts", selector = ".item", saveTo = "items" })
            .Node("title", "browser.read", new { what = "title", saveTo = "title" })
            .Node("shot", "browser.screenshot", new { path = "снимки/форма.png" })
            .Node("close", "browser.close", new { })
            .Node("end", "end")
            .Chain("start", "open", "fill", "click", "read", "list", "title", "shot", "close", "end")
            .Build());

        var done = await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("Принято: Пепа", done.Variables["answer"]!.GetValue<string>());
        Assert.Equal(["Один", "Два", "Три"], done.Variables["items"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Форма", done.Variables["title"]!.GetValue<string>());
        Assert.Equal(1, f.Site.Submits);
        Assert.True(new FileInfo(Path.Combine(f.Workspace, "снимки", "форма.png")).Length > 0);
        Assert.False(f.Sessions.IsOpen(id, "main"));
    }

    [Fact]
    public async Task Missing_element_stops_process_and_nothing_is_clicked()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var id = await f.H.RunAsync(ClickScenario(f, "#missing"));
        var process = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Contains("не появился", process.StatusMessage);
        Assert.Equal(0, f.Site.Submits);

        // Что было на странице в момент ошибки — снимок, адрес и заголовок в результате шага.
        var failed = (await f.H.StepsAsync(id)).Single(s => s.State == StepState.Failed);
        var snapshot = failed.Output!["snapshot"]!.AsObject();
        Assert.Equal(f.Site.Url("/form"), snapshot["url"]!.GetValue<string>());
        Assert.Equal("Форма", snapshot["title"]!.GetValue<string>());
        var saved = Assert.Single(f.Snapshots.Saved);
        Assert.Equal((id, snapshot["image"]!.GetValue<string>()), (saved.ProcessId, saved.Name));
        Assert.Equal([0xFF, 0xD8], saved.Image[..2]);
    }

    [Fact]
    public async Task Ambiguous_selector_is_not_clicked()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var id = await f.H.RunAsync(ClickScenario(f, ".item"));
        var process = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("несколько элементов", process.StatusMessage);
    }

    [Fact]
    public async Task Wait_for_element_that_appears_later_and_timeout_branch()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var id = await f.H.RunAsync(new ScenarioBuilder("Ждать")
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/late")))
            .Node("appear", "browser.waitFor", new { selector = "#late", waitSeconds = 10 })
            .Node("never", "browser.waitFor", new { selector = "#never", waitSeconds = 1 })
            .Node("end", "end", new { message = "дождались всего" })
            .Node("late", "end", new { message = "второго не дождались" })
            .Edge("start", "open").Edge("open", "appear").Edge("appear", "never")
            .Edge("never", "end").Edge("never", "late", Ports.Timeout)
            .Build());

        var done = await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("второго не дождались", done.StatusMessage);
    }

    [Fact]
    public async Task Lost_browser_after_restart_is_reported_and_page_is_not_touched()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var id = await f.H.RunAsync(new ScenarioBuilder("С паузой")
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/form")))
            .Node("w", "wait", new { mode = "duration", amount = 60, unit = "seconds" })
            .Node("click", "browser.click", new { selector = "#go" })
            .Node("end", "end")
            .Chain("start", "open", "w", "click", "end")
            .Build());
        await f.H.WaitForStatusAsync(id, ProcessStatus.Waiting);

        await f.RestartAsync();
        f.H.Time.Advance(TimeSpan.FromSeconds(61));
        await f.H.Engine.ResumeAsync(id, ResumeAction.Continue);
        var process = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.Equal(AttentionKind.StepFailed, process.Attention);
        Assert.Contains("был закрыт", process.StatusMessage);
        Assert.Equal(0, f.Site.Submits);
    }

    [Fact]
    public async Task Fill_verification_reads_the_field_instead_of_typing_again()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var session = await f.Sessions.OpenLocalAsync("p1", "main", new LocalBrowserOptions { Headless = true, Channel = TestBrowser.Channel ?? "msedge" },
            null, true, CancellationToken.None);
        await session.CurrentPage().GotoAsync(f.Site.Url("/form"));

        var node = new FillNode(f.Sessions);
        var context = Context("p1", new JsonObject { ["selector"] = "#user", ["text"] = "abc", ["session"] = "main", ["waitSeconds"] = 5 });
        Assert.Equal(Pepa.Core.Nodes.VerificationOutcome.NotDone, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);
        Assert.IsType<Pepa.Core.Nodes.NodeResult.Success>(await node.ExecuteAsync(context, CancellationToken.None));
        Assert.Equal(Pepa.Core.Nodes.VerificationOutcome.Done, (await node.VerifyAsync(context, CancellationToken.None)).Outcome);

        var gotoNode = new GotoNode(f.Sessions);
        var gotoContext = Context("p1", new JsonObject { ["url"] = f.Site.Url("/second"), ["session"] = "main" });
        Assert.Equal(Pepa.Core.Nodes.VerificationOutcome.NotDone, (await gotoNode.VerifyAsync(gotoContext, CancellationToken.None)).Outcome);
        await gotoNode.ExecuteAsync(gotoContext, CancellationToken.None);
        Assert.Equal(Pepa.Core.Nodes.VerificationOutcome.Done, (await gotoNode.VerifyAsync(gotoContext, CancellationToken.None)).Outcome);
    }

    internal static Pepa.Core.Nodes.NodeExecutionContext Context(string processId, JsonObject input) => new()
    {
        ProcessId = processId,
        Node = new NodeDefinition { Id = "n", Type = "test" },
        Input = input,
        Variables = new JsonObject(),
        Attempt = 1,
        Time = TimeProvider.System,
    };

    private static ScenarioDefinition ClickScenario(BrowserFixture f, string selector) => new ScenarioBuilder("Нажать")
        .Node("start", "start")
        .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/form")))
        .Node("click", "browser.click", new { selector, waitSeconds = 1 })
        .Node("end", "end")
        .Chain("start", "open", "click", "end")
        .Build();
}
