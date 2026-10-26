using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Browser.Tests;

/// <summary>Списки, флажки, загрузка файла, вкладки, ожидание адреса и JavaScript — на настоящем Chromium.</summary>
public sealed class BrowserFormTests
{
    [Fact]
    public async Task Select_check_upload_tabs_url_and_script()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(f.Workspace, "документы"));
        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "документы", "скан.txt"), "скан", TestContext.Current.CancellationToken);

        var id = await f.H.RunAsync(new ScenarioBuilder("Элементы")
            .Variable("city", VariableType.Any).Variable("checked", VariableType.Any).Variable("files", VariableType.Any)
            .Variable("tabUrl", VariableType.Any).Variable("backUrl", VariableType.Any)
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/controls")))
            .Node("select", "browser.select", new { selector = "#city", option = "Санкт-Петербург" })
            .Node("selectValue", "browser.evaluate", new { script = "document.getElementById('city').value", saveTo = "city" })
            .Node("check", "browser.check", new { selector = "#agree" })
            .Node("checkAgain", "browser.check", new { selector = "#agree" })
            .Node("readChecked", "browser.evaluate", new { script = "() => document.getElementById('agree').checked", saveTo = "checked" })
            .Node("upload", "browser.upload", new { selector = "#file", path = "документы/скан.txt" })
            .Node("readFiles", "browser.evaluate", new { script = "Array.from(document.getElementById('file').files).map(f => f.name)", saveTo = "files" })
            .Node("popup", "browser.click", new { selector = "#popup" })
            .Node("tab", "browser.tab", new { which = "newest" })
            .Node("tabUrl", "browser.read", new { what = "url", saveTo = "tabUrl" })
            .Node("back", "browser.tab", new { which = "url", urlContains = "/controls" })
            .Node("later", "browser.click", new { selector = "#later" })
            .Node("waitUrl", "browser.waitUrl", new { contains = "step=done", waitSeconds = 10 })
            .Node("backUrl", "browser.read", new { what = "url", saveTo = "backUrl" })
            .Node("noTab", "browser.tab", new { which = "url", urlContains = "нет-такой", waitSeconds = 1 })
            .Node("end", "end", new { message = "всё" })
            .Node("missing", "end", new { message = "вкладки нет" })
            .Chain("start", "open", "select", "selectValue", "check", "checkAgain", "readChecked", "upload", "readFiles", "popup", "tab",
                "tabUrl", "back", "later", "waitUrl", "backUrl", "noTab", "end")
            .Edge("noTab", "missing", Ports.Timeout)
            .Build());

        var done = await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("вкладки нет", done.StatusMessage);
        Assert.Equal("spb", done.Variables["city"]!.GetValue<string>());
        Assert.True(done.Variables["checked"]!.GetValue<bool>());
        Assert.Equal(["скан.txt"], done.Variables["files"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("from=popup", done.Variables["tabUrl"]!.GetValue<string>());
        Assert.Contains("step=done", done.Variables["backUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_option_and_wrong_element_are_reported_and_nothing_changes()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var missing = await f.H.RunAsync(Single(f, "browser.select", new { selector = "#city", option = "Казань", waitSeconds = 2 }));
        Assert.Contains("нет варианта «Казань»", (await f.H.WaitForStatusAsync(missing, ProcessStatus.NeedsAttention)).StatusMessage);

        var notSelect = await f.H.RunAsync(Single(f, "browser.select", new { selector = "#fake-select", option = "Москва" }));
        Assert.Contains("не выпадающий список", (await f.H.WaitForStatusAsync(notSelect, ProcessStatus.NeedsAttention)).StatusMessage);

        var noFile = await f.H.RunAsync(Single(f, "browser.upload", new { selector = "#file", path = "нет/файла.txt" }));
        Assert.Contains("нет в рабочей папке", (await f.H.WaitForStatusAsync(noFile, ProcessStatus.NeedsAttention)).StatusMessage);
    }

    [Fact]
    public async Task Select_and_check_are_verified_by_reading_the_page()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var session = await f.Sessions.OpenLocalAsync("p1", "main", new LocalBrowserOptions { Headless = true, Channel = TestBrowser.Channel ?? "msedge" },
            null, true, CancellationToken.None);
        var page = session.CurrentPage();
        await page.GotoAsync(f.Site.Url("/controls"));
        await page.SelectOptionAsync("#city", new SelectOptionValue { Label = "Санкт-Петербург" });
        await page.CheckAsync("#agree");

        var select = new SelectOptionNode(f.Sessions);
        var check = new CheckNode(f.Sessions);
        Assert.Equal(VerificationOutcome.Done, (await select.VerifyAsync(Ctx(new() { ["selector"] = "#city", ["option"] = "Санкт-Петербург" }), default)).Outcome);
        Assert.Equal(VerificationOutcome.NotDone, (await select.VerifyAsync(Ctx(new() { ["selector"] = "#city", ["option"] = "Москва" }), default)).Outcome);
        Assert.Equal(VerificationOutcome.Done, (await select.VerifyAsync(Ctx(new() { ["selector"] = "#city", ["option"] = "spb", ["by"] = "value" }), default)).Outcome);
        Assert.Equal(VerificationOutcome.Done, (await check.VerifyAsync(Ctx(new() { ["selector"] = "#agree" }), default)).Outcome);
        Assert.Equal(VerificationOutcome.NotDone, (await check.VerifyAsync(Ctx(new() { ["selector"] = "#agree", ["state"] = "uncheck" }), default)).Outcome);

        static NodeExecutionContext Ctx(JsonObject input)
        {
            input["session"] = "main";
            return BrowserScenarioTests.Context("p1", input);
        }
    }

    private static ScenarioDefinition Single(BrowserFixture f, string type, object config) => new ScenarioBuilder("Один блок")
        .Node("start", "start")
        .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/controls")))
        .Node("act", type, config)
        .Node("end", "end")
        .Chain("start", "open", "act", "end")
        .Build();
}
