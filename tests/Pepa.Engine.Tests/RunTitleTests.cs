using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Название запуска (D-081): чем один процесс сценария отличается от другого — без секретов.</summary>
public sealed class RunTitleTests
{
    private static ScenarioBuilder Account() => new ScenarioBuilder("Аккаунт")
        .Variable("login", VariableType.Text, "anna", isInput: true)
        .Variable("password", VariableType.Text, "", isInput: true, secret: true)
        .Variable("profile", VariableType.Text, "k1a2", isInput: true)
        .Variable("note", VariableType.Text, "третье", isInput: true)
        .Node("start", "start")
        .Node("end", "end")
        .Chain("start", "end");

    [Fact]
    public async Task Without_a_template_the_title_is_the_first_two_plain_inputs_and_inputs_are_kept_without_secrets()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(Account().Build(), new JsonObject { ["login"] = "boris", ["password"] = "s3cret-pass", ["profile"] = "k9z8" });
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal("boris · k9z8", process.Title);
        Assert.NotNull(process.Inputs);
        Assert.Equal("boris", process.Inputs!["login"]!.GetValue<string>());
        Assert.Equal("k9z8", process.Inputs["profile"]!.GetValue<string>());
        Assert.Equal("третье", process.Inputs["note"]!.GetValue<string>());
        Assert.False(process.Inputs.ContainsKey("password"));
    }

    [Fact]
    public async Task Template_title_hides_secrets_and_collapses_lines()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = Account().Build() with { RunTitle = "{{ vars.login }}\n · пароль {{ vars.password }} · {{ vars.profile }}" };
        var id = await h.RunAsync(scenario, new JsonObject { ["password"] = "s3cret-pass" });
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Equal("anna · пароль •••• · k1a2", process.Title);
        Assert.DoesNotContain("s3cret-pass", process.Title);
    }

    [Fact]
    public async Task Template_that_fails_does_not_stop_the_run_and_is_noted_in_the_journal()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = Account().Build() with { RunTitle = "{{ vars.login.toUpperCase().nothing.here }}" };
        var id = await h.RunAsync(scenario);
        var process = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.Null(process.Title);
        var journal = await h.Store.GetJournalAsync(id);
        Assert.Contains(journal, j => j.Level == JournalLevel.Warning && j.Message.StartsWith("Название запуска не вычислилось", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Long_title_is_cut_and_a_scenario_without_inputs_has_no_title()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = Account().Build() with { RunTitle = "{{ vars.login }} " + new string('я', 300) };
        var id = await h.RunAsync(scenario);
        var title = (await h.WaitForStatusAsync(id, ProcessStatus.Completed)).Title!;
        Assert.Equal(120, title.Length);
        Assert.EndsWith("…", title);

        var plain = await h.RunAsync(new ScenarioBuilder("Без входа").Node("start", "start").Node("end", "end").Chain("start", "end").Build());
        var process = await h.WaitForStatusAsync(plain, ProcessStatus.Completed);
        Assert.Null(process.Title);
        Assert.Null(process.Inputs);
    }

    [Fact]
    public async Task Called_scenario_gets_its_own_title_and_no_inputs_that_carry_a_secret_of_the_caller()
    {
        await using var h = await EngineHarness.CreateAsync();
        var childId = await h.CreateScenarioAsync(new ScenarioBuilder("Дочерний")
            .Variable("token", VariableType.Text, "", isInput: true)
            .Variable("profile", VariableType.Text, "", isInput: true)
            .Node("start", "start")
            .Node("end", "end")
            .Chain("start", "end")
            .Build());
        var parent = await h.RunAsync(new ScenarioBuilder("Родитель")
            .Variable("key", VariableType.Text, "tok-77aa99", secret: true)
            .Node("start", "start")
            .Node("call", "scenario.call", new { scenario = childId, inputs = "({ token: vars.key, profile: \"p1\" })" })
            .Node("end", "end")
            .Edge("start", "call").Edge("call", "end", Ports.Done)
            .Build());
        await h.WaitForStatusAsync(parent, ProcessStatus.Completed);

        var child = Assert.Single(await h.Store.GetChildProcessesAsync(parent));
        // Секрет вызвавшего процесса в несекретной переменной вызванного — скрыт в названии и не сохранён для повтора.
        Assert.Equal("•••• · p1", child.Title);
        Assert.False(child.Inputs!.ContainsKey("token"));
        Assert.Equal("p1", child.Inputs["profile"]!.GetValue<string>());
    }

    [Fact]
    public async Task Title_and_inputs_survive_a_restart()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Ждёт")
            .Variable("login", VariableType.Text, "vera", isInput: true)
            .Node("start", "start")
            .Node("w", "event.wait", new { source = "manual", eventType = "go" })
            .Node("end", "end")
            .Edge("start", "w").Edge("w", "end", Ports.Event)
            .Build());
        await h.WaitForStatusAsync(id, ProcessStatus.Waiting);
        await h.RestartAsync();

        var process = await h.ProcessAsync(id);
        Assert.Equal("vera", process.Title);
        Assert.Equal("vera", process.Inputs!["login"]!.GetValue<string>());
    }

    [Fact]
    public void Validator_reports_a_broken_title_template()
    {
        var registry = new Core.Nodes.NodeRegistry().RegisterLogicNodes();
        var validator = new Core.Validation.ScenarioValidator(registry, new Core.Expressions.JintExpressionEvaluator());
        var report = validator.Validate(Account().Build() with { RunTitle = "{{ vars.login " });
        Assert.Contains(report.Issues, i => i.Code == "scenario.runTitle");
    }
}
