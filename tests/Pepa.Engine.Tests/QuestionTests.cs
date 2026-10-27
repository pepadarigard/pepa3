using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Persistence;
using Pepa.Engine.Tests.Infrastructure;

namespace Pepa.Engine.Tests;

/// <summary>«Спросить пользователя»: вопрос ждёт ответа, ответ один, а секретный ответ не попадает в историю.</summary>
public sealed class QuestionTests
{
    private static ScenarioDefinition Ask(object config, bool secretVariable = false, int? timeout = null) =>
        new ScenarioBuilder("Вопрос")
            .Variable("answer", VariableType.Any, secret: secretVariable)
            .Variable("order", VariableType.Text, "A-17")
            .Node("start", "start")
            .Node("ask", "user.ask", config)
            .Node("done", "end", new { message = "ответ {{ vars.answer }}" })
            .Node("late", "end", new { outcome = "failure", message = "нет ответа" })
            .Chain("start", "ask").Edge("ask", "done", Ports.Event).Edge("ask", "late", Ports.Timeout).Build();

    private static async Task<QuestionInfo> WaitQuestionAsync(EngineHarness h, string processId)
    {
        await h.WaitForAsync(processId, p => p.Status == ProcessStatus.Waiting);
        return Assert.Single(await h.Engine.ListQuestionsAsync(processId));
    }

    [Fact]
    public async Task Question_with_options_waits_for_one_answer()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(Ask(new { question = "Оплатить заказ {{ vars.order }}?", options = "Да\nНет\n\nДа", saveTo = "answer" }));
        var question = await WaitQuestionAsync(h, id);

