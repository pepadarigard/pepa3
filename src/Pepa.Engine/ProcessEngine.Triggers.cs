using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pepa.Core;
using Pepa.Core.Expressions;
using Pepa.Engine.Persistence;
using Pepa.Engine.Scheduling;

namespace Pepa.Engine;

/// <summary>
/// Автоматический запуск: по расписанию и по событиям. Запуски, пропущенные, пока Pepa была закрыта (или сильно опоздавшие),
/// не выполняются сами — они учитываются, и пользователь может запустить их вручную.
/// Любое решение о запуске принимается в той же транзакции, что и запись процесса, по только что перечитанному триггеру:
/// удалённый, выключенный или изменённый пользователем триггер ничего не запустит и не будет перезаписан старыми данными.
/// </summary>
public sealed partial class ProcessEngine
{
    private async Task TriggerLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunDueSchedulesAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка расписаний.");
            }

            try
            {
                await Task.Delay(_options.TimerInterval, _time, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunDueSchedulesAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var due = (await _store.ListTriggersAsync(null, cancellationToken))
            .Where(t => t is { Kind: TriggerKind.Schedule, Enabled: true, NextRunAt: { } next } && next <= now)
            .ToList();
        foreach (var trigger in due)
        {
            await RunScheduledOccurrenceAsync(trigger, cancellationToken);
        }
    }

    /// <summary>Наступил срок одного запуска по расписанию.</summary>
    private async Task RunScheduledOccurrenceAsync(TriggerRecord seen, CancellationToken cancellationToken)
    {
        var occurrence = seen.NextRunAt!.Value;
        var now = _time.GetUtcNow();
        var late = now - occurrence > _options.ScheduleLateGrace;

        PreparedStart? prepared = null;
        string? error = null;
        if (!late)
        {
            try
            {
                prepared = await PrepareStartAsync(seen.ScenarioId, seen.Inputs?.DeepClone() as JsonObject, cancellationToken);
            }
            catch (Exception ex) when (ex is EngineOperationException or ScenarioInvalidException)
            {
                error = ex.Message;
            }
        }

        ProcessRecord? started = null;
        TriggerRecord? failed = null;
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            var current = await write.GetTriggerAsync(seen.Id);
            if (current is not { Kind: TriggerKind.Schedule, Enabled: true } || current.NextRunAt != occurrence ||
                current.UpdatedAt != seen.UpdatedAt)
            {
                return; // триггер удалён, выключен или изменён — решение примет следующий проход по свежим данным
            }

            var updated = current with
            {
                NextRunAt = ScheduleCalculator.Next(current.Config, now, _time.LocalTimeZone),
                UpdatedAt = now,
            };

            if (late)
            {
                updated = updated with
                {
                    MissedRuns = current.MissedRuns + 1 + ScheduleCalculator.CountBetween(current.Config, occurrence, now, _time.LocalTimeZone),
                    LastMissedAt = occurrence,
                };
            }
            else if (current.Overlap == "skip" && current.LastProcessId is { } lastId &&
                     await write.GetProcessAsync(lastId) is { IsTerminal: false })
            {
                updated = updated with { SkippedRuns = current.SkippedRuns + 1 };
            }
            else if (prepared is null)
            {
                updated = updated with { LastError = error, LastRunAt = now };
                failed = current.LastError == error ? null : updated;
            }
            else
            {
                var (process, created) = await InsertStartedProcessAsync(write, prepared, $"schedule:{current.Id}:{occurrence:O}",
                    $"по расписанию: {ScheduleCalculator.Describe(current.Config)}");
                started = created ? process : null;
                updated = updated with { LastRunAt = now, LastProcessId = process.Id, LastError = null };
            }

            if (updated.NextRunAt is null)
            {
                updated = updated with { Enabled = false };
            }

            await write.SaveTriggerAsync(updated);
            await write.CommitAsync();
        }

        if (started is not null)
        {
            Notify(started.Id);
            Enqueue(started.Id);
        }

        TriggersChanged?.Invoke();
        if (failed is not null)
        {
            await RaiseTriggerFailedAsync(failed, failed.LastError!);
        }
    }

    /// <summary>Изменились триггеры или их состояние.</summary>
    public event Action? TriggersChanged;

    /// <summary>
    /// Автозапуск не смог запустить процесс (сценарий с ошибками, удалён, неверные входные данные). Сообщается, когда ошибка
    /// появилась или сменилась, — не на каждом запуске с той же ошибкой.
    /// </summary>
    public event Action<TriggerFailure>? TriggerFailed;

    private async Task RaiseTriggerFailedAsync(TriggerRecord trigger, string error)
    {
        string scenario;
        try
        {
            scenario = (await _store.GetScenarioAsync(trigger.ScenarioId))?.Name ?? "удалённый сценарий";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать сценарий автозапуска {Trigger}.", trigger.Id);
            scenario = "сценарий";
        }

        _logger.LogWarning("Автозапуск «{Trigger}» сценария «{Scenario}» не сработал: {Error}", TriggerTitle(trigger), scenario, error);
        try
        {
            TriggerFailed?.Invoke(new TriggerFailure(trigger.Id, TriggerTitle(trigger), scenario, trigger.Kind, error));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Обработчик сбоя автозапуска завершился с ошибкой.");
        }
    }

    /// <summary>Как назвать автозапуск человеку: его имя или что он делает.</summary>
    internal static string TriggerTitle(TriggerRecord trigger) => trigger.Name is { Length: > 0 } name
        ? name
        : trigger.Kind == TriggerKind.Schedule
            ? $"по расписанию: {ScheduleCalculator.Describe(trigger.Config)}"
            : $"по событию «{trigger.Config.GetString("source")}»";

    /// <summary>Для расписаний, чей срок прошёл, пока Pepa была закрыта: учесть пропуск и назначить следующий запуск.</summary>
    private async Task<int> RecordMissedSchedulesAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var missedTotal = 0;
        foreach (var seen in await _store.ListTriggersAsync(null, cancellationToken))
        {
            if (seen is not { Kind: TriggerKind.Schedule, Enabled: true, NextRunAt: { } next } || next > now)
            {
                continue;
            }

            await using var write = await _store.BeginWriteAsync(cancellationToken);
            var current = await write.GetTriggerAsync(seen.Id);
            if (current is not { Kind: TriggerKind.Schedule, Enabled: true, NextRunAt: { } due } || due > now)
            {
                continue;
            }

            var missed = 1 + ScheduleCalculator.CountBetween(current.Config, due, now, _time.LocalTimeZone);
            missedTotal += missed;
            var nextRun = ScheduleCalculator.Next(current.Config, now, _time.LocalTimeZone);
            await write.SaveTriggerAsync(current with
            {
                MissedRuns = current.MissedRuns + missed,
                LastMissedAt = due,
                NextRunAt = nextRun,
                Enabled = nextRun is not null,
                UpdatedAt = now,
            });
            await write.CommitAsync();
        }

        return missedTotal;
    }

    /// <summary>
    /// Запуск процессов триггерами по событию, которое никто не ждал. Вызывается под замком сопоставления. Failures — автозапуски,
    /// которым событие подошло, но процесс не запустился (««имя»: причина», без секретов события).
    /// </summary>
    private async Task<(List<string> Started, List<string> Failures)> StartByEventTriggersAsync(EventRecord record, CancellationToken cancellationToken)
    {
        var started = new List<string>();
        var failures = new List<string>();
        // Ответ на вопрос (даже не доставленный) — только своему вопросу, никаких запусков по нему.
        if (IsQuestionSource(record.Source))
        {
            return (started, failures);
        }

        var secrets = Expand(record.Secrets);
        var failed = new List<TriggerRecord>();

        var triggers = (await _store.ListTriggersAsync(null, cancellationToken))
            .Where(t => t is { Kind: TriggerKind.Event, Enabled: true });
        var eventScope = new ExpressionScope(new JsonObject(), Extra: new JsonObject { ["event"] = EventJson(record) });
        foreach (var trigger in triggers)
        {
            var source = trigger.Config.GetString("source");
            var type = trigger.Config.GetString("eventType");
            var key = trigger.Config.GetString("correlationKey");
            if (!string.Equals(source?.Trim(), record.Source, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, record.Type, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(key) && !string.Equals(key, record.CorrelationKey, StringComparison.Ordinal)))
            {
                continue;
            }

            string? error = null;
            PreparedStart? prepared = null;
            if (trigger.Config.GetString("filter") is { Length: > 0 } filter)
            {
                bool matches;
                try
                {
                    matches = _expressions.EvaluateCondition(filter, eventScope);
                }
                catch (ExpressionException ex)
                {
                    // Подходит ли событие, неизвестно — это не «подошло, но не запустилось».
                    matches = false;
                    error = $"условие автозапуска не вычислилось — подходит ли событие, неизвестно: {Redact(ex.Message, secrets)}";
                }

                if (!matches && error is null)
                {
                    continue;
                }
            }

            if (error is null)
            {
                try
                {
                    var inputs = new JsonObject();
                    foreach (var (name, expression) in trigger.Inputs ?? [])
                    {
                        if (expression.AsString() is { Length: > 0 } code)
                        {
                            inputs[name] = _expressions.Evaluate(code, eventScope);
                        }
                    }

                    prepared = await PrepareStartAsync(trigger.ScenarioId, inputs, cancellationToken);
                    // Секреты из события секретны и в запущенном процессе.
                    prepared = prepared with { Secrets = record.Secrets };
                }
                catch (Exception ex) when (ex is ExpressionException or EngineOperationException or ScenarioInvalidException)
                {
                    // Текст ошибки может содержать данные события — их секреты скрыты.
                    error = Redact(ex.Message, secrets);
                }
            }

            ProcessRecord? created = null;
            await using (var write = await _store.BeginWriteAsync(cancellationToken))
            {
                var current = await write.GetTriggerAsync(trigger.Id);
                if (current is not { Kind: TriggerKind.Event, Enabled: true } || current.UpdatedAt != trigger.UpdatedAt)
                {
                    continue; // триггер удалён, выключен или изменён, пока событие обрабатывалось
                }

                var now = _time.GetUtcNow();
                if (prepared is null)
                {
                    await write.SaveTriggerAsync(current with { LastError = error, UpdatedAt = now });
                    failures.Add($"«{TriggerTitle(current)}»: {error}");
                    if (current.LastError != error)
                    {
                        failed.Add(current with { LastError = error });
                    }
                }
                else
                {
                    var (process, isNew) = await InsertStartedProcessAsync(write, prepared, $"event:{current.Id}:{record.Id}",
                        $"по событию #{record.Id} «{record.Source}»");
                    created = isNew ? process : null;
                    started.Add(process.Id);
                    await write.SaveTriggerAsync(current with
                    {
                        LastRunAt = now, LastProcessId = process.Id, LastError = null, UpdatedAt = now,
                    });
                }

                await write.CommitAsync();
            }

            if (created is not null)
            {
                Notify(created.Id);
                Enqueue(created.Id);
            }
        }

        TriggersChanged?.Invoke();
        foreach (var trigger in failed)
        {
            await RaiseTriggerFailedAsync(trigger, trigger.LastError!);
        }

        return (started, failures);
    }

    // ───────────────────────────── Управление триггерами ─────────────────────────────

    /// <summary>Создать или изменить триггер. Расписание проверяется и пересчитывается.</summary>
    public async Task<TriggerRecord> SaveTriggerAsync(TriggerRecord trigger, CancellationToken cancellationToken = default)
    {
        if (await _store.GetScenarioAsync(trigger.ScenarioId, cancellationToken) is null)
        {
            throw new EngineOperationException("Сценарий не найден.");
        }

        var now = _time.GetUtcNow();
        if (trigger.Kind == TriggerKind.Schedule)
        {
            if (ScheduleCalculator.Validate(trigger.Config) is { } problem)
            {
                throw new EngineOperationException(problem);
            }

            if (trigger.Enabled && ScheduleCalculator.Next(trigger.Config, now, _time.LocalTimeZone) is null)
            {
                throw new EngineOperationException("По этому расписанию запусков больше не будет (время уже прошло).");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(trigger.Config.GetString("source")))
            {
                throw new EngineOperationException("Укажите источник события.");
            }

            if (IsQuestionSource(trigger.Config.GetString("source")))
            {
                throw new EngineOperationException($"Источник «{EventSources.Question}» служебный: ответ на вопрос получает только тот, кто спросил.");
            }

            if (trigger.Config.GetString("filter") is { Length: > 0 } filter && _expressions.CheckExpression(filter) is { } error)
            {
                throw new EngineOperationException($"Условие: {error}");
            }

            foreach (var (name, value) in trigger.Inputs ?? [])
            {
                if (value.AsString() is { Length: > 0 } code && _expressions.CheckExpression(code) is { } inputError)
                {
                    throw new EngineOperationException($"Значение «{name}»: {inputError}");
                }
            }
        }

        TriggerRecord saved;
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            TriggerRecord? existing = null;
            if (!string.IsNullOrEmpty(trigger.Id))
            {
                existing = await write.GetTriggerAsync(trigger.Id)
                           ?? throw new EngineOperationException("Запуск по расписанию или событию не найден (возможно, его удалили).");
            }

            saved = trigger with
            {
                Id = existing?.Id ?? NewId(),
                Name = trigger.Name.Trim(),
                Overlap = trigger.Overlap is "allow" ? "allow" : "skip",
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now,
                LastRunAt = existing?.LastRunAt,
                LastProcessId = existing?.LastProcessId,
                MissedRuns = existing?.MissedRuns ?? 0,
                LastMissedAt = existing?.LastMissedAt,
                SkippedRuns = existing?.SkippedRuns ?? 0,
                LastError = null,
                NextRunAt = trigger.Kind == TriggerKind.Schedule && trigger.Enabled
                    ? ScheduleCalculator.Next(trigger.Config, now, _time.LocalTimeZone)
                    : null,
            };
            await write.SaveTriggerAsync(saved);
            await write.CommitAsync();
        }

        TriggersChanged?.Invoke();
        return saved;
    }

    public async Task<bool> DeleteTriggerAsync(string id, CancellationToken cancellationToken = default)
    {
        bool deleted;
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            deleted = await write.DeleteTriggerAsync(id);
            await write.CommitAsync();
        }

        TriggersChanged?.Invoke();
        return deleted;
    }

    /// <summary>Запустить триггер вручную (например, после пропущенных запусков) и сбросить счётчик пропусков.</summary>
    public async Task<string> RunTriggerNowAsync(string id, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var seen = await _store.GetTriggerAsync(id, cancellationToken) ?? throw new EngineOperationException("Триггер не найден.");
        if (seen.Kind != TriggerKind.Schedule)
        {
            throw new EngineOperationException("Вручную запускаются только расписания.");
        }

        var prepared = await PrepareStartAsync(seen.ScenarioId, seen.Inputs?.DeepClone() as JsonObject, cancellationToken);
        ProcessRecord process;
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            var current = await write.GetTriggerAsync(id) ?? throw new EngineOperationException("Триггер не найден (возможно, его удалили).");
            (process, _) = await InsertStartedProcessAsync(write, prepared, null, "вручную по расписанию");
            var now = _time.GetUtcNow();
            // Запуск удался — прежняя ошибка автозапуска больше не правда (и следующая снова будет новостью).
            await write.SaveTriggerAsync(current with { MissedRuns = 0, LastRunAt = now, LastProcessId = process.Id, LastError = null, UpdatedAt = now });
            await write.CommitAsync();
        }

        Notify(process.Id);
        Enqueue(process.Id);
        TriggersChanged?.Invoke();
        return process.Id;
    }

    /// <summary>Сбросить счётчик пропущенных запусков, ничего не запуская.</summary>
    public async Task DismissMissedRunsAsync(string id, CancellationToken cancellationToken = default)
    {
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            var current = await write.GetTriggerAsync(id) ?? throw new EngineOperationException("Триггер не найден.");
            await write.SaveTriggerAsync(current with { MissedRuns = 0, UpdatedAt = _time.GetUtcNow() });
            await write.CommitAsync();
        }

        TriggersChanged?.Invoke();
    }
}
