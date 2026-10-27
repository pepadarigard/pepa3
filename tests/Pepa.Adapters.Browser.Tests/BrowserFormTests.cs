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
    public async Task Slow_checkbox_is_awaited_and_consumed_upload_is_not_repeated()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(f.Workspace, "скан.txt"), "скан", TestContext.Current.CancellationToken);
        var retry = new NodeSettings { RetryCount = 2, RetryDelaySeconds = 0 };
        var id = await f.H.RunAsync(new ScenarioBuilder("Медленный сайт")
            .Variable("clicks", VariableType.Any)
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/controls")))
            .Node("check", "browser.check", new { selector = "#slow" }, retry)
            .Node("clicks", "browser.evaluate", new { script = "window.slowClicks", saveTo = "clicks" })
            .Node("upload", "browser.upload", new { selector = "#consume", path = "скан.txt" }, retry)
            .Node("end", "end")
            .Chain("start", "open", "check", "clicks", "upload", "end")
            .Build());

        // Флажок: одно нажатие, отметка появилась через 0,8 с — Pepa дождалась, а не нажала снова (что сняло бы отметку).
        // Файл: сайт забрал его и очистил поле — Pepa не выбирает файл второй раз, а спрашивает.
        var process = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.UnknownStep, process.Attention);
        Assert.Equal(1, process.Variables["clicks"]!.GetValue<double>());
        Assert.Contains("сайт сразу забрал", process.StatusMessage);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal(1, f.Site.Submits);
        Assert.Single(await f.H.StepsAsync(id), s => s.NodeId == "upload");
    }

    [Fact]
    public async Task Script_gets_variables_as_data_not_as_code()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        // Текст из сообщения с кавычками и кодом остаётся текстом.
        var id = await f.H.RunAsync(new ScenarioBuilder("Переменные в коде")
            .Variable("message", VariableType.Text, JsonValue.Create("'); window.hacked = true; ('"))
            .Variable("length", VariableType.Any).Variable("hacked", VariableType.Any)
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/controls")))
            .Node("length", "browser.evaluate", new { script = "() => vars.message.length", saveTo = "length" })
            .Node("hacked", "browser.evaluate", new { script = "window.hacked === true", saveTo = "hacked" })
            .Node("end", "end")
            .Chain("start", "open", "length", "hacked", "end")
            .Build());
        var done = await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(28, done.Variables["length"]!.GetValue<double>());
        Assert.False(done.Variables["hacked"]!.GetValue<bool>());

        // Старый способ ({{ }} в коде) отклоняется проверкой сценария, а не выполняется.
        Assert.Equal(["script"], new EvaluateNode(f.Sessions).Validate(new NodeDefinition
        {
            Id = "e", Type = EvaluateNode.TypeName, Config = new JsonObject { ["script"] = "'{{ vars.message }}'.length" },
        }).Select(i => i.Field));
        var all = new JsonObject { ["a"] = 1, ["secret"] = "x" };
        Assert.Equal("""{"a":1}""", EvaluateNode.UsedVariables("vars.a + 1", all).ToJsonString());
        Assert.Equal("""{"a":1}""", EvaluateNode.UsedVariables("(v) => v.a + 1", all).ToJsonString());
        Assert.Equal("""{"a":1}""", EvaluateNode.UsedVariables("function (vars) { return vars.a; }", all).ToJsonString());
        Assert.Equal("""{"a":1}""", EvaluateNode.UsedVariables("x => x.a", all).ToJsonString());
        Assert.Equal(all.ToJsonString(), EvaluateNode.UsedVariables("JSON.stringify(vars)", all).ToJsonString());
        Assert.Equal(all.ToJsonString(), EvaluateNode.UsedVariables("(v) => Object.keys(v)", all).ToJsonString());
        Assert.Equal(all.ToJsonString(), EvaluateNode.UsedVariables("({ a }) => a", all).ToJsonString());
        Assert.Equal("{}", EvaluateNode.UsedVariables("document.vars.a", all).ToJsonString());
    }

    [Fact]
    public async Task Scripts_written_as_statements_work_and_a_syntax_error_is_a_clear_failure()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        // Так писали код до передачи переменных данными — он должен работать по-прежнему.
        var id = await f.H.RunAsync(new ScenarioBuilder("Инструкции")
            .Variable("count", VariableType.Number, JsonValue.Create(3))
            .Variable("a", VariableType.Any).Variable("b", VariableType.Any).Variable("c", VariableType.Any).Variable("d", VariableType.Any)
            .Variable("e", VariableType.Any).Variable("strict", VariableType.Any).Variable("plain", VariableType.Any)
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/controls")))
            .Node("a", "browser.evaluate", new { script = "document.title.length > 0;", saveTo = "a" })
            .Node("b", "browser.evaluate", new { script = "const n = vars.count; n * 2", saveTo = "b" })
            .Node("c", "browser.evaluate", new { script = "function (v) { return v.count + 1; }", saveTo = "c" })
            .Node("d", "browser.evaluate", new { script = "// комментарий\nvars.count * 10", saveTo = "d" })
            .Node("e", "browser.evaluate", new { script = "async () => [0 / 0, 1]", saveTo = "e" })
            // Директива в начале кода остаётся директивой: строгий режим действует.
            .Node("strict", "browser.evaluate", new { script = "'use strict';\n(function () { return this === undefined; })()", saveTo = "strict" })
            // Код — только строка: она и есть результат.
            .Node("plain", "browser.evaluate", new { script = "'готово'", saveTo = "plain" })
            .Node("end", "end")
            .Chain("start", "open", "a", "b", "c", "d", "e", "strict", "plain", "end")
            .Build());
        var done = await f.H.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.True(done.Variables["a"]!.GetValue<bool>());
        Assert.Equal(6, done.Variables["b"]!.GetValue<double>());
        Assert.Equal(4, done.Variables["c"]!.GetValue<double>());
        Assert.Equal(30, done.Variables["d"]!.GetValue<double>());
        Assert.Equal("[null,1]", done.Variables["e"]!.ToJsonString());
        Assert.True(done.Variables["strict"]!.GetValue<bool>());
        Assert.Equal("готово", done.Variables["plain"]!.GetValue<string>());

        // Ошибка в записи кода — код не выполнялся: обычная ошибка шага, а не «неизвестно, выполнился ли».
        var broken = await f.H.RunAsync(Single(f, "browser.evaluate", new { script = "window.ran = true; )(" }));
        var stopped = await f.H.WaitForStatusAsync(broken, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, stopped.Attention);
        Assert.Contains("код не выполнялся", stopped.StatusMessage);

        // «return» вне функции: разбор не прошёл — тоже «не выполнялся».
        var returned = await f.H.RunAsync(Single(f, "browser.evaluate", new { script = "window.ran = true; return 1" }));
        var notRun = await f.H.WaitForStatusAsync(returned, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, notRun.Attention);
        Assert.Contains("код не выполнялся", notRun.StatusMessage);

        // Ошибка во время выполнения (код уже что-то сделал) — итог неизвестен, а не «ошибка селектора».
        var failed = await f.H.RunAsync(Single(f, "browser.evaluate", new { script = "window.ran = true; JSON.parse('не json')" }));
        var unknown = await f.H.WaitForStatusAsync(failed, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.UnknownStep, unknown.Attention);
        Assert.Contains("начал выполняться", unknown.StatusMessage);
    }

    [Theory]
    [InlineData("'use strict';\nx = 1", 13)]
    [InlineData("// заметка\n\"use strict\"\nx()", 23)]
    [InlineData("'a'; 'b'; x", 9)]
    [InlineData("x = 1", 0)]
    [InlineData("'abc'.length", 0)]
    [InlineData("'abc'\n.length", 0)]
    [InlineData("'не закрыта", 0)]
    public void Start_mark_goes_after_leading_directives(string script, int end)
    {
        Assert.Equal(end, EvaluateNode.DirectivesEnd(script));
        // Отметка — в той же строке: номера строк в ошибках не сдвигаются.
        Assert.Equal(script.Count(c => c == '\n'), EvaluateNode.Marked(script).Count(c => c == '\n'));
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
