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
}
