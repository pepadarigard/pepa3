using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Engine.Nodes;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

/// <summary>Вопрос процесса, который ждёт ответа пользователя («Спросить пользователя»).</summary>
public sealed record QuestionInfo(
    SubscriptionRecord Subscription,
    ProcessRecord Process,
    ScenarioDefinition Definition,
    string Text,
    IReadOnlyList<string> Options,
    bool AllowText,
    bool Secret,
    string? Image)
{
    /// <summary>Ответ своим текстом: можно, если разрешено или вариантов нет.</summary>
    public bool FreeText => AllowText || Options.Count == 0;
}

public sealed partial class ProcessEngine
{
    private const int MaxAnswerLength = 10_000;

    /// <summary>Вопросы, которые ждут ответа (у незавершённых процессов), — все или одного процесса.</summary>
    public async Task<IReadOnlyList<QuestionInfo>> ListQuestionsAsync(string? processId = null, CancellationToken cancellationToken = default)
    {
        var result = new List<QuestionInfo>();
        foreach (var subscription in await _store.GetActiveSubscriptionsAsync(EventSources.Question, cancellationToken))
        {
            if (subscription.Purpose != SubscriptionPurpose.Question || (processId is not null && subscription.ProcessId != processId))
            {
                continue;
            }

            if (await QuestionAsync(subscription, cancellationToken) is { } question)
            {
                result.Add(question);
            }
        }

        return result;
    }

    private async Task<QuestionInfo?> QuestionAsync(SubscriptionRecord subscription, CancellationToken cancellationToken)
    {
        var process = await _store.GetProcessAsync(subscription.ProcessId, cancellationToken);
        if (process is null || process.IsTerminal)
        {
            return null;
        }

        var input = (await _store.GetStepAsync(subscription.StepId, cancellationToken))?.Input ?? new JsonObject();
        var definition = Graph(process).Definition;
        var saveTo = definition.Variables.FirstOrDefault(v => v.Name == subscription.SaveTo);
        return new QuestionInfo(subscription, process, definition, input.GetString("question") ?? "",
            AskUserNode.Options(input.GetString("options")), input.GetBoolean("allowText") ?? false,
            (input.GetBoolean("secret") ?? false) || saveTo?.Secret == true,
            input.GetString("image") is { Length: > 0 } image && !string.IsNullOrWhiteSpace(image) ? image.Trim() : null);
    }

    /// <summary>Вопрос по номеру (null — такого нет или процесс его больше не ждёт).</summary>
    public async Task<QuestionInfo?> GetQuestionAsync(long id, CancellationToken cancellationToken = default) =>
        await _store.GetSubscriptionAsync(id, cancellationToken) is { Purpose: SubscriptionPurpose.Question, State: SubscriptionState.Active } subscription
            ? await QuestionAsync(subscription, cancellationToken)
            : null;

    /// <summary>
    /// Ответить на вопрос процесса. Ответ — событие с ключом вопроса: его получит только этот процесс (приостановленный —
    /// после «Продолжить»), а второй ответ на тот же вопрос не создаётся (<see cref="PublishResult.Duplicate"/>).
    /// Секретный ответ — секрет события: в истории и на странице «События» он скрыт.
    /// </summary>
    public async Task<PublishResult> AnswerQuestionAsync(long id, string? answer, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var subscription = await _store.GetSubscriptionAsync(id, cancellationToken);
        if (subscription is not { Purpose: SubscriptionPurpose.Question })
        {
            throw new EngineOperationException("Вопрос не найден.");
        }

        PublishResult result;
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            // Второй ответ на тот же вопрос (двойное нажатие, вторая вкладка) — не ошибка и не новое событие.
            if (await write.FindEventByExternalIdAsync(EventSources.Question, ExternalId(subscription)) is { } earlier)
            {
                return new PublishResult(earlier.Id, true);
            }

            // Состояние — под правом записи: вопрос мог только что закрыться по тайм-ауту.
            var current = await write.GetSubscriptionAsync(id);
            var question = current is { State: SubscriptionState.Active } ? await QuestionAsync(current, cancellationToken) : null;
            if (question is null)
            {
                throw new EngineOperationException("Процесс больше не ждёт ответа на этот вопрос.");
            }

            // Секрет — как есть (пробелы в пароле важны), остальное — без пробелов по краям.
            var value = question.Secret ? answer ?? "" : (answer ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new EngineOperationException("Ответ пустой.");
            }

            if (value.Length > MaxAnswerLength)
            {
                throw new EngineOperationException($"Ответ длиннее {MaxAnswerLength} знаков.");
            }

            if (!question.FreeText && !question.Options.Contains(value, StringComparer.Ordinal))
            {
                throw new EngineOperationException("Выберите один из вариантов ответа.");
            }

            var record = NewEventRecord(new EventInput
            {
                Source = EventSources.Question,
                Type = AskUserNode.AnswerType,
                ExternalId = ExternalId(subscription),
                CorrelationKey = subscription.CorrelationKey,
                Payload = new JsonObject { ["answer"] = value },
            }, _time.GetUtcNow()) with { Secrets = question.Secret ? [value] : null };
            var eventId = await write.TryInsertEventAsync(record);
            result = eventId is null
                ? new PublishResult((await write.FindEventByExternalIdAsync(EventSources.Question, record.ExternalId!))!.Id, true)
                : new PublishResult(eventId.Value, false);
            await write.CommitAsync();
        }

        if (!result.Duplicate)
        {
            SignalRouter();
        }

        EventsChanged?.Invoke();
        return result;
    }

    private static string ExternalId(SubscriptionRecord subscription) => $"answer:{subscription.Id}";
}
