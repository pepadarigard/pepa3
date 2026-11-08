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
}
