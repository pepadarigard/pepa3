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
/// Всё сопоставление идёт под одним замком (<see cref="_routingLock"/>), по одному событию, а каждое изменение
/// события записывается, только если оно всё ещё в том состоянии, в котором его прочитали.
/// </summary>
public sealed partial class ProcessEngine
{
    private readonly SemaphoreSlim _routerSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _routingLock = new(1, 1);

    /// <summary>
    /// Последнее событие, принятое до этого запуска Pepa. Такие события сопоставляются с ожидающими процессами,
    /// но процессы по ним триггеры сами не запускают (как и пропущенные запуски по расписанию).
    /// </summary>
    private long _eventsBeforeStart;

    /// <summary>Когда запущен движок: события, произошедшие у источника раньше, триггеры сами не запускают.</summary>
    private DateTimeOffset _startedAt;

    /// <summary>Когда запущен движок (см. <see cref="EventInput.OccurredAt"/>).</summary>
    public DateTimeOffset StartedAt => _startedAt;

    private enum RouteMode
    {
        /// <summary>Новое событие: подписки, затем триггеры (если событие пришло во время этой работы Pepa).</summary>
        New,

        /// <summary>Повторное сопоставление необработанного события с подписками. Триггеры не запускаются, без совпадений ничего не меняется.</summary>
        Rematch,

        /// <summary>Пользователь попросил сопоставить снова: подписки и триггеры.</summary>
        Manual,
    }

