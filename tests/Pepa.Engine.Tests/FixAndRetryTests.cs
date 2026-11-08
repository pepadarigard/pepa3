using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>«Исправить и повторить» (D-085): ошибка во входных данных исправляется, а уже сделанное не повторяется.</summary>
public sealed class FixAndRetryTests
{
    private const string Action = TestActionNode.TypeName;

    private static ScenarioDefinition SiteThenSend() => new ScenarioBuilder("Сайт и сообщение")
        .Variable("chat", VariableType.Text, "@ivn", isInput: true)
        .Variable("count", VariableType.Number, 1)
        .Variable("password", VariableType.Text, "old-secret-1", secret: true)
        .Node("start", "start")
        .Node("site", Action, new { key = "site" })
        .Node("send", Action, new { key = "send-{{ vars.chat }}" })
        .Node("end", "end", new { message = "отправлено в {{ vars.chat }}" })
        .Chain("start", "site", "send", "end")
        .Build();

    [Fact]
    public async Task Fixed_input_is_used_by_the_same_step_and_earlier_work_is_not_repeated()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("send-@ivn", ActionMode.Fail);
        var id = await h.RunAsync(SiteThenSend());
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, stopped.Attention);
        Assert.Contains(ProcessAction.RetryWithChanges, h.Engine.GetAllowedActions(stopped, await h.Store.GetTokensAsync(id)));

        var result = await h.Engine.ResumeAsync(id, ResumeAction.RetryWithChanges,
            new JsonObject { ["chat"] = "@ivan", ["password"] = "new-secret-2" });
        Assert.StartsWith("Переменные исправлены (2).", result.Message);
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("отправлено в @ivan", done.StatusMessage);
        Assert.Equal(1, h.World.Executions("site"));
        Assert.Equal(1, h.World.EffectCount("send-@ivan"));

        var journal = await h.Store.GetJournalAsync(id);
        Assert.Contains(journal, j => j.Message == "Пользователь исправил переменную «chat»: «@ivn» → «@ivan».");
        Assert.Contains(journal, j => j.Message == "Пользователь исправил секретную переменную «password» (значение не показывается).");
        Assert.All(journal, j => Assert.DoesNotContain("secret", j.Message, StringComparison.Ordinal));
        // Прежний пароль остаётся секретом процесса: он скрыт там, где мог сохраниться.
        Assert.Contains("old-secret-1", done.Secrets!.All);
    }

    [Fact]
    public async Task Fix_is_refused_for_an_unknown_outcome_and_for_wrong_values()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("send-@ivn", ActionMode.ThrowAfterEffect);
        h.World.ForcedVerification["send-@ivn"] = VerificationOutcome.Inconclusive;
        var id = await h.RunAsync(SiteThenSend());
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.UnknownStep, stopped.Attention);
        // Неизвестно, ушло ли сообщение: повторять с другими данными нельзя — это могло бы отправить второе.
        Assert.DoesNotContain(ProcessAction.RetryWithChanges, h.Engine.GetAllowedActions(stopped, await h.Store.GetTokensAsync(id)));
        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h.Engine.ResumeAsync(id, ResumeAction.RetryWithChanges, new JsonObject { ["chat"] = "@ivan" }));

        await using var h2 = await EngineHarness.CreateAsync();
        h2.World.Plan("send-@ivn", ActionMode.Fail);
        var failed = await h2.RunAsync(SiteThenSend());
        await h2.WaitForStatusAsync(failed, ProcessStatus.NeedsAttention);
        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h2.Engine.ResumeAsync(failed, ResumeAction.RetryWithChanges, new JsonObject { ["count"] = "много" }));
        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h2.Engine.ResumeAsync(failed, ResumeAction.RetryWithChanges, new JsonObject { ["nope"] = 1 }));
        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h2.Engine.ResumeAsync(failed, ResumeAction.RetryWithChanges, new JsonObject { ["chat"] = "@ivn" }));
        Assert.Equal(ProcessStatus.NeedsAttention, (await h2.ProcessAsync(failed)).Status);
        Assert.Equal("@ivn", (await h2.ProcessAsync(failed)).Variables["chat"]!.GetValue<string>());
    }

    [Fact]
    public async Task Fixed_retry_is_a_new_visit_of_the_step_and_variables_are_refused_for_other_actions()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("send-@ivn", ActionMode.Fail);
        var id = await h.RunAsync(SiteThenSend());
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h.Engine.ResumeAsync(id, ResumeAction.Retry, new JsonObject { ["chat"] = "@ivan" }));

        await h.Engine.ResumeAsync(id, ResumeAction.RetryWithChanges, new JsonObject { ["chat"] = "@ivan" });
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        // Первая попытка с новыми данными: то, что блок берёт из прошлой попытки (ключ от повтора), к ней не относится.
        var sends = (await h.StepsAsync(id)).Where(s => s.NodeId == "send").ToList();
        Assert.Equal([1, 1], sends.Select(s => s.Attempt));
    }

    [Fact]
    public async Task Not_offered_when_the_action_ran_and_there_is_nowhere_to_go()
    {
        await using var h = await EngineHarness.CreateAsync();
        var childId = await h.CreateScenarioAsync(new ScenarioBuilder("Дочерний")
            .Variable("profile", VariableType.Text, "", isInput: true)
            .Node("start", "start")
            .Node("end", "end", new { outcome = "failure", message = "не вышло" })
            .Chain("start", "end")
            .Build());
        var parent = await h.RunAsync(new ScenarioBuilder("Родитель")
            .Variable("profile", VariableType.Text, "p1")
            .Node("start", "start")
            .Node("call", "scenario.call", new { scenario = childId, inputs = "({ profile: vars.profile })" })
            .Node("end", "end")
            .Edge("start", "call").Edge("call", "end", Ports.Done)
            .Build());
        var stopped = await h.WaitForStatusAsync(parent, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, stopped.Attention);
        Assert.Contains("Вызванный сценарий завершился с ошибкой", stopped.StatusMessage);
        // Вызванный сценарий работал: «с исправлениями» его шаги повторились бы.
        var allowed = h.Engine.GetAllowedActions(stopped, await h.Store.GetTokensAsync(parent));
        Assert.Contains(ProcessAction.Retry, allowed);
        Assert.DoesNotContain(ProcessAction.RetryWithChanges, allowed);
        await Assert.ThrowsAsync<EngineOperationException>(() =>
            h.Engine.ResumeAsync(parent, ResumeAction.RetryWithChanges, new JsonObject { ["profile"] = "p2" }));
    }

    private static ScenarioDefinition Credentials() => new ScenarioBuilder("Учётка")
        .Variable("password", VariableType.Text, "Qw9z-pass-old", secret: true)
        .Variable("creds", VariableType.Any)
        .Variable("note", VariableType.Text, "")
        .Node("start", "start")
        .Node("set", "variable.set", new { variable = "creds", value = "({ login: 'anna', password: vars.password })" })
        .Node("send", Action, new { key = "login-{{ vars.creds.login }}" })
        .Node("end", "end")
        .Chain("start", "set", "send", "end")
        .Build();

    [Fact]
    public async Task Value_that_holds_a_secret_is_not_taken_back_with_the_mask_and_its_new_value_stays_secret()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("login-anna", ActionMode.Fail);
        var id = await h.RunAsync(Credentials());
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        // Так значение видно в окне: пароль скрыт. Вернуть его маской значило бы записать «••••» вместо пароля.
        var masked = new JsonObject { ["login"] = "anna.k", ["password"] = ProcessEngine.SecretMask };
        var refused = await Assert.ThrowsAsync<EngineOperationException>(() =>
            h.Engine.ResumeAsync(id, ResumeAction.RetryWithChanges, new JsonObject { ["creds"] = masked }));
        Assert.Contains("Введите значение целиком", refused.Message);
        Assert.Equal("Qw9z-pass-old", (await h.ProcessAsync(id)).Variables["creds"]!["password"]!.GetValue<string>());

        await h.Engine.ResumeAsync(id, ResumeAction.RetryWithChanges,
            new JsonObject { ["creds"] = new JsonObject { ["login"] = "anna.k", ["password"] = "Zx8y-pass-new" } });
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        var journal = await h.Store.GetJournalAsync(id);
        Assert.Contains(journal, j => j.Message == "Пользователь исправил переменную «creds» (значение не показывается: в нём был секрет).");
        Assert.All(journal, j => Assert.DoesNotContain("pass-", j.Message, StringComparison.Ordinal));
        Assert.Contains("Zx8y-pass-new", done.Secrets!.All);
        Assert.Contains("Qw9z-pass-old", done.Secrets.All);
    }

    [Fact]
    public async Task Journal_hides_a_secret_entered_in_the_same_fix_and_a_secret_the_cut_would_split()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("login-anna", ActionMode.Fail);
        var id = await h.RunAsync(Credentials());
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        // Новый пароль и заметка с ним же — в одном исправлении; второй секрет (старый пароль) — на месте обрезки.
        var note = "Server=db;Password=N3w-Pa55word;" + new string('x', 82) + "Qw9z-pass-old";
        await h.Engine.ResumeAsync(id, ResumeAction.RetryWithChanges,
            new JsonObject { ["password"] = "N3w-Pa55word", ["note"] = note });
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        var fixedNote = Assert.Single(await h.Store.GetJournalAsync(id), j => j.Message.StartsWith("Пользователь исправил переменную «note»", StringComparison.Ordinal));
        Assert.DoesNotContain("N3w-Pa55word", fixedNote.Message);
        Assert.DoesNotContain("Qw9z", fixedNote.Message);
        Assert.Contains(ProcessEngine.SecretMask, fixedNote.Message);
    }

    [Fact]
    public async Task Block_fixed_in_the_editor_is_taken_by_the_stopped_process_and_earlier_work_is_not_repeated()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("send-@ivn", ActionMode.Fail);
        var definition = SiteThenSend();
        var scenarioId = await h.CreateScenarioAsync(definition);
        var id = await h.Engine.StartProcessAsync(scenarioId);
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains(ProcessAction.RetryWithNewBlock, h.Engine.GetAllowedActions(stopped, await h.Store.GetTokensAsync(id)));

        // Блок ещё не исправлен — отказ с подсказкой, процесс ждёт как был.
        var same = await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.ResumeAsync(id, ResumeAction.RetryWithNewBlock));
        Assert.Contains("такой же, как в процессе", same.Message);

        // В конструкторе исправили сам блок (адрес отправки), а не данные.
        var stored = (await h.Store.GetScenarioAsync(scenarioId))!;
        var fixedDefinition = definition with
        {
            Nodes = [.. definition.Nodes.Select(n => n.Id == "send"
                ? n with { Config = new JsonObject { ["key"] = "send-@ivan" }, Position = new NodePosition(500, 500) }
                : n)],
        };
        await h.Scenarios.UpdateAsync(scenarioId, fixedDefinition, stored.Version);

        var result = await h.Engine.ResumeAsync(id, ResumeAction.RetryWithNewBlock);
        Assert.StartsWith("Блок взят из сохранённого сценария.", result.Message);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal(1, h.World.Executions("site"));
        Assert.Equal(1, h.World.EffectCount("send-@ivan"));
        var journal = await h.Store.GetJournalAsync(id);
        Assert.Contains(journal, j => j.Message.StartsWith("Блок «", StringComparison.Ordinal) &&
                                      j.Message.Contains("взят из сохранённого сценария (версия 2)", StringComparison.Ordinal) &&
                                      j.Message.Contains("«send-{{ vars.chat }}» → «send-@ivan»", StringComparison.Ordinal));
        // Процесс помнит схему с новым блоком (и после перезапуска), но место блока на схеме — прежнее.
        await h.RestartAsync();
        var process = await h.ProcessAsync(id);
        var node = Core.Serialization.PepaJson.Deserialize<ScenarioDefinition>(process.DefinitionJson).Nodes.Single(n => n.Id == "send");
        Assert.Equal("send-@ivan", node.Config["key"]!.GetValue<string>());
        Assert.NotEqual(new NodePosition(500, 500), node.Position);
    }

    [Fact]
    public async Task Block_is_not_taken_when_it_was_removed_replaced_or_is_invalid()
    {
        await using var h = await EngineHarness.CreateAsync();
        h.World.Plan("send-@ivn", ActionMode.Fail);
        var definition = SiteThenSend();
        var scenarioId = await h.CreateScenarioAsync(definition);
        var id = await h.Engine.StartProcessAsync(scenarioId);
        await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);

        async Task<string> RefusedWith(ScenarioDefinition saved)
        {
            var stored = (await h.Store.GetScenarioAsync(scenarioId))!;
            await using (var write = await h.Store.BeginWriteAsync())
            {
                // Прямо в хранилище: сохранение через конструктор не пропустило бы неверный сценарий.
                await write.SaveScenarioAsync(stored with { DefinitionJson = Core.Serialization.PepaJson.Serialize(saved), Version = stored.Version + 1 },
                    stored.Version);
                await write.CommitAsync();
            }

            return (await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.ResumeAsync(id, ResumeAction.RetryWithNewBlock))).Message;
        }

        var withoutBlock = definition with
        {
            Nodes = [.. definition.Nodes.Where(n => n.Id != "send")],
            Edges = [.. definition.Edges.Where(e => e.Source != "send" && e.Target != "send")],
        };
        Assert.Contains("нет блока", await RefusedWith(withoutBlock));
        Assert.Contains("другого типа", await RefusedWith(definition with
        {
            Nodes = [.. definition.Nodes.Select(n => n.Id == "send" ? n with { Type = "wait", Config = new JsonObject { ["amount"] = 1 } } : n)],
        }));
        Assert.Contains("с ошибкой", await RefusedWith(definition with
        {
            Nodes = [.. definition.Nodes.Select(n => n.Id == "send" ? n with { Config = new JsonObject { ["key"] = "{{ vars.chat " } } : n)],
        }));
        var process = await h.ProcessAsync(id);
        Assert.Equal(ProcessStatus.NeedsAttention, process.Status);
        Assert.Contains("send-{{ vars.chat }}", process.DefinitionJson);
    }

    [Fact]
    public async Task Not_offered_when_the_called_scenario_completed_but_its_result_did_not_fit_the_variable()
    {
        await using var h = await EngineHarness.CreateAsync();
        var childId = await h.CreateScenarioAsync(new ScenarioBuilder("Дочерний")
            .Variable("profile", VariableType.Text, "", isInput: true)
            .Node("start", "start")
            .Node("act", Action, new { key = "child-act" })
            .Node("end", "end")
            .Chain("start", "act", "end")
            .Build());
        var parent = await h.RunAsync(new ScenarioBuilder("Родитель")
            .Variable("profile", VariableType.Text, "p1")
            .Variable("result", VariableType.Text, "")
            .Node("start", "start")
            .Node("call", "scenario.call", new { scenario = childId, inputs = "({ profile: vars.profile })", saveTo = "result" })
            .Node("end", "end")
            .Edge("start", "call").Edge("call", "end", Ports.Done)
            .Build());

        // Итог вызванного сценария — объект, а переменная — текст: шаг не принят, но вызванный уже всё сделал.
        var stopped = await h.WaitForStatusAsync(parent, ProcessStatus.NeedsAttention);
        Assert.Equal(AttentionKind.StepFailed, stopped.Attention);
        Assert.Equal(1, h.World.EffectCount("child-act"));
        var allowed = h.Engine.GetAllowedActions(stopped, await h.Store.GetTokensAsync(parent));
        Assert.DoesNotContain(ProcessAction.RetryWithChanges, allowed);
        Assert.DoesNotContain(ProcessAction.RetryWithNewBlock, allowed);
    }
}
