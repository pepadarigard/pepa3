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

    /// <summary>
    /// Вопросы, которые ждут ответа (у незавершённых процессов), — все или одного процесса. Вопрос, срок ответа на который
    /// вышел, не показывается: ответ на него не примут (у приостановленного процесса тайм-аут наступит после «Продолжить»).
    /// </summary>
    public async Task<IReadOnlyList<QuestionInfo>> ListQuestionsAsync(string? processId = null, CancellationToken cancellationToken = default)
    {
        var result = new List<QuestionInfo>();
        var now = _time.GetUtcNow();
        foreach (var subscription in await _store.GetActiveSubscriptionsAsync(EventSources.Question, cancellationToken))
        {
            if (subscription.Purpose != SubscriptionPurpose.Question || (processId is not null && subscription.ProcessId != processId) ||
                subscription.ExpiresAt <= now)
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
    /// Ответить на вопрос процесса — текстом или номером варианта. Ответ сразу отдаётся своему вопросу (под правом записи,
    /// без общего сопоставления событий): его не получит ни другой процесс, ни автозапуск, а после срока ответа он не
    /// принимается. Приостановленный процесс применит ответ после «Продолжить». Второй ответ на тот же вопрос не создаётся
    /// (<see cref="PublishResult.Duplicate"/>). Секретный ответ и секреты в вариантах — секреты события.
    /// </summary>
    public async Task<PublishResult> AnswerQuestionAsync(long id, string? answer, int? option = null, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var subscription = await _store.GetSubscriptionAsync(id, cancellationToken);
        if (subscription is not { Purpose: SubscriptionPurpose.Question })
        {
            throw new EngineOperationException("Вопрос не найден.");
        }

        PublishResult result;
        ProcessRecord process;
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            // Второй ответ на тот же вопрос (двойное нажатие, вторая вкладка) — не ошибка и не новое событие.
            if (await write.FindEventByExternalIdAsync(EventSources.Question, ExternalId(subscription)) is { } earlier)
            {
                return new PublishResult(earlier.Id, true);
            }

            // Состояние и срок — под правом записи: вопрос мог только что закрыться по тайм-ауту. Срок проверяется и у
            // приостановленного процесса (его тайм-аут наступит после «Продолжить»).
            var now = _time.GetUtcNow();
            var current = await write.GetSubscriptionAsync(id);
            var question = current is { State: SubscriptionState.Active } && (current.ExpiresAt is not { } expires || now < expires)
                ? await QuestionAsync(current, cancellationToken)
                : null;
            if (question is null)
            {
                throw new EngineOperationException(current is { State: SubscriptionState.Active }
                    ? "Время ответа на этот вопрос вышло."
                    : "Процесс больше не ждёт ответа на этот вопрос.");
            }

            if (option is { } index)
            {
                answer = index >= 0 && index < question.Options.Count
                    ? question.Options[index]
                    : throw new EngineOperationException("Такого варианта ответа нет.");
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

            var converted = Converted(value, question);
            var payload = new JsonObject { ["answer"] = converted };
            var secrets = SecretsIn(RawSecretValues(question.Process, question.Definition), payload);
            if (question.Secret)
            {
                // И как введён, и как сохранится (число без пробелов, «да» как true).
                secrets.Add(value);
                secrets.Add(converted.AsString() ?? converted.ToJsonString());
            }

            process = question.Process;
            var record = NewEventRecord(new EventInput
            {
                Source = EventSources.Question,
                Type = AskUserNode.AnswerType,
                ExternalId = ExternalId(subscription),
                CorrelationKey = subscription.CorrelationKey,
                Payload = payload,
            }, now) with
            {
                State = EventState.Matched,
                ProcessId = process.Id,
                SubscriptionId = subscription.Id,
                Note = process.Status is ProcessStatus.Paused or ProcessStatus.NeedsAttention
                    ? "Ответ на вопрос; будет применён после возобновления процесса."
                    : null,
                HandledAt = now,
                Secrets = secrets.Count > 0 ? secrets.Distinct(StringComparer.Ordinal).ToList() : null,
            };
            var eventId = await write.TryInsertEventAsync(record)
                          ?? throw new EngineOperationException("На этот вопрос уже ответили.");
            if (!await write.TrySetSubscriptionStateAsync(subscription.Id, SubscriptionState.Active, SubscriptionState.Fulfilled, eventId))
            {
                throw new EngineOperationException("Процесс больше не ждёт ответа на этот вопрос.");
            }

            await write.CommitAsync();
            result = new PublishResult(eventId, false);
        }

        Enqueue(process.Id);
        Notify(process.Id);
        EventsChanged?.Invoke();
        return result;
    }

    /// <summary>
    /// Ответ — в тип переменной, куда он сохраняется (см. <see cref="AskUserNode.TryConvert"/>). Иначе ответ не принимается:
    /// принятый, он бы не поместился в переменную, а вопрос был бы уже закрыт.
    /// </summary>
    private static JsonNode Converted(string value, QuestionInfo question)
    {
        var type = question.Definition.Variables.FirstOrDefault(v => v.Name == question.Subscription.SaveTo)?.Type ?? VariableType.Any;
        return AskUserNode.TryConvert(value, type, out var converted, out var problem)
            ? converted
            : throw new EngineOperationException(problem);
    }

    private static string ExternalId(SubscriptionRecord subscription) => $"answer:{subscription.Id}";
}
