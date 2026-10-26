using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pepa.Core;
using Pepa.Core.Expressions;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

public sealed record PublishResult(long EventId, bool Duplicate);

/// <summary>
/// Система событий: приём с защитой от повторов, сопоставление с ожидающими процессами по правилам,
/// конфликты при неоднозначности, запуск процессов триггерами.
/// </summary>
public sealed partial class ProcessEngine
{
    private readonly SemaphoreSlim _routerSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _routingLock = new(1, 1);

    /// <summary>Принять событие. Повтор с тем же внешним идентификатором не создаёт новое событие.</summary>
    public async Task<PublishResult> PublishEventAsync(EventInput input, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        if (string.IsNullOrWhiteSpace(input.Source) || input.Source.Length > 100)
        {
            throw new EngineOperationException("У события должен быть источник (до 100 символов).");
        }

        if ((input.Type?.Length ?? 0) > 100)
        {
            throw new EngineOperationException("Вид события — не длиннее 100 символов.");
        }

        PublishResult result;
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            var record = NewEventRecord(input, _time.GetUtcNow());
            var id = await write.TryInsertEventAsync(record);
            if (id is null)
            {
                var existing = await write.FindEventByExternalIdAsync(record.Source, record.ExternalId!);
                result = new PublishResult(existing!.Id, true);
            }
            else
            {
                result = new PublishResult(id.Value, false);
            }

            await write.CommitAsync();
        }

        if (!result.Duplicate)
        {
            SignalRouter();
        }