        Assert.Equal("Оплатить заказ A-17?", question.Text);
        Assert.Equal(["Да", "Нет"], question.Options);
        Assert.False(question.FreeText);
        var wrong = await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.AnswerQuestionAsync(question.Subscription.Id, "Может быть"));
        Assert.Contains("один из вариантов", wrong.Message);

        var first = await h.Engine.AnswerQuestionAsync(question.Subscription.Id, " Да ");
        var second = await h.Engine.AnswerQuestionAsync(question.Subscription.Id, "Нет");
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal("ответ Да", done.StatusMessage);
        Assert.Empty(await h.Engine.ListQuestionsAsync());
        Assert.Contains(await h.Store.GetJournalAsync(id), j => j.Message.Contains("Получен ответ: «Да»", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Free_text_answer_and_secret_answer_stays_out_of_history()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(Ask(new { question = "Код из SMS?", secret = true, saveTo = "answer" }));
        var question = await WaitQuestionAsync(h, id);
        Assert.True(question.FreeText);
        Assert.True(question.Secret);

        var empty = await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.AnswerQuestionAsync(question.Subscription.Id, "   "));
        Assert.Contains("пустой", empty.Message);
        await h.Engine.AnswerQuestionAsync(question.Subscription.Id, " 48-21-Qx ");
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);

        // Ответ настоящий (с пробелами — так его ввели), но в истории и на странице события скрыт.
        Assert.Equal(" 48-21-Qx ", done.Variables["answer"]!.GetValue<string>());
        var journal = string.Join("\n", (await h.Store.GetJournalAsync(id)).Select(j => j.Message));
        Assert.DoesNotContain("48-21-Qx", journal);
        Assert.Contains("Получен ответ: «••••»", journal);
        Assert.DoesNotContain("48-21-Qx", done.StatusMessage);
        var answerEvent = Assert.Single(await h.Store.ListEventsAsync(new EventQuery { Limit = 10 }), e => e.Source == EventSources.Question);
        Assert.Equal([" 48-21-Qx "], answerEvent.Secrets);
        // Изменения переменных в шаге хранятся настоящими, а показываются без секрета.
        var masker = new SecretMasker(done, PepaJsonOf(done));
        Assert.DoesNotContain("48-21-Qx", string.Join("\n", (await h.StepsAsync(id)).Select(masker.Step).Select(s => s.Output?.ToJsonString())));
        Assert.DoesNotContain("48-21-Qx", masker.Variables(done.Variables).ToJsonString());
    }

    [Fact]
    public async Task Answer_saved_into_a_secret_variable_is_secret_too()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(Ask(new { question = "Пароль?", saveTo = "answer" }, secretVariable: true));
        var question = await WaitQuestionAsync(h, id);
        Assert.True(question.Secret);
    }

    [Fact]
    public async Task Without_an_answer_the_process_takes_the_timeout_branch_and_a_late_answer_is_refused()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(Ask(new { question = "Подтвердить?", options = "Да", timeoutSeconds = 60, saveTo = "answer" }));
        var question = await WaitQuestionAsync(h, id);

        var late = await h.WaitForAsync(id, p => p.IsTerminal, advance: TimeSpan.FromSeconds(30));
        Assert.Equal(ProcessStatus.Failed, late.Status);
        Assert.Equal("нет ответа", late.StatusMessage);
        var error = await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.AnswerQuestionAsync(question.Subscription.Id, "Да"));
        Assert.Contains("больше не ждёт", error.Message);
    }

    [Fact]
    public async Task Question_survives_a_restart_and_the_answer_is_applied_after_resume()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(Ask(new { question = "Продолжить?", options = "Да\nНет", saveTo = "answer" }));
        await WaitQuestionAsync(h, id);

        await h.RestartAsync();
        var question = Assert.Single(await h.Engine.ListQuestionsAsync(id));
        Assert.Equal(ProcessStatus.Paused, question.Process.Status);
        await h.Engine.AnswerQuestionAsync(question.Subscription.Id, "Нет");
        await h.IdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProcessStatus.Paused, (await h.ProcessAsync(id)).Status);

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        var done = await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Equal("ответ Нет", done.StatusMessage);
    }

    [Fact]
    public async Task Answers_can_not_be_forged_by_scenarios_or_the_events_api()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(Ask(new { question = "Да?", options = "Да", saveTo = "answer" }));
        var question = await WaitQuestionAsync(h, id);

        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.PublishEventAsync(new EventInput
        {
            Source = EventSources.Question, Type = "answer", CorrelationKey = question.Subscription.CorrelationKey,
            Payload = new JsonObject { ["answer"] = "Да" },
        }));
        var forger = await h.RunAsync(new ScenarioBuilder("Подделка")
            .Node("start", "start")
            .Node("send", "event.send", new { source = EventSources.Question, eventType = "answer", payload = "({ answer: 'Да' })" })
            .Node("end", "end")
            .Chain("start", "send", "end").Build());
        var stopped = await h.WaitForStatusAsync(forger, ProcessStatus.NeedsAttention);
        Assert.Contains("служебный", stopped.StatusMessage);
        var listener = await h.RunAsync(new ScenarioBuilder("Перехват")
            .Node("start", "start")
            .Node("wait", "event.wait", new { source = EventSources.Question })
            .Node("end", "end")
            .Chain("start", "wait").Edge("wait", "end", Ports.Event).Build());
        Assert.Contains("служебный", (await h.WaitForStatusAsync(listener, ProcessStatus.NeedsAttention)).StatusMessage);

        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(id)).Status);
        await h.Engine.CancelAsync(id);
    }

    [Fact]
    public async Task Answer_after_the_deadline_is_refused_even_while_the_process_is_paused()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(Ask(new { question = "Подтвердить?", options = "Да", timeoutSeconds = 60, saveTo = "answer" }));
        await WaitQuestionAsync(h, id);

        var question = Assert.Single(await h.Engine.ListQuestionsAsync(id));
        await h.RestartAsync(whileClosed: () => h.Time.Advance(TimeSpan.FromMinutes(5)));
        Assert.Empty(await h.Engine.ListQuestionsAsync(id));
        var error = await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.AnswerQuestionAsync(question.Subscription.Id, "Да"));
        Assert.Contains("Время ответа", error.Message);
        Assert.DoesNotContain(await h.Store.ListEventsAsync(new EventQuery { Limit = 10 }), e => e.Source == EventSources.Question);

        await h.Engine.ResumeAsync(id, ResumeAction.Continue);
        Assert.Equal("нет ответа", (await h.WaitForAsync(id, p => p.IsTerminal, advance: TimeSpan.FromSeconds(1))).StatusMessage);
    }

    [Fact]
    public async Task Answer_goes_only_to_its_own_question()
    {
        await using var h = await EngineHarness.CreateAsync();
        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.SaveTriggerAsync(new TriggerRecord
        {
            Id = "t", ScenarioId = "s", Kind = TriggerKind.Event, Enabled = true, Name = "перехват",
            Config = new JsonObject { ["source"] = EventSources.Question }, CreatedAt = h.Time.GetUtcNow(), UpdatedAt = h.Time.GetUtcNow(),
        }));

        var a = await h.RunAsync(Ask(new { question = "А?", options = "Да\nНет", saveTo = "answer" }));
        var b = await h.RunAsync(Ask(new { question = "Б?", options = "Оплатить\nОтменить", saveTo = "answer" }));
        var questionA = await WaitQuestionAsync(h, a);
        var questionB = await WaitQuestionAsync(h, b);

        // Ответ A принят, пока A на паузе, потом A остановлен: ответ возвращается к сопоставлению и не подходит никому.
        await h.Engine.PauseAsync(a);
        var answer = await h.Engine.AnswerQuestionAsync(questionA.Subscription.Id, "Нет");
        await h.Engine.CancelAsync(a);
        var record = await WaitEventAsync(h, answer.EventId, EventState.Unmatched);
        Assert.Null(record.StartedProcesses);

        // Отдать его вопросу Б вручную нельзя — Б ждёт свой ответ.
        await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.ResolveEventAsync(answer.EventId, questionB.Subscription.Id));
        Assert.Equal(ProcessStatus.Waiting, (await h.ProcessAsync(b)).Status);
        await h.Engine.CancelAsync(b);
    }

    [Fact]
    public async Task Option_filled_from_a_secret_is_a_secret_of_the_answer()
    {
        await using var h = await EngineHarness.CreateAsync();
        var scenario = new ScenarioBuilder("Вопрос")
            .Variable("answer", VariableType.Any)
            .Variable("token", VariableType.Text, isInput: true, secret: true)
            .Node("start", "start")
            .Node("ask", "user.ask", new { question = "Каким ключом?", options = "{{ vars.token }}\nДругим", saveTo = "answer" })
            .Node("end", "end")
            .Chain("start", "ask").Edge("ask", "end", Ports.Event).Build();
        var id = await h.RunAsync(scenario, new JsonObject { ["token"] = "tok-S3cr3t-77" });
        var question = await WaitQuestionAsync(h, id);

        var answer = await h.Engine.AnswerQuestionAsync(question.Subscription.Id, null, option: 0);
        await h.WaitForStatusAsync(id, ProcessStatus.Completed);
        Assert.Contains("tok-S3cr3t-77", (await h.Store.GetEventAsync(answer.EventId))!.Secrets!);
        Assert.DoesNotContain("tok-S3cr3t-77", string.Join("\n", (await h.Store.GetJournalAsync(id)).Select(j => j.Message)));
    }

    [Fact]
    public async Task Answer_is_converted_to_the_type_of_its_variable_or_refused()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Число")
            .Variable("amount", VariableType.Number)
            .Node("start", "start")
            .Node("ask", "user.ask", new { question = "Сколько?", saveTo = "amount" })
            .Node("end", "end", new { message = "сумма {{ vars.amount * 2 }}" })
            .Chain("start", "ask").Edge("ask", "end", Ports.Event).Build());
        var question = await WaitQuestionAsync(h, id);

        var error = await Assert.ThrowsAsync<EngineOperationException>(() => h.Engine.AnswerQuestionAsync(question.Subscription.Id, "много"));
        Assert.Contains("числом", error.Message);
        Assert.Single(await h.Engine.ListQuestionsAsync(id));
        await h.Engine.AnswerQuestionAsync(question.Subscription.Id, "42,5");
        Assert.Equal("сумма 85", (await h.WaitForStatusAsync(id, ProcessStatus.Completed)).StatusMessage);
    }

    [Fact]
    public async Task Options_that_render_empty_stop_the_process_instead_of_allowing_any_answer()
    {
        await using var h = await EngineHarness.CreateAsync();
        var id = await h.RunAsync(new ScenarioBuilder("Пусто")
            .Variable("choices", VariableType.Text)
            .Variable("answer", VariableType.Any)
            .Node("start", "start")
            .Node("ask", "user.ask", new { question = "Что выбрать?", options = "{{ vars.choices }}", saveTo = "answer" })
            .Node("end", "end")
            .Chain("start", "ask").Edge("ask", "end", Ports.Event).Build());
        var stopped = await h.WaitForStatusAsync(id, ProcessStatus.NeedsAttention);
        Assert.Contains("Варианты ответа получились пустыми", stopped.StatusMessage);
        Assert.Empty(await h.Engine.ListQuestionsAsync(id));
    }

    private static async Task<EventRecord> WaitEventAsync(EngineHarness h, long eventId, EventState state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var record = await h.Store.GetEventAsync(eventId);
            if (record?.State == state)
            {
                return record;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Событие в состоянии {record?.State}, ожидалось {state}. {record?.Note}");
            }

            await Task.Delay(20);
        }
    }

    private static ScenarioDefinition PepaJsonOf(ProcessRecord process) =>
        Core.Serialization.PepaJson.Deserialize<ScenarioDefinition>(process.DefinitionJson);
}
