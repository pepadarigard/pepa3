using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Adapters.Browser.Tests;

/// <summary>«Ждать одно из» (D-084): свой выход у каждого исхода страницы, ничего или два сразу — остановка.</summary>
public sealed class BrowserWaitAnyTests
{
    private static ScenarioDefinition Login(BrowserFixture f, string user, bool connectNothing = true)
    {
        var builder = new ScenarioBuilder("Вход")
            .Variable("result", VariableType.Any)
            .Node("start", "start")
            .Node("open", "browser.open", BrowserFixture.OpenConfig(f.Site.Url("/login")))
            .Node("fill", "browser.fill", new { selector = "#user", text = user })
            .Node("enter", "browser.click", new { selector = "#enter" })
            .Node("any", "browser.waitAny", new
            {
                name1 = "Кабинет", kind1 = "element", target1 = "#dashboard",
                name2 = "Неверный пароль", kind2 = "text", target2 = "неверный пароль",
                name3 = "Запрос кода", kind3 = "url", target3 = "/verify",
                waitSeconds = 3, saveTo = "result",
            })
            .Node("cabinet", "end", new { message = "кабинет: {{ vars.result.outcome }}" })
            .Node("password", "end", new { outcome = "failure", message = "пароль" })
            .Node("code", "end", new { message = "код" })
            .Chain("start", "open", "fill", "enter", "any")
            .Edge("any", "cabinet", "o1").Edge("any", "password", "o2").Edge("any", "code", "o3");
        if (connectNothing)
        {
            builder.Node("nothing", "end", new { message = "ничего" }).Edge("any", "nothing", Ports.Timeout);
        }

        return builder.Build();
    }

    [Fact]
    public async Task Each_outcome_goes_to_its_output()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        Assert.Equal("кабинет: 1", (await f.H.WaitForStatusAsync(await f.H.RunAsync(Login(f, "ok")), ProcessStatus.Completed)).StatusMessage);
        Assert.Equal("пароль", (await f.H.WaitForStatusAsync(await f.H.RunAsync(Login(f, "bad")), ProcessStatus.Failed)).StatusMessage);
        Assert.Equal("код", (await f.H.WaitForStatusAsync(await f.H.RunAsync(Login(f, "code")), ProcessStatus.Completed)).StatusMessage);
        Assert.Equal("ничего", (await f.H.WaitForStatusAsync(await f.H.RunAsync(Login(f, "nobody")), ProcessStatus.Completed)).StatusMessage);
    }

    [Fact]
    public async Task Nothing_with_the_output_unconnected_stops_with_a_snapshot_of_the_page()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var id = await f.H.RunAsync(Login(f, "nobody", connectNothing: false));
        var stopped = await f.H.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("не появилось ни одного исхода", stopped.StatusMessage);
        Assert.Contains("Выход «Ничего из этого» не подключён", stopped.StatusMessage);
        var failed = (await f.H.StepsAsync(id)).Single(s => s.State == StepState.Failed);
        Assert.Equal(f.Site.Url("/login"), failed.Output!["snapshot"]!["url"]!.GetValue<string>());
        Assert.Single(f.Snapshots.Saved);
    }

    [Fact]
    public async Task Two_outcomes_at_once_stop_instead_of_guessing()
    {
        TestBrowser.RequireBrowser();
        await using var f = await BrowserFixture.CreateAsync();
        var stopped = await f.H.WaitForStatusAsync(await f.H.RunAsync(Login(f, "both")), ProcessStatus.NeedsAttention);
        Assert.Contains("сразу несколько исходов: «Кабинет», «Неверный пароль»", stopped.StatusMessage);
    }
}