        EventsChanged?.Invoke();
        return result;
    }

    /// <summary>Изменились события (для обновления интерфейса).</summary>
    public event Action? EventsChanged;

    private void SignalRouter()
    {
        if (_routerSignal.CurrentCount < 2)
        {
            _routerSignal.Release();
        }
    }

    private async Task RouterLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _routerSignal.WaitAsync(TimeSpan.FromSeconds(5), stop);
                await RoutePendingAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка сопоставления событий.");
            }
        }
    }

    /// <summary>Сопоставить все принятые, но ещё не обработанные события — по порядку поступления.</summary>
    internal async Task RoutePendingAsync(CancellationToken cancellationToken = default)
    {
        await _routingLock.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                var pending = await _store.ListEventsAsync(new EventQuery { States = [EventState.Received], Limit = 200 }, cancellationToken);
                if (pending.Count == 0)
                {
                    break;
                }

                foreach (var record in pending)
                {
                    await RouteEventAsync(record, cancellationToken);
                }

                if (pending.Count < 200)
                {
                    break;
                }
            }
        }
        finally
        {
            _routingLock.Release();
        }

        EventsChanged?.Invoke();
    }

    private async Task RouteEventAsync(EventRecord record, CancellationToken cancellationToken)
    {
        // Попытки на случай, если подходящая подписка перестала действовать между проверкой и назначением.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidates = await FindCandidatesAsync(record, cancellationToken);
            var now = _time.GetUtcNow();

            if (candidates.Count == 0)
            {
                var started = await StartByEventTriggersAsync(record, cancellationToken);
                await using var write = await _store.BeginWriteAsync(cancellationToken);
                await write.UpdateEventAsync(started.Count > 0
                    ? record with
                    {
                        State = EventState.Delivered,
                        StartedProcesses = new JsonArray(started.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
                        Note = $"Запущено процессов по триггерам: {started.Count}.",
                        HandledAt = now,
                    }
                    : record with
                    {
                        State = EventState.Unmatched,
                        Note = "Нет ожидающих процессов с подходящими правилами. Ничего не запускалось.",
                        HandledAt = now,
                    });
                await write.CommitAsync();
                return;
            }

            var best = candidates.Max(c => c.Subscription.Priority);
            var top = candidates.Where(c => c.Subscription.Priority == best).ToList();
            if (top.Count > 1)
            {
                await using var write = await _store.BeginWriteAsync(cancellationToken);
                await write.UpdateEventAsync(record with
                {
                    State = EventState.Conflict,
                    Candidates = new JsonArray(top.Select(c => (JsonNode?)new JsonObject
                    {
                        ["subscriptionId"] = c.Subscription.Id,
                        ["processId"] = c.Process.Id,
                        ["scenarioName"] = c.Process.ScenarioName,
                    }).ToArray()),
                    Note = $"Событие одинаково подходит {top.Count} процессам. Выберите, кому его отдать.",
                    HandledAt = now,
                });
                foreach (var candidate in top)
                {
                    await write.AddJournalAsync(Journal(candidate.Process.Id, JournalLevel.Warning,
                        $"Событие #{record.Id} «{record.Source}» подходит сразу нескольким процессам. " +
                        "Оно не отдано никому — нужно решение на странице «Выполнение»."));
                }

                await write.CommitAsync();
                foreach (var candidate in top)
                {
                    Notify(candidate.Process.Id);
                }

                return;
            }

            var winner = top[0];
            await using (var write = await _store.BeginWriteAsync(cancellationToken))
            {
                if (!await write.TrySetSubscriptionStateAsync(winner.Subscription.Id, SubscriptionState.Active,
                        SubscriptionState.Fulfilled, record.Id))
                {
                    continue; // подписка уже не действует — сопоставить заново
                }

                await write.UpdateEventAsync(record with
                {
                    State = EventState.Matched,
                    ProcessId = winner.Process.Id,
                    SubscriptionId = winner.Subscription.Id,
                    Candidates = null,
                    Note = winner.Process.Status is ProcessStatus.Paused or ProcessStatus.NeedsAttention
                        ? "Назначено процессу; будет применено после его возобновления."
                        : null,
                    HandledAt = now,
                });
                await write.CommitAsync();
            }

            Enqueue(winner.Process.Id);
            Notify(winner.Process.Id);
            return;
        }

        _logger.LogWarning("Не удалось сопоставить событие {EventId}: подписки менялись во время сопоставления.", record.Id);
    }

    private sealed record Candidate(SubscriptionRecord Subscription, ProcessRecord Process);

    private async Task<List<Candidate>> FindCandidatesAsync(EventRecord record, CancellationToken cancellationToken)
    {
        var result = new List<Candidate>();
        foreach (var subscription in await _store.GetActiveSubscriptionsAsync(record.Source, cancellationToken))
        {
            if (subscription.EventType is { } type && !string.Equals(type, record.Type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (subscription.CorrelationKey is { } key && !string.Equals(key, record.CorrelationKey, StringComparison.Ordinal))
            {
                continue;
            }

            // Только события, пришедшие во время ожидания (или в разрешённое окно перед ним).
            if (record.ReceivedAt < subscription.CreatedAt - TimeSpan.FromSeconds(subscription.LookbackSeconds) ||
                (subscription.ExpiresAt is { } expires && record.ReceivedAt > expires))
            {
                continue;
            }

            var process = await _store.GetProcessAsync(subscription.ProcessId, cancellationToken);
            if (process is null || process.IsTerminal)
            {
                continue;
            }

            if (subscription.MatchExpression is { } expression)
            {
                bool matches;
                try
                {
                    matches = _expressions.EvaluateCondition(expression, Scope(process) with
                    {
                        Extra = new JsonObject { ["event"] = EventJson(record) },
                    });
                }
                catch (ExpressionException ex)
                {
                    await using var write = await _store.BeginWriteAsync(cancellationToken);
                    await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning,
                        $"Условие ожидания события не вычислилось для события #{record.Id}: {ex.Message}"));
                    await write.CommitAsync();
                    continue;
                }

                if (!matches)
                {
                    continue;
                }
            }

            result.Add(new Candidate(subscription, process));
        }

        return result;
    }

    /// <summary>
    /// Новое ожидание: необработанные события из разрешённого окна снова отправляются на сопоставление —
    /// с учётом всех действующих подписок, а не только новой.
    /// </summary>
    private async Task ReclaimUnmatchedAsync(string source, DateTimeOffset since)
    {
        var unmatched = await _store.ListEventsAsync(new EventQuery { States = [EventState.Unmatched], Limit = 500 });
        var candidates = unmatched.Where(e => e.Source == source && e.ReceivedAt >= since).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        await using var write = await _store.BeginWriteAsync();
        foreach (var record in candidates)
        {
            await write.UpdateEventAsync(record with { State = EventState.Received, Note = null, HandledAt = null });
        }

        await write.CommitAsync();
    }

    /// <summary>Решение пользователя по конфликтному или необработанному событию.</summary>
    public async Task<ControlResult> ResolveEventAsync(long eventId, long? subscriptionId, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        string? processId = null;
        ControlResult result;
        await _routingLock.WaitAsync(cancellationToken);
        try
        {
            var record = await _store.GetEventAsync(eventId, cancellationToken)
                         ?? throw new EngineOperationException("Событие не найдено.");
            if (record.State is not (EventState.Conflict or EventState.Unmatched))
            {
                throw new EngineOperationException("Событие уже обработано.");
            }

            var now = _time.GetUtcNow();
            await using var write = await _store.BeginWriteAsync(cancellationToken);
            if (subscriptionId is null)
            {
                await write.UpdateEventAsync(record with
                {
                    State = EventState.Ignored,
                    Note = "Пропущено по решению пользователя.",
                    HandledAt = now,
                });
                result = new ControlResult("Событие пропущено.");
            }
            else
            {
                var subscription = await write.GetSubscriptionAsync(subscriptionId.Value)
                                   ?? throw new EngineOperationException("Ожидание не найдено.");
                if (subscription.Source != record.Source ||
                    !await write.TrySetSubscriptionStateAsync(subscription.Id, SubscriptionState.Active, SubscriptionState.Fulfilled, record.Id))
                {
                    throw new EngineOperationException("Этот процесс уже не ждёт такое событие.");
                }

                await write.UpdateEventAsync(record with
                {
                    State = EventState.Matched,
                    ProcessId = subscription.ProcessId,
                    SubscriptionId = subscription.Id,
                    Candidates = null,
                    Note = "Назначено пользователем.",
                    HandledAt = now,
                });
                await write.AddJournalAsync(Journal(subscription.ProcessId, JournalLevel.Info,
                    $"Событие #{record.Id} назначено этому процессу пользователем."));
                processId = subscription.ProcessId;
                result = new ControlResult("Событие отдано выбранному процессу.");
            }

            await write.CommitAsync();
        }
        finally
        {
            _routingLock.Release();
        }

        if (processId is not null)
        {
            Enqueue(processId);
            Notify(processId);
        }

        EventsChanged?.Invoke();
        return result;
    }

    /// <summary>Повторить сопоставление необработанного события (например, после запуска нужного процесса).</summary>
    public async Task<ControlResult> RerouteEventAsync(long eventId, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var record = await _store.GetEventAsync(eventId, cancellationToken) ?? throw new EngineOperationException("Событие не найдено.");
        if (record.State is not (EventState.Unmatched or EventState.Conflict))
        {
            throw new EngineOperationException("Повторить сопоставление можно только для необработанного события.");
        }

        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            await write.UpdateEventAsync(record with { State = EventState.Received, Candidates = null, Note = null, HandledAt = null });
            await write.CommitAsync();
        }

        await RoutePendingAsync(cancellationToken);
        var updated = await _store.GetEventAsync(eventId, cancellationToken);
        return new ControlResult(updated?.State switch
        {
            EventState.Matched or EventState.Delivered => "Событие сопоставлено.",
            EventState.Conflict => "Событие снова подходит нескольким процессам — выберите вручную.",
            _ => "Подходящих процессов по-прежнему нет.",
        });
    }
}
