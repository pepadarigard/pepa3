using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pepa.Core;
using Pepa.Core.Expressions;
using Pepa.Engine.Persistence;
using Pepa.Engine.Scheduling;

namespace Pepa.Engine;

/// <summary>
/// Автоматический запуск: по расписанию и по событиям. Запуски, пропущенные, пока Pepa была закрыта,
/// не выполняются сами — они учитываются, и пользователь может запустить их вручную.
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
        var triggers = await _store.ListTriggersAsync(null, cancellationToken);
        foreach (var trigger in triggers.Where(t => t is { Kind: TriggerKind.Schedule, Enabled: true, NextRunAt: { } next } && next <= now))
        {
            var occurrence = trigger.NextRunAt!.Value;
            var updated = trigger with
            {
                NextRunAt = ScheduleCalculator.Next(trigger.Config, now, _time.LocalTimeZone),
                UpdatedAt = now,
            };

            if (trigger.Overlap == "skip" && trigger.LastProcessId is { } lastId &&
                await _store.GetProcessAsync(lastId, cancellationToken) is { IsTerminal: false })
            {
                updated = updated with { SkippedRuns = trigger.SkippedRuns + 1 };
            }
            else
            {
                try
                {
                    var processId = await StartProcessAsync(trigger.ScenarioId, trigger.Inputs?.DeepClone() as JsonObject,
                        $"schedule:{trigger.Id}:{occurrence:O}", $"по расписанию: {ScheduleCalculator.Describe(trigger.Config)}",
                        cancellationToken);
                    updated = updated with { LastRunAt = now, LastProcessId = processId, LastError = null };
                }
                catch (Exception ex) when (ex is EngineOperationException or ScenarioInvalidException)
                {
                    updated = updated with { LastError = ex.Message, LastRunAt = now };
                }
            }

            if (updated.NextRunAt is null)
            {
                updated = updated with { Enabled = false };
            }

            await using var write = await _store.BeginWriteAsync(cancellationToken);
            await write.SaveTriggerAsync(updated);
            await write.CommitAsync();
            TriggersChanged?.Invoke();
        }
    }

    /// <summary>Изменились триггеры или их состояние.</summary>
    public event Action? TriggersChanged;

    /// <summary>Для расписаний, чей срок прошёл, пока Pepa была закрыта: учесть пропуск и назначить следующий запуск.</summary>
    private async Task<int> RecordMissedSchedulesAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var missedTotal = 0;
        foreach (var trigger in await _store.ListTriggersAsync(null, cancellationToken))
        {
            if (trigger is not { Kind: TriggerKind.Schedule, Enabled: true, NextRunAt: { } next } || next > now)
            {
                continue;
            }

            var missed = 1 + ScheduleCalculator.CountBetween(trigger.Config, next, now, _time.LocalTimeZone);
            missedTotal += missed;
            var nextRun = ScheduleCalculator.Next(trigger.Config, now, _time.LocalTimeZone);
            await using var write = await _store.BeginWriteAsync(cancellationToken);
            await write.SaveTriggerAsync(trigger with
            {
                MissedRuns = trigger.MissedRuns + missed,
                LastMissedAt = next,
                NextRunAt = nextRun,
                Enabled = nextRun is not null,
                UpdatedAt = now,
            });
            await write.CommitAsync();
        }

        return missedTotal;
    }

    /// <summary>Запуск процессов триггерами по событию, которое никто не ждал.</summary>
    private async Task<List<string>> StartByEventTriggersAsync(EventRecord record, CancellationToken cancellationToken)
    {
        var started = new List<string>();
        var triggers = (await _store.ListTriggersAsync(null, cancellationToken))
            .Where(t => t is { Kind: TriggerKind.Event, Enabled: true });
        var eventScope = new ExpressionScope(new JsonObject(), Extra: new JsonObject { ["event"] = EventJson(record) });
        foreach (var trigger in triggers)
        {
            var source = trigger.Config.GetString("source");
            var type = trigger.Config.GetString("eventType");
            if (!string.Equals(source, record.Source, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(type) && !string.Equals(type, record.Type, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string? error = null;
            try
            {
                if (trigger.Config.GetString("filter") is { Length: > 0 } filter && !_expressions.EvaluateCondition(filter, eventScope))
                {
                    continue;
                }

                var inputs = new JsonObject();
                foreach (var (name, expression) in trigger.Inputs ?? [])
                {
                    if (expression.AsString() is { Length: > 0 } code)
                    {
                        inputs[name] = _expressions.Evaluate(code, eventScope);
                    }
                }

                var processId = await StartProcessAsync(trigger.ScenarioId, inputs, $"event:{trigger.Id}:{record.Id}",
                    $"по событию #{record.Id} «{record.Source}»", cancellationToken);
                started.Add(processId);
                await using var write = await _store.BeginWriteAsync(cancellationToken);
                await write.SaveTriggerAsync(trigger with
                {
                    LastRunAt = _time.GetUtcNow(), LastProcessId = processId, LastError = null, UpdatedAt = _time.GetUtcNow(),
                });
                await write.CommitAsync();
            }
            catch (Exception ex) when (ex is ExpressionException or EngineOperationException or ScenarioInvalidException)
            {
                error = ex.Message;
            }

            if (error is not null)
            {
                await using var write = await _store.BeginWriteAsync(cancellationToken);
                await write.SaveTriggerAsync(trigger with { LastError = error, UpdatedAt = _time.GetUtcNow() });
                await write.CommitAsync();
            }
        }

        if (started.Count > 0)
        {
            TriggersChanged?.Invoke();
        }

        return started;
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
        var existing = string.IsNullOrEmpty(trigger.Id) ? null : await _store.GetTriggerAsync(trigger.Id, cancellationToken);
        var saved = trigger with
        {
            Id = existing?.Id ?? (string.IsNullOrEmpty(trigger.Id) ? NewId() : trigger.Id),
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
        };

        if (saved.Kind == TriggerKind.Schedule)
        {
            if (ScheduleCalculator.Validate(saved.Config) is { } problem)
            {
                throw new EngineOperationException(problem);
            }

            var next = ScheduleCalculator.Next(saved.Config, now, _time.LocalTimeZone);
            if (next is null && saved.Enabled)
            {
                throw new EngineOperationException("По этому расписанию запусков больше не будет (время уже прошло).");
            }

            saved = saved with { NextRunAt = saved.Enabled ? next : null };
        }
        else
        {
            if (string.IsNullOrWhiteSpace(saved.Config.GetString("source")))
            {
                throw new EngineOperationException("Укажите источник события.");
            }

            if (saved.Config.GetString("filter") is { Length: > 0 } filter && _expressions.CheckExpression(filter) is { } error)
            {
                throw new EngineOperationException($"Условие: {error}");
            }

            foreach (var (name, value) in saved.Inputs ?? [])
            {
                if (value.AsString() is { Length: > 0 } code && _expressions.CheckExpression(code) is { } inputError)
                {
                    throw new EngineOperationException($"Значение «{name}»: {inputError}");
                }
            }

            saved = saved with { NextRunAt = null };
        }

        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
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
        var trigger = await _store.GetTriggerAsync(id, cancellationToken) ?? throw new EngineOperationException("Триггер не найден.");
        if (trigger.Kind != TriggerKind.Schedule)
        {
            throw new EngineOperationException("Вручную запускаются только расписания.");
        }

        var processId = await StartProcessAsync(trigger.ScenarioId, trigger.Inputs?.DeepClone() as JsonObject,
            null, "вручную по расписанию", cancellationToken);
        var now = _time.GetUtcNow();
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            await write.SaveTriggerAsync(trigger with { MissedRuns = 0, LastRunAt = now, LastProcessId = processId, UpdatedAt = now });
            await write.CommitAsync();
        }

        TriggersChanged?.Invoke();
        return processId;
    }

    /// <summary>Сбросить счётчик пропущенных запусков, ничего не запуская.</summary>
    public async Task DismissMissedRunsAsync(string id, CancellationToken cancellationToken = default)
    {
        var trigger = await _store.GetTriggerAsync(id, cancellationToken) ?? throw new EngineOperationException("Триггер не найден.");
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            await write.SaveTriggerAsync(trigger with { MissedRuns = 0, UpdatedAt = _time.GetUtcNow() });
            await write.CommitAsync();
        }

        TriggersChanged?.Invoke();
    }
}
