using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>Секретные переменные: значения не попадают в журнал, ошибки и итоги шагов, но процесс работает с настоящими.</summary>
public sealed class SecretTests
{
    [Fact]
    public async Task Secret_values_are_masked_in_the_journal_errors_and_status_but_the_step_keeps_the_real_value()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("S3cr3t-Pa55", ActionMode.Fail);
        var scenario = new ScenarioBuilder("Вход")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("login", VariableType.Text, isInput: true)
            .Node("start", "start")
            .Node("log", "log", new { message = "вход {{ vars.login }} / {{ vars.password }}" })
            .Node("act", TestActionNode.TypeName, new { key = "{{ vars.password }}" })
            .Node("end", "end", new { message = "готово" })
            .Chain("start", "log", "act", "end").Build();

        var id = await h.RunAsync(scenario, new JsonObject { ["password"] = "S3cr3t-Pa55", ["login"] = "ivan" });
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        Assert.DoesNotContain("S3cr3t-Pa55", stopped.StatusMessage);
        Assert.Contains(ProcessEngine.SecretMask, stopped.StatusMessage);
        var journal = string.Join("\n", (await h.Store.GetJournalAsync(id)).Select(j => j.Message));
        Assert.Contains("вход ivan / ••••", journal);
        Assert.DoesNotContain("S3cr3t-Pa55", journal);
        var steps = await h.StepsAsync(id);
        var failed = steps.Single(s => s.NodeId == "act");
        Assert.DoesNotContain("S3cr3t-Pa55", failed.Error);
        // Параметры шага и переменные — настоящие: по ним процесс продолжается и проверяет результат после перезапуска.
        Assert.Equal("S3cr3t-Pa55", failed.Input!["key"]!.GetValue<string>());
        Assert.Equal("S3cr3t-Pa55", stopped.Variables["password"]!.GetValue<string>());
        await h.Engine.CancelAsync(id);
    }

    [Fact]
    public async Task Longer_secret_is_masked_whole_and_very_short_values_are_left_alone()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder("Коды")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("pin", VariableType.Text, isInput: true, secret: true)
            .Variable("code", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("end", "end", new { message = "{{ vars.password }} {{ vars.pin }} {{ vars.code }}" })
            .Chain("start", "end").Build();

        var id = await h.RunAsync(scenario, new JsonObject { ["password"] = "abc123", ["pin"] = "abc", ["code"] = "7" });
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        // «abc» внутри «abc123» не оставляет хвост «123»; одну цифру не скрыть — она есть в любом тексте.
        Assert.Equal("•••• •••• 7", done.StatusMessage);
    }

    [Fact]
    public async Task Called_scenario_keeps_passed_secrets_secret_and_does_not_return_them()
    {
        await using var h = await EngineHarness.CreateAsync();
        var child = await h.CreateScenarioAsync(new ScenarioBuilder("Ребёнок")
            .Variable("password", VariableType.Text, isInput: true)
            .Node("start", "start")
            .Node("log", "log", new { message = "в ребёнке {{ vars.password }}" })
            .Node("end", "end")
            .Chain("start", "log", "end").Build());
        var parent = await h.RunAsync(new ScenarioBuilder("Родитель")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("result", VariableType.Any)
            .Node("start", "start")
            .Node("call", "scenario.call", new { scenario = child, inputs = "({ password: vars.password })", saveTo = "result" })
            .Node("end", "end")
            .Edge("start", "call").Edge("call", "end", Ports.Done)
            .Build(), new JsonObject { ["password"] = "S3cr3t-Pa55" });
        var done = await h.WaitForStatusAsync(parent, ProcessStatus.Completed);

        // Итог вызванного сценария сохранён в обычную переменную — пароля в нём нет.
        Assert.Equal(ProcessEngine.SecretMask, done.Variables["result"]!["password"]!.GetValue<string>());
        var called = (await h.Store.ListProcessesAsync(new ProcessQuery { Limit = 10 })).Single(p => p.ParentProcessId == parent);
        // Во вызванном сценарии переменная не отмечена секретной, но значение пришло из секретной — оно секретно и там.
        var definition = Core.Serialization.PepaJson.Deserialize<ScenarioDefinition>(called.DefinitionJson);
        Assert.True(definition.Variables.Single(v => v.Name == "password").Secret);
        var journal = string.Join("\n", (await h.Store.GetJournalAsync(called.Id)).Select(j => j.Message));
        Assert.Contains("в ребёнке ••••", journal);
        Assert.Equal(ProcessEngine.SecretMask, new SecretMasker(called, definition).Variables(called.Variables)["password"]!.GetValue<string>());
        Assert.Equal("S3cr3t-Pa55", called.Variables["password"]!.GetValue<string>());
    }

    [Fact]
    public async Task Changed_secret_numbers_and_wrong_type_values_stay_hidden()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder("Смена")
            .Variable("password", VariableType.Text, isInput: true, secret: true)
            .Variable("pin", VariableType.Number, secret: true)
            .Node("start", "start")
            .Node("log", "log", new { message = "вход: {{ vars.password }}" })
            .Node("set", "variable.set", new { variable = "password", value = "'second-Pa55'" })
            .Node("pin", "variable.set", new { variable = "pin", value = "987654" })
            .Node("logPin", "log", new { message = "ПИН {{ vars.pin }}" })
            .Node("wrong", "variable.set", new { variable = "pin", value = "'zz-wrong-pin'" })
            .Node("end", "end")
            .Chain("start", "log", "set", "pin", "logPin", "wrong", "end").Build();
        var id = await h.RunAsync(scenario, new JsonObject { ["password"] = "S3cr3t-Pa55" });
        var stopped = await h.WaitForAsync(id, p => p.Status is ProcessStatus.NeedsAttention or ProcessStatus.Failed);

        // Значение неверного типа — в сообщении об ошибке, но скрыто: оно присваивалось секретной переменной.
        Assert.DoesNotContain("zz-wrong-pin", stopped.StatusMessage);
        var journal = string.Join("\n", (await h.Store.GetJournalAsync(id)).Select(j => j.Message));
        Assert.Contains("вход: ••••", journal);
        Assert.Contains("ПИН ••••", journal);
        Assert.DoesNotContain("987654", journal);
        Assert.DoesNotContain("zz-wrong-pin", journal);

        // При показе: прежнее значение пароля в параметрах шага скрыто, хотя переменная уже другая; число — тоже.
        var masker = new SecretMasker(stopped, scenario);
        var steps = (await h.StepsAsync(id)).Select(masker.Step).ToList();
        Assert.Equal(ProcessEngine.SecretMask, steps.Single(s => s.NodeId == "log").Input!["message"]!.GetValue<string>());
        Assert.Equal(ProcessEngine.SecretMask, steps.Single(s => s.NodeId == "set").Output!["variables"]!["password"]!.GetValue<string>());
        Assert.Equal(ProcessEngine.SecretMask, steps.Single(s => s.NodeId == "pin").Output!["variables"]!["pin"]!.GetValue<string>());
        var shown = string.Join("\n", steps.Select(s => $"{s.Input?.ToJsonString()} {s.Output?.ToJsonString()} {s.Error} {s.Resolution}"));
        Assert.DoesNotContain("S3cr3t-Pa55", shown);
        Assert.DoesNotContain("second-Pa55", shown);
        Assert.DoesNotContain("987654", shown);
        await h.Engine.CancelAsync(id);
    }
}
