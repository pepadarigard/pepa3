using System.Text.Json;
using System.Text.Json.Nodes;
using Pepa.Core;
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

    [Fact]
    public async Task Snapshot_is_not_saved_when_a_secret_value_is_visible_on_the_page()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        ScenarioDefinition Typed(bool typeSecret) => new ScenarioBuilder("Секрет на странице")
            .Variable("token", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/form")))
            .Node("fill", "browser.fill", new { selector = "#user", text = typeSecret ? "{{ vars.token }}" : "Пепа" })
            .Node("click", "browser.click", new { selector = "#missing", waitSeconds = 1 })
            .Node("end", "end")
            .Chain("start", "open", "fill", "click", "end")
            .Build();

        // Секрет введён в обычное поле — на картинке он был бы виден: картинки нет, адрес и заголовок есть.
        var shown = await f.H.RunAsync(Typed(typeSecret: true), new JsonObject { ["token"] = "Tok-998877-secret" });
        await f.H.WaitForStatusAsync(shown, ProcessStatus.NeedsAttention);
        var snapshot = (await f.H.StepsAsync(shown)).Single(s => s.State == StepState.Failed).Output!["snapshot"]!;
        Assert.Null(snapshot["image"]);
        Assert.Contains("видно секретное значение", snapshot["note"]!.GetValue<string>());
        Assert.Equal("Форма", snapshot["title"]!.GetValue<string>());
        Assert.Empty(f.Snapshots.Saved);
        await f.H.Engine.CancelAsync(shown);

        // У процесса есть секрет, но на странице его нет — снимок сохраняется.
        var hidden = await f.H.RunAsync(Typed(typeSecret: false), new JsonObject { ["token"] = "Tok-998877-secret" });
        await f.H.WaitForStatusAsync(hidden, ProcessStatus.NeedsAttention);
        Assert.NotNull((await f.H.StepsAsync(hidden)).Single(s => s.State == StepState.Failed).Output!["snapshot"]!["image"]);
        Assert.Single(f.Snapshots.Saved);
        await f.H.Engine.CancelAsync(hidden);
    }

    [Fact]
    public async Task Snapshot_is_not_saved_when_the_secret_is_in_a_web_component_or_differs_only_by_line_breaks()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        ScenarioDefinition Fill(string page, string selector) => new ScenarioBuilder("Секрет в поле")
            .Variable("token", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url(page)))
            .Node("fill", "browser.fill", new { selector, text = "{{ vars.token }}" })
            .Node("click", "browser.click", new { selector = "#missing", waitSeconds = 1 })
            .Node("end", "end")
            .Chain("start", "open", "fill", "click", "end")
            .Build();

        // Поле в теневом DOM веб-компонента.
        var shadow = await f.H.RunAsync(Fill("/shadow", "#token"), new JsonObject { ["token"] = "Tok-998877-secret" });
        await f.H.WaitForStatusAsync(shadow, ProcessStatus.NeedsAttention);
        // Значение из файла с переводом строки: однострочное поле его убирает.
        var crlf = await f.H.RunAsync(Fill("/form", "#user"), new JsonObject { ["token"] = "Tok-554433-secret\r\n" });
        await f.H.WaitForStatusAsync(crlf, ProcessStatus.NeedsAttention);

        foreach (var id in new[] { shadow, crlf })
        {
            var snapshot = (await f.H.StepsAsync(id)).Single(s => s.State == StepState.Failed).Output!["snapshot"]!;
            Assert.Null(snapshot["image"]);
            Assert.Contains("видно секретное значение", snapshot["note"]!.GetValue<string>());
            await f.H.Engine.CancelAsync(id);
        }

        Assert.Empty(f.Snapshots.Saved);
    }

    private static ScenarioDefinition DialogScenario(BrowserFixture f, string selector, object? answer = null) => new ScenarioBuilder("Окно сайта")
        .Node("start", "start")
        .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/dialogs")))
        .Node("click", "browser.click", answer is null ? new { selector } : MergeSelector(selector, answer))
        .Node("end", "end")
        .Chain("start", "open", "click", "end")
        .Build();

    private static JsonObject MergeSelector(string selector, object answer)
    {
        var config = JsonSerializer.SerializeToNode(answer)!.AsObject();
        config["selector"] = selector;
        return config;
    }

    private static async Task<string?> PageTextAsync(BrowserFixture f, string processId, string selector) =>
        await (await f.Sessions.GetAsync(processId, "main", CancellationToken.None)).CurrentPage().TextContentAsync(selector);

    [Fact]
    public async Task Site_question_stops_the_process_with_its_text_unless_an_answer_is_chosen()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();

        // По умолчанию: «Отмена» и остановка — что сделал сайт, решает пользователь.
        var stopped = await f.H.RunAsync(DialogScenario(f, "#del"));
        var process = await f.H.WaitForStatusAsync(stopped, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.UnknownStep, process.Attention);
        Assert.Contains("вопрос «Удалить запись?»", process.StatusMessage);
        Assert.Contains("ответ «Отмена»", process.StatusMessage);
        Assert.Equal("отменено", await PageTextAsync(f, stopped, "#out"));
        await f.H.Engine.CancelAsync(stopped);

        // Ответ выбран — процесс идёт дальше, окно видно в журнале.
        var accepted = await f.H.RunAsync(DialogScenario(f, "#del", new { dialog = "accept" }));
        await f.H.WaitForStatusAsync(accepted, ProcessStatus.Completed);
        Assert.Contains(await f.H.Store.GetJournalAsync(accepted), j => j.Message.Contains("«Удалить запись?»", StringComparison.Ordinal));

        var asked = await f.H.RunAsync(new ScenarioBuilder("Запрос")
            .Variable("answer", VariableType.Any)
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/dialogs")))
            .Node("click", "browser.click", new { selector = "#ask", dialog = "accept", dialogText = "Анна" })
            .Node("read", "browser.read", new { what = "text", selector = "#out", saveTo = "answer" })
            .Node("end", "end", new { message = "{{ vars.answer }}" })
            .Chain("start", "open", "click", "read", "end")
            .Build());
        Assert.Equal("имя: Анна", (await f.H.WaitForStatusAsync(asked, ProcessStatus.Completed)).StatusMessage);
    }

    [Fact]
    public async Task Site_message_between_steps_stops_the_next_step_before_it_acts()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var id = await f.H.RunAsync(new ScenarioBuilder("Окно между шагами")
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/dialogs")))
            .Node("later", "browser.click", new { selector = "#later" })
            .Node("wait", "event.wait", new { source = "manual", eventType = "go" })
            .Node("save", "browser.click", new { selector = "#save", dialog = "accept" })
            .Node("end", "end")
            .Chain("start", "open", "later", "wait")
            .Edge("wait", "save", Ports.Event).Edge("save", "end")
            .Build());
        await f.H.WaitForStatusAsync(id, ProcessStatus.Waiting);

        // Сайт показывает сообщение по таймеру, пока процесс ждёт.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (await PageTextAsync(f, id, "#out") != "после окна")
        {
            Assert.True(DateTime.UtcNow < deadline, "Сайт не показал окно.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await f.H.Engine.PublishEventAsync(new EventInput { Source = "manual", Type = "go" });
        var stopped = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, stopped.Attention);
        Assert.Contains("сообщение «Сессия скоро закончится»", stopped.StatusMessage);
        Assert.Contains("не выполнялся", stopped.StatusMessage);
        Assert.Equal("после окна", await PageTextAsync(f, id, "#out"));
        await f.H.Engine.CancelAsync(id);
    }

    private static ScenarioDefinition ClickScenario(BrowserFixture f, string selector) => new ScenarioBuilder("Нажать")
        .Node("start", "start")
        .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/form")))
        .Node("click", "browser.click", new { selector, waitSeconds = 1 })
        .Node("end", "end")
        .Chain("start", "open", "click", "end")
        .Build();
}