    /// <summary>Источник служебных событий платформы (завершение процессов, ответы на вопросы): снаружи его отправить нельзя.</summary>
    internal static bool IsPlatformSource(string? source) =>
        string.Equals(source?.Trim(), EventSources.Platform, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(source?.Trim(), EventSources.Question, StringComparison.OrdinalIgnoreCase);

    /// <summary>Принять событие. Повтор с тем же внешним идентификатором не создаёт новое событие.</summary>
    public async Task<PublishResult> PublishEventAsync(EventInput input, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        if (string.IsNullOrWhiteSpace(input.Source) || input.Source.Length > 100)
        {
            throw new EngineOperationException("У события должен быть источник (до 100 символов).");
        }

        if (IsPlatformSource(input.Source))
        {
            throw new EngineOperationException($"Источник «{input.Source.Trim()}» служебный — его события создаёт только сама Pepa.");
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
                    await RouteEventAsync(record, RouteMode.New, cancellationToken);
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

    /// <summary>Сопоставить одно событие. Вызывающий держит <see cref="_routingLock"/>.</summary>
    private async Task<EventState> RouteEventAsync(EventRecord record, RouteMode mode, CancellationToken cancellationToken)
    {
        var expected = record.State;

        // Попытки на случай, если подходящая подписка перестала действовать между проверкой и назначением.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidates = await FindCandidatesAsync(record, cancellationToken);

            if (candidates.Count == 0)
            {
                if (mode == RouteMode.Rematch)
                {
                    return record.State;
                }

                var allowTriggers = mode == RouteMode.Manual ||
                                    (record.Id > _eventsBeforeStart && (record.OccurredAt is null || record.OccurredAt >= _startedAt));
                var started = allowTriggers ? await StartByEventTriggersAsync(record, cancellationToken) : [];
                var now = _time.GetUtcNow();
                var final = started.Count > 0
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
                        Note = allowTriggers
                            ? "Нет ожидающих процессов с подходящими правилами. Ничего не запускалось."
                            : "Произошло или принято до запуска Pepa: ожидающих его процессов нет, а запуск по триггерам для " +
                              "таких событий сам не выполняется. Чтобы обработать событие, нажмите «Сопоставить снова».",
                        HandledAt = now,
                    };
                await using var write = await _store.BeginWriteAsync(cancellationToken);
                if (await write.TryUpdateEventAsync(final, expected))
                {
                    await write.CommitAsync();
                }

                return final.State;
            }

            var best = candidates.Max(c => c.Subscription.Priority);
            var top = candidates.Where(c => c.Subscription.Priority == best).ToList();
            if (top.Count > 1)
            {
                await using (var write = await _store.BeginWriteAsync(cancellationToken))
                {
                    if (!await write.TryUpdateEventAsync(record with
                        {
                            State = EventState.Conflict,
                            Candidates = new JsonArray(top.Select(c => (JsonNode?)new JsonObject
                            {
                                ["subscriptionId"] = c.Subscription.Id,
                                ["processId"] = c.Process.Id,
                                ["scenarioName"] = c.Process.ScenarioName,
                            }).ToArray()),
                            Note = Waiters(top.Select(c => c.Process.Id).ToList()) + " Выберите, кому его отдать.",
                            HandledAt = _time.GetUtcNow(),
                        }, expected))
                    {
                        return record.State;
                    }

                    foreach (var processId in top.Select(c => c.Process.Id).Distinct())
                    {
                        await write.AddJournalAsync(Journal(processId, JournalLevel.Warning,
                            $"Событие #{record.Id} «{record.Source}» подходит сразу нескольким ожиданиям. " +
                            "Оно не отдано никому — нужно решение на странице «Выполнение»."));
                    }

                    await write.CommitAsync();
                }

                foreach (var candidate in top)
                {
                    Notify(candidate.Process.Id);
                }

                return EventState.Conflict;
            }

            var winner = top[0];
            await using (var write = await _store.BeginWriteAsync(cancellationToken))
            {
                if (!await write.TrySetSubscriptionStateAsync(winner.Subscription.Id, SubscriptionState.Active,
                        SubscriptionState.Fulfilled, record.Id))
                {
                    continue; // подписка уже не действует — сопоставить заново
                }

                if (!await write.TryUpdateEventAsync(record with
                    {
                        State = EventState.Matched,
                        ProcessId = winner.Process.Id,
                        SubscriptionId = winner.Subscription.Id,
                        Candidates = null,
                        Note = winner.Process.Status is ProcessStatus.Paused or ProcessStatus.NeedsAttention
                            ? "Назначено процессу; будет применено после его возобновления."
                            : null,
                        HandledAt = _time.GetUtcNow(),
                    }, expected))
                {
                    return record.State; // событие уже изменилось — подписку не трогаем (транзакция откатится)
                }

                await write.CommitAsync();
            }

            Enqueue(winner.Process.Id);
            Notify(winner.Process.Id);
            return EventState.Matched;
        }

        _logger.LogWarning("Не удалось сопоставить событие {EventId}: подписки менялись во время сопоставления.", record.Id);
        return record.State;
    }

    private sealed record Candidate(SubscriptionRecord Subscription, ProcessRecord Process);

    private async Task<List<Candidate>> FindCandidatesAsync(EventRecord record, CancellationToken cancellationToken)
    {
        var result = new List<Candidate>();
        foreach (var subscription in await _store.GetActiveSubscriptionsAsync(record.Source, cancellationToken))
        {
            if (!SubscriptionAccepts(subscription.EventType, subscription.CorrelationKey, record))
            {
                continue;
            }

            if (!WithinWaitWindow(subscription, record))
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

    /// <summary>Допуск на расхождение часов источника (например, сервера Telegram) и этого компьютера.</summary>
    private static readonly TimeSpan SourceClockTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Событие произошло во время ожидания (или в разрешённое окно перед ним). Когда событие случилось, говорит время
    /// источника (<see cref="EventRecord.OccurredAt"/>): сообщение, догруженное после перезапуска, произошло раньше, чем
    /// принято. Живые события дополнительно ограничены временем приёма — по часам этого компьютера.
    /// </summary>
    private static bool WithinWaitWindow(SubscriptionRecord subscription, EventRecord record)
    {
        var from = subscription.CreatedAt - TimeSpan.FromSeconds(subscription.LookbackSeconds);
        if (record.ReceivedAt < from || (record.OccurredAt is { } occurred && occurred < from - SourceClockTolerance))
        {
            return false;
        }

        var happened = record.OccurredAt is { } at && at < record.ReceivedAt ? at : record.ReceivedAt;
        return subscription.ExpiresAt is not { } expires || happened <= expires;
    }

    private static bool SubscriptionAccepts(string? eventType, string? correlationKey, EventRecord record) =>
        (eventType is null || string.Equals(eventType, record.Type, StringComparison.OrdinalIgnoreCase)) &&
        (correlationKey is null || string.Equals(correlationKey, record.CorrelationKey, StringComparison.Ordinal));

    /// <summary>Новое ожидание с окном «пришедшие раньше»: какие необработанные события стоит предложить подпискам.</summary>
    private sealed record ReclaimRequest(string Source, string? EventType, string? CorrelationKey, DateTimeOffset Since);

    /// <summary>
    /// Необработанные события из разрешённого окна сопоставляются с действующими подписками. Триггеры при этом не
    /// запускаются: событие уже однажды «никому не подошло».
    /// </summary>
    private async Task ReclaimUnmatchedAsync(ReclaimRequest request)
    {
        await _routingLock.WaitAsync();
        try
        {
            var unmatched = await _store.ListEventsAsync(new EventQuery { States = [EventState.Unmatched], Limit = 500 });
            foreach (var record in unmatched
                         .Where(e => e.Source == request.Source && e.ReceivedAt >= request.Since &&
                                     SubscriptionAccepts(request.EventType, request.CorrelationKey, e))
                         .OrderBy(e => e.Id))
            {
                await RouteEventAsync(record, RouteMode.Rematch, CancellationToken.None);
            }
        }
        finally
        {
            _routingLock.Release();
        }
    }

    /// <summary>События, которые вернулись на сопоставление (процесс завершился или повторил шаг, не применив их).</summary>
    private async Task RematchEventsAsync(IReadOnlyCollection<long> eventIds)
    {
        await _routingLock.WaitAsync();
        try
        {
            foreach (var id in eventIds.Order())
            {
                if (await _store.GetEventAsync(id) is { State: EventState.Unmatched } record)
                {
                    await RouteEventAsync(record, RouteMode.Rematch, CancellationToken.None);
                }
            }
        }
        finally
        {
            _routingLock.Release();
        }
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
            await using var write = await _store.BeginWriteAsync(cancellationToken);
            var record = await write.GetEventAsync(eventId) ?? throw new EngineOperationException("Событие не найдено.");
            if (record.State is not (EventState.Conflict or EventState.Unmatched))
            {
                throw new EngineOperationException("Событие уже обработано.");
            }

            var now = _time.GetUtcNow();
            if (subscriptionId is null)
            {
                await write.TryUpdateEventAsync(record with
                {
                    State = EventState.Ignored,
                    Note = "Пропущено по решению пользователя.",
                    HandledAt = now,
                }, record.State);
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

                await write.TryUpdateEventAsync(record with
                {
                    State = EventState.Matched,
                    ProcessId = subscription.ProcessId,
                    SubscriptionId = subscription.Id,
                    Candidates = null,
                    Note = "Назначено пользователем.",
                    HandledAt = now,
                }, record.State);
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

    /// <summary>Повторить сопоставление необработанного события по просьбе пользователя (подписки и триггеры).</summary>
    public async Task<ControlResult> RerouteEventAsync(long eventId, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        EventState state;
        await _routingLock.WaitAsync(cancellationToken);
        try
        {
            var record = await _store.GetEventAsync(eventId, cancellationToken) ?? throw new EngineOperationException("Событие не найдено.");
            if (record.State is not (EventState.Unmatched or EventState.Conflict))
            {
                throw new EngineOperationException("Повторить сопоставление можно только для необработанного события.");
            }

            state = await RouteEventAsync(record, RouteMode.Manual, cancellationToken);
        }
        finally
        {
            _routingLock.Release();
        }

        EventsChanged?.Invoke();
        return new ControlResult(state switch
        {
            EventState.Matched or EventState.Delivered => "Событие сопоставлено.",
            EventState.Conflict => "Событие снова подходит нескольким процессам — выберите вручную.",
            _ => "Подходящих процессов по-прежнему нет.",
        });
    }

    /// <summary>Кому подходит событие: разным процессам или нескольким ветвям одного процесса.</summary>
    private static string Waiters(IReadOnlyList<string> processIds)
    {
        var processes = processIds.Distinct(StringComparer.Ordinal).Count();
        return processes == 1
            ? $"Событие одинаково подходит {processIds.Count} ожиданиям в параллельных ветвях одного процесса."
            : processes == processIds.Count
                ? $"Событие одинаково подходит {processes} процессам."
                : $"Событие одинаково подходит {processIds.Count} ожиданиям в {processes} процессах.";
    }
}
