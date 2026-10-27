using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Serialization;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

/// <summary>
/// Действия платформы: подписки на события, ресурсы, дочерние процессы. Всё, что меняет состояние процесса,
/// выполняется под его «правом записи» и в одной транзакции с итогом шага.
/// </summary>
public sealed partial class ProcessEngine
{
    private const int MaxDepth = 8;

    /// <summary>Что сделать после подтверждения транзакции.</summary>
    private sealed class CommitEffects
    {
        public HashSet<string> Enqueue { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Notify { get; } = new(StringComparer.Ordinal);
        public List<string> CancelChildren { get; } = [];
        public List<ReclaimRequest> Reclaim { get; } = [];

        /// <summary>События, вернувшиеся на сопоставление (только с подписками, без триггеров).</summary>
        public HashSet<long> Rematch { get; } = [];
        public bool RouteEvents { get; set; }
    }

    /// <summary>
    /// Последствия уже подтверждённой транзакции. Ошибка здесь не должна делать процесс «сломанным»:
    /// его состояние уже записано, поэтому ошибки только записываются в журнал приложения.
    /// </summary>
    private async Task ApplyEffectsAsync(CommitEffects effects)
    {
        foreach (var id in effects.Notify)
        {
            Notify(id);
        }

        foreach (var id in effects.Enqueue)
        {
            Enqueue(id);
        }

        try
        {
            foreach (var request in effects.Reclaim)
            {
                await ReclaimUnmatchedAsync(request);
            }

            if (effects.Rematch.Count > 0)
            {
                await RematchEventsAsync(effects.Rematch);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Не удалось повторно сопоставить события (они остались необработанными и видны в списке событий).");
        }

        if (effects.RouteEvents)
        {
            SignalRouter();
        }

        foreach (var childId in effects.CancelChildren)
        {
            _ = CancelChildSafelyAsync(childId);
        }
    }

    private async Task CancelChildSafelyAsync(string childId)
    {
        try
        {
            await CancelAsync(childId);
        }
        catch (Exception ex) when (ex is EngineOperationException or InvalidOperationException)
        {
            // Дочерний процесс уже завершён.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось остановить дочерний процесс {ProcessId}.", childId);
        }
    }

    // ───────────────────────────── Действия шага ─────────────────────────────

    /// <summary>Превращает действия, исход которых известен сразу (ресурс свободен, событие записано), в обычный итог.</summary>
    private async Task<NodeResult> ResolveIntentAsync(IStoreWrite write, ProcessRecord process, TokenRecord token, string label,
        NodeResult result, CommitEffects effects, DateTimeOffset now)
    {
        switch (result)
        {
            case NodeResult.AcquireResource acquire:
            {
                var key = acquire.Key.Trim();
                if (key.Length == 0)
                {
                    return NodeResult.Fail("Не указан ресурс.", retryable: false);
                }

                var resource = await write.GetResourceAsync(key);
                if (resource?.HolderProcessId == process.Id)
                {
                    return acquire.RepeatNode
                        ? new NodeResult.RunAgain(null)
                        : NodeResult.Ok(Ports.Out, EmptyUpdates, new JsonObject { ["resource"] = key },
                            $"Ресурс «{key}» уже занят этим процессом.");
                }

                if (resource?.HolderProcessId is null)
                {
                    // Свободен — но если в очереди уже есть активные ожидающие, честно встаём за ними.
                    var waiters = await write.GetWaitersAsync(key);
                    if (!waiters.Any(w => w.ProcessId != process.Id && IsActiveWaiter(w)))
                    {
                        await write.UpsertResourceAsync(new ResourceRecord
                        {
                            Key = key,
                            Title = acquire.Title ?? resource?.Title ?? key,
                            HolderProcessId = process.Id,
                            AcquiredAt = now,
                            UpdatedAt = now,
                        });
                        return acquire.RepeatNode
                            ? new NodeResult.RunAgain($"Ресурс «{acquire.Title ?? key}» занят процессом.")
                            : NodeResult.Ok(Ports.Out, EmptyUpdates, new JsonObject { ["resource"] = key },
                                $"Ресурс «{key}» занят процессом.");
                    }
                }
                else if (await WouldDeadlockAsync(write, process.Id, resource.HolderProcessId))
                {
                    return NodeResult.Fail(
                        $"Взаимная блокировка: ресурс «{key}» занят процессом #{resource.HolderProcessId[^6..]}, " +
                        "который сам ждёт этот процесс (его ресурс или завершение вызванного сценария). " +
                        "Освободите ресурс до вызова или измените порядок.", retryable: false);
                }

                if (resource is null)
                {
                    await write.UpsertResourceAsync(new ResourceRecord { Key = key, Title = acquire.Title ?? key, UpdatedAt = now });
                }

                return acquire with { Key = key };
            }

            case NodeResult.ReleaseResource release:
            {
                var key = release.Key.Trim();
                var resource = await write.GetResourceAsync(key);
                if (resource?.HolderProcessId != process.Id)
                {
                    return new NodeResult.Success(Ports.Out, EmptyUpdates, null,
                        $"Ресурс «{key}» не был занят этим процессом — освобождать нечего.", MessageLevel.Warning);
                }

                var granted = await ReleaseAndGrantAsync(write, resource, now);
                if (granted is not null)
                {
                    effects.Enqueue.Add(granted);
                }

                return NodeResult.Ok(Ports.Out, EmptyUpdates, new JsonObject { ["resource"] = key },
                    granted is null ? $"Ресурс «{key}» освобождён." : $"Ресурс «{key}» освобождён и передан процессу #{granted[^6..]}.");
            }

            case NodeResult.PublishEvent publish:
            {
                var input = publish.Event;
                if (string.IsNullOrWhiteSpace(input.Source))
                {
                    return NodeResult.Fail("У события не указан источник.", retryable: false);
                }

                if (IsPlatformSource(input.Source))
                {
                    return NodeResult.Fail($"Источник «{EventSources.Platform}» служебный — его события создаёт только сама Pepa.", retryable: false);
                }

                // Секреты отправителя в событии (данные, ключ) событие помнит: при показе они скрыты, получателю — секретны.
                var senderSecrets = RawSecretValues(process, Graph(process).Definition);
                var carried = senderSecrets.Count == 0
                    ? null
                    : SecretsIn(senderSecrets, input.Payload, JsonValue.Create(input.CorrelationKey), JsonValue.Create(input.ExternalId)) is
                        { Count: > 0 } found ? found : null;
                var id = await write.TryInsertEventAsync(NewEventRecord(input, now) with { Secrets = carried });
                long eventId;
                var duplicate = false;
                if (id is null)
                {
                    duplicate = true;
                    eventId = (await write.FindEventByExternalIdAsync(input.Source, input.ExternalId!))!.Id;
                }
                else
                {
                    eventId = id.Value;
                    effects.RouteEvents = true;
                }

                return NodeResult.Ok(Ports.Out, EmptyUpdates,
                    new JsonObject { ["eventId"] = eventId, ["duplicate"] = duplicate },
                    duplicate
                        ? $"Событие «{input.Type}» с ключом «{input.ExternalId}» уже было отправлено раньше — повтор не нужен."
                        : $"Отправлено событие «{input.Type}»" + (input.CorrelationKey is { Length: > 0 } k ? $" (ключ «{k}»)." : "."));
            }

            case NodeResult.Success { ReleaseResources: { Count: > 0 } keys }:
            {
                foreach (var key in keys.Select(k => k.Trim()).Distinct(StringComparer.Ordinal))
                {
                    if (await write.GetResourceAsync(key) is { } held && held.HolderProcessId == process.Id &&
                        await ReleaseAndGrantAsync(write, held, now) is { } granted)
                    {
                        effects.Enqueue.Add(granted);
                    }
                }

                return result;
            }

            default:
                return result;
        }
    }

    /// <summary>Источники сравниваются без учёта регистра: храним их в нижнем регистре.</summary>
    internal static string NormalizeSource(string source) => source.Trim().ToLowerInvariant();

    internal static EventRecord NewEventRecord(EventInput input, DateTimeOffset now) => new()
    {
        Source = NormalizeSource(input.Source),
        Type = input.Type?.Trim() ?? "",
        ExternalId = string.IsNullOrWhiteSpace(input.ExternalId) ? null : input.ExternalId.Trim(),
        CorrelationKey = string.IsNullOrEmpty(input.CorrelationKey) ? null : input.CorrelationKey,
        Payload = input.Payload?.DeepClone(),
        ReceivedAt = now,
        OccurredAt = input.OccurredAt,
        State = EventState.Received,
    };

    private static JsonObject DescribeWait(NodeResult.WaitForEvent wait) => new()
    {
        ["source"] = wait.Subscription.Source,
        ["eventType"] = wait.Subscription.EventType,
        ["correlationKey"] = wait.Subscription.CorrelationKey,
        ["until"] = wait.TimeoutAt?.ToString("O"),
    };

    internal static string DescribeSpec(EventSubscriptionSpec spec) =>
        $"«{spec.Source}" + (string.IsNullOrWhiteSpace(spec.EventType) ? "" : $" / {spec.EventType}") + "»" +
        (string.IsNullOrEmpty(spec.CorrelationKey) ? "" : $" с ключом «{spec.CorrelationKey}»");

    // ───────────────────────────── Ожидания ─────────────────────────────

    /// <summary>
    /// Проверить ожидание события или ресурса: пришло ли то, чего ждём, или вышел срок.
    /// Возвращает true, если состояние процесса изменилось.
    /// </summary>
    private async Task<bool> TryResolveWaitAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token, DateTimeOffset now)
    {
        if (token.WaitingStepId is not { } stepId)
        {
            return false;
        }

        var node = graph.FindNode(token.NodeId);
        if (node is null || !_registry.TryGet(node.Type, out var executor))
        {
            return false;
        }

        var step = await _store.GetStepAsync(stepId);
        if (step is null)
        {
            return false;
        }

        return token.WaitKind == WaitKind.Event
            ? await TryResolveEventWaitAsync(process, graph, token, node, executor.Descriptor, step, now)
            : await TryResolveResourceWaitAsync(process, graph, token, node, executor.Descriptor, step, now);
    }

    private async Task<bool> TryResolveEventWaitAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token,
        NodeDefinition node, NodeTypeDescriptor descriptor, StepRecord step, DateTimeOffset now)
    {
        if (!long.TryParse(token.WaitRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var subscriptionId))
        {
            return false;
        }

        var subscription = await _store.GetSubscriptionAsync(subscriptionId);
        if (subscription is null)
        {
            return false;
        }

        if (subscription is { State: SubscriptionState.Fulfilled, EventId: { } eventId })
        {
            var record = await _store.GetEventAsync(eventId);
            if (record is null)
            {
                return false;
            }

            var eventJson = EventJson(record);
            NodeResult result;
            if (subscription.Purpose == SubscriptionPurpose.ChildProcess)
            {
                var payload = record.Payload as JsonObject;
                var status = payload?.GetString("status");
                var childMessage = payload?.GetString("message");
                var updates = new Dictionary<string, JsonNode?>();
                if (subscription.SaveTo is { } saveTo)
                {
                    updates[saveTo] = payload?["variables"]?.DeepClone() ?? new JsonObject();
                }

                result = status == "completed"
                    ? NodeResult.Ok(Ports.Done, updates, new JsonObject { ["childProcessId"] = record.CorrelationKey, ["status"] = status },
                        $"Вызванный сценарий завершён успешно (процесс #{record.CorrelationKey?[^6..]}).")
                    : NodeResult.Fail(
                        $"Вызванный сценарий завершился {(status == "cancelled" ? "остановкой" : "с ошибкой")}" +
                        (string.IsNullOrWhiteSpace(childMessage) ? "." : $": {childMessage}"), retryable: false);
            }
            else
            {
                var updates = new Dictionary<string, JsonNode?>();
                if (subscription.SaveTo is { } saveTo)
                {
                    updates[saveTo] = eventJson.DeepClone();
                }

                result = NodeResult.Ok(Ports.Event, updates, new JsonObject { ["event"] = eventJson },
                    $"Получено событие «{record.Source}" + (record.Type.Length > 0 ? $" / {record.Type}" : "") + $"» #{record.Id}.");
            }

            return await ApplyResultAsync(process, graph, token, node, descriptor, step, result, null, acquired: record.Secrets,
                extraWrites: async (write, _, final, effects) =>
                {
                    if (final is NodeResult.Success)
                    {
                        await write.TryUpdateEventAsync(record with
                        {
                            State = EventState.Delivered,
                            ProcessId = process.Id,
                            SubscriptionId = subscription.Id,
                            HandledAt = now,
                        }, EventState.Matched);
                    }
                    else if (await write.TryUpdateEventAsync(record with
                             {
                                 State = EventState.Unmatched,
                                 ProcessId = null,
                                 SubscriptionId = null,
                                 Note = $"Процесс #{process.Id[^6..]} не смог принять событие: {(final as NodeResult.Failure)?.Error}",
                                 HandledAt = now,
                             }, EventState.Matched))
                    {
                        // Событие не потеряно: его можно отдать другому ожидающему процессу.
                        effects.Rematch.Add(record.Id);
                    }
                });
        }

        if (subscription.State == SubscriptionState.Active && token.WakeAt is { } wakeAt && wakeAt <= now && TimeoutDue(subscription, wakeAt, now))
        {
            var isChild = subscription.Purpose == SubscriptionPurpose.ChildProcess;
            var timeoutResult = isChild
                ? NodeResult.Ok(Ports.Timeout,
                    $"Вызванный сценарий не завершился до {FormatLocal(wakeAt)} — тайм-аут, он будет остановлен (процесс #{subscription.CorrelationKey?[^6..]}).")
                : NodeResult.Ok(Ports.Timeout, $"Событие не пришло до {FormatLocal(wakeAt)} — тайм-аут.");
            var applied = await ApplyResultAsync(process, graph, token, node, descriptor, step, timeoutResult, null,
                decide: async write =>
                    await write.TrySetSubscriptionStateAsync(subscription.Id, SubscriptionState.Active, SubscriptionState.Expired, null)
                        ? timeoutResult
                        : null);
            if (applied && isChild && subscription.CorrelationKey is { } childId)
            {
                await CancelChildSafelyAsync(childId);
            }

            return applied;
        }

        return false;
    }

    /// <summary>
    /// Ожидание, начатое до запуска Pepa, завершается по тайм-ауту не раньше чем через <see cref="EngineOptions.CatchUpGrace"/>
    /// после запуска и не раньше, чем источник (например, Telegram) сообщит, что догрузил пропущенное, — но не позже
    /// <see cref="EngineOptions.MaxCatchUpWait"/>: событие, пришедшее вовремя, пока Pepa была закрыта, не должно проиграть тайм-ауту.
    /// </summary>
    private bool TimeoutDue(SubscriptionRecord subscription, DateTimeOffset wakeAt, DateTimeOffset now)
    {
        if (subscription.CreatedAt >= _startedAt)
        {
            return true;
        }

        if (now < _startedAt + _options.CatchUpGrace)
        {
            return false;
        }

        return !_catchingUp.ContainsKey(subscription.Source) || now >= _startedAt + _options.MaxCatchUpWait;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _catchingUp = new(StringComparer.Ordinal);

    /// <summary>
    /// Источник после запуска догружает события, пришедшие, пока Pepa была закрыта: тайм-ауты ожиданий его событий,
    /// начатых до запуска, пока не наступают. Вызывать до <see cref="StartAsync"/>.
    /// </summary>
    public void SourceCatchingUp(string source) => _catchingUp[source] = true;

    /// <summary>Источник догрузил пропущенное (см. <see cref="SourceCatchingUp"/>).</summary>
    /// <remarks>Тайм-ауты проверяет таймер движка — отдельного сигнала не нужно.</remarks>
    public void SourceCaughtUp(string source) => _catchingUp.TryRemove(source, out _);

    private async Task<bool> TryResolveResourceWaitAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token,
        NodeDefinition node, NodeTypeDescriptor descriptor, StepRecord step, DateTimeOffset now)
    {
        var key = token.WaitRef;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        // Ресурс, который блок занимает сам: получив его, блок выполняется снова (уже с ресурсом).
        var repeat = step.Output?["repeat"]?.GetValue<bool>() == true;
        NodeResult Granted(string message) => repeat
            ? new NodeResult.RunAgain(message)
            : NodeResult.Ok(Ports.Out, EmptyUpdates, new JsonObject { ["resource"] = key }, message);

        return await ApplyResultAsync(process, graph, token, node, descriptor, step, NodeResult.Ok(), null, decide: async write =>
        {
            var resource = await write.GetResourceAsync(key);
            if (resource?.HolderProcessId == process.Id)
            {
                await write.RemoveWaiterAsync(key, process.Id);
                return Granted($"Ресурс «{resource.Title}» получен из очереди.");
            }

            if (resource is null || resource.HolderProcessId is null)
            {
                var first = (await write.GetWaitersAsync(key))
                    .FirstOrDefault(w => w.ProcessId == process.Id || IsActiveWaiter(w));
                if (first is null || first.ProcessId == process.Id)
                {
                    await write.UpsertResourceAsync(new ResourceRecord
                    {
                        Key = key,
                        Title = resource?.Title ?? key,
                        HolderProcessId = process.Id,
                        AcquiredAt = now,
                        UpdatedAt = now,
                    });
                    await write.RemoveWaiterAsync(key, process.Id);
                    return Granted($"Ресурс «{resource?.Title ?? key}» освободился и занят процессом.");
                }
            }

            if (token.WakeAt is { } wakeAt && wakeAt <= now)
            {
                await write.RemoveTokenWaiterAsync(key, token.Id);
                return NodeResult.Ok(Ports.Timeout, $"Ресурс «{key}» не освободился до {FormatLocal(wakeAt)} — тайм-аут.");
            }

            return null;
        });
    }

    // ───────────────────────────── Ресурсы ─────────────────────────────

    /// <summary>Держатель ресурса — сама Pepa, а не процесс (например, «pepa:recording» — запись действий).</summary>
    public const string ExternalHolderPrefix = "pepa:";

    /// <summary>Запись действий в окнах Windows держит мышь и клавиатуру.</summary>
    public const string RecordingHolder = ExternalHolderPrefix + "recording";

    public static bool IsExternalHolder(string? holder) => holder?.StartsWith(ExternalHolderPrefix, StringComparison.Ordinal) == true;

    /// <summary>Кем занят ресурс, если не процессом: «запись действий в окнах Windows»; null — процессом (или свободен).</summary>
    public static string? ExternalHolderTitle(string? holder) =>
        holder == RecordingHolder ? "запись действий в окнах Windows" : IsExternalHolder(holder) ? "Pepa" : null;

    /// <summary>
    /// Занять ресурс самой Pepa (не процессом): процессы, которым он нужен, ждут в очереди, как если бы его держал другой
    /// процесс. После перезапуска такой ресурс свободен (его держатель — не процесс).
    /// </summary>
    /// <returns>null — занят; иначе — процесс, который держит ресурс сейчас (тогда ничего не изменилось).</returns>
    public async Task<string?> TryHoldExternallyAsync(string key, string title, string holder, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        if (!IsExternalHolder(holder))
        {
            throw new ArgumentException("Держатель должен начинаться с " + ExternalHolderPrefix, nameof(holder));
        }

        await using var write = await _store.BeginWriteAsync(cancellationToken);
        var resource = await write.GetResourceAsync(key);
        if (resource?.HolderProcessId is { } current && current != holder)
        {
            return current;
        }

        var now = _time.GetUtcNow();
        await write.UpsertResourceAsync(new ResourceRecord
        {
            Key = key, Title = resource?.Title ?? title, HolderProcessId = holder, AcquiredAt = resource?.AcquiredAt ?? now, UpdatedAt = now,
        });
        await write.CommitAsync();
        return null;
    }

    /// <summary>Освободить ресурс, занятый самой Pepa, и передать первому в очереди.</summary>
    public async Task ReleaseExternalAsync(string key, string holder, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var effects = new CommitEffects();
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            if (await write.GetResourceAsync(key) is not { } resource || resource.HolderProcessId != holder)
            {
                return;
            }

            if (await ReleaseAndGrantAsync(write, resource, _time.GetUtcNow()) is { } granted)
            {
                effects.Enqueue.Add(granted);
            }

            await write.CommitAsync();
        }

        await ApplyEffectsAsync(effects);
    }

    /// <summary>Освободить ресурс и сразу передать первому в очереди активному процессу. Возвращает получателя.</summary>
    private static async Task<string?> ReleaseAndGrantAsync(IStoreWrite write, ResourceRecord resource, DateTimeOffset now)
    {
        var next = (await write.GetWaitersAsync(resource.Key)).FirstOrDefault(IsActiveWaiter);
        await write.UpsertResourceAsync(resource with
        {
            HolderProcessId = next?.ProcessId,
            AcquiredAt = next is null ? null : now,
            UpdatedAt = now,
        });
        if (next is not null)
        {
            await write.RemoveWaiterAsync(resource.Key, next.ProcessId);
        }

        return next?.ProcessId;
    }

    /// <summary>
    /// Ожидающий, которому можно отдать ресурс: процесс идёт (с параллельными ветвями он «выполняется», пока другая ветвь
    /// работает) или ждёт. Остановленные (пауза, решение пользователя) очередь не задерживают, но место в ней сохраняют.
    /// </summary>
    private static bool IsActiveWaiter(ResourceWaiterRecord waiter) => waiter.ProcessStatus is ProcessStatus.Waiting or ProcessStatus.Running;

    /// <summary>
    /// Ждёт ли держатель — по цепочке «ждёт ресурс у…» и «ждёт завершения вызванного сценария…» — этот процесс.
    /// </summary>
    private static async Task<bool> WouldDeadlockAsync(IStoreWrite write, string processId, string holderId)
    {
        var waiters = await write.GetAllWaitersAsync();
        var resources = (await write.GetAllResourcesAsync()).ToDictionary(r => r.Key, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(holderId);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current == processId)
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            foreach (var waiting in waiters.Where(w => w.ProcessId == current))
            {
                if (resources.TryGetValue(waiting.ResourceKey, out var resource) && resource.HolderProcessId is { } next)
                {
                    pending.Push(next);
                }
            }

            foreach (var subscription in await write.GetSubscriptionsAsync(current))
            {
                if (subscription is { Purpose: SubscriptionPurpose.ChildProcess, State: SubscriptionState.Active, CorrelationKey: { } child })
                {
                    pending.Push(child);
                }
            }
        }

        return false;
    }

    /// <summary>Принудительно освободить ресурс (решение пользователя, например после ручной проверки).</summary>
    public async Task<ControlResult> ForceReleaseResourceAsync(string key, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var effects = new CommitEffects();
        string message;
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            var resource = await write.GetResourceAsync(key) ?? throw new EngineOperationException("Ресурс не найден.");
            if (resource.HolderProcessId is not { } holder)
            {
                throw new EngineOperationException("Ресурс и так свободен.");
            }

            var now = _time.GetUtcNow();
            var granted = await ReleaseAndGrantAsync(write, resource, now);
            if (!IsExternalHolder(holder))
            {
                await write.AddJournalAsync(Journal(holder, JournalLevel.Warning,
                    $"Ресурс «{key}» освобождён пользователем принудительно."));
                effects.Notify.Add(holder);
            }

            if (granted is not null)
            {
                effects.Enqueue.Add(granted);
            }

            await write.CommitAsync();
            message = granted is null
                ? $"Ресурс «{key}» освобождён."
                : $"Ресурс «{key}» освобождён и передан процессу #{granted[^6..]}.";
        }

        await ApplyEffectsAsync(effects);
        return new ControlResult(message);
    }

    // ───────────────────────────── Завершение процесса ─────────────────────────────

    /// <summary>
    /// Процесс завершён (успешно, с ошибкой или остановлен): освободить ресурсы, снять подписки и очередь,
    /// сообщить родительскому процессу, остановить дочерние.
    /// </summary>
    private async Task<IReadOnlyList<(JournalLevel, string)>> OnTerminalAsync(IStoreWrite write, ProcessRecord process,
        CommitEffects effects, DateTimeOffset now)
    {
        var journal = new List<(JournalLevel, string)>();
        foreach (var resource in await write.GetResourcesHeldByAsync(process.Id))
        {
            var granted = await ReleaseAndGrantAsync(write, resource, now);
            if (granted is not null)
            {
                effects.Enqueue.Add(granted);
            }

            journal.Add((JournalLevel.Info, $"Ресурс «{resource.Key}» освобождён при завершении процесса."));
        }

        await write.RemoveWaitersOfProcessAsync(process.Id);

        foreach (var subscription in (await write.GetSubscriptionsAsync(process.Id)).Where(s => s.State is SubscriptionState.Active or SubscriptionState.Fulfilled))
        {
            if (subscription.State == SubscriptionState.Active)
            {
                await write.TrySetSubscriptionStateAsync(subscription.Id, SubscriptionState.Active, SubscriptionState.Cancelled, null);

                // Сценарий, которого процесс ждёт («Вызвать сценарий»), — часть этого процесса и останавливается вместе с ним,
                // как бы процесс ни закончился (остановлен или завершён в другой параллельной ветви). Запущенные без ожидания
                // («Запустить сценарий») работают дальше независимо.
                if (subscription is { Purpose: SubscriptionPurpose.ChildProcess, CorrelationKey: { } childId })
                {
                    effects.CancelChildren.Add(childId);
                    journal.Add((JournalLevel.Warning, $"Вызванный сценарий (процесс #{childId[^Math.Min(6, childId.Length)..]}) остановлен: этот процесс завершился."));
                }
            }

            await ReturnMatchedEventsAsync(write, subscription.Id, $"Процесс #{process.Id[^6..]} завершился, не применив событие.", effects);
        }

        if (process.ParentProcessId is not null)
        {
            var status = process.Status switch
            {
                ProcessStatus.Completed => "completed",
                ProcessStatus.Cancelled => "cancelled",
                _ => "failed",
            };
            // Итог — настоящие значения (вызвавший работает с ними); секреты в нём событие помнит: при показе они скрыты, а
            // вызвавший процесс получает их как свои секретные.
            var definition = PepaJson.Deserialize<ScenarioDefinition>(process.DefinitionJson);
            var payload = new JsonObject
            {
                ["status"] = status,
                ["message"] = process.StatusMessage,
                ["variables"] = process.Variables.DeepClone(),
            };
            await write.TryInsertEventAsync(NewEventRecord(new EventInput
            {
                Source = EventSources.Platform,
                Type = "finished",
                ExternalId = $"finished:{process.Id}",
                CorrelationKey = process.Id,
                Payload = payload,
            }, now) with { Secrets = SecretsIn(RawSecretValues(process, definition), payload) is { Count: > 0 } carried ? carried : null });
            effects.RouteEvents = true;
        }

        return journal;
    }

    /// <summary>
    /// Назначенное, но не применённое событие возвращается на сопоставление — его может ждать другой процесс.
    /// Запуск по триггерам для него не повторяется: оно уже однажды было сопоставлено.
    /// </summary>
    private async Task ReturnMatchedEventsAsync(IStoreWrite write, long subscriptionId, string note, CommitEffects effects)
    {
        foreach (var record in await write.GetEventsForSubscriptionAsync(subscriptionId))
        {
            if (record.State == EventState.Matched && await write.TryUpdateEventAsync(record with
                {
                    State = EventState.Unmatched,
                    ProcessId = null,
                    SubscriptionId = null,
                    Note = note,
                    HandledAt = _time.GetUtcNow(),
                }, EventState.Matched))
            {
                effects.Rematch.Add(record.Id);
            }
        }
    }

    /// <summary>
    /// Шаг, который ждал событие, ресурс или вызванный сценарий, перезапускается или пропускается по решению пользователя:
    /// прежнее ожидание снимается, назначенное ему событие возвращается на сопоставление, место в очереди освобождается.
    /// </summary>
    private async Task ReleaseTokenWaitAsync(IStoreWrite write, string processId, TokenRecord token, CommitEffects effects)
    {
        switch (token.WaitKind)
        {
            case WaitKind.Event when long.TryParse(token.WaitRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var subscriptionId):
                if (await write.GetSubscriptionAsync(subscriptionId) is { } subscription)
                {
                    await write.TrySetSubscriptionStateAsync(subscription.Id, subscription.State, SubscriptionState.Cancelled, null);
                    await ReturnMatchedEventsAsync(write, subscription.Id,
                        $"Процесс #{processId[^6..]} перешёл к другому шагу, не применив событие.", effects);
                    if (subscription is { Purpose: SubscriptionPurpose.ChildProcess, State: SubscriptionState.Active, CorrelationKey: { } childId })
                    {
                        effects.CancelChildren.Add(childId);
                    }
                }

                break;
            case WaitKind.Resource when token.WaitRef is { } key:
                await write.RemoveTokenWaiterAsync(key, token.Id);
                break;
        }
    }

    // ───────────────────────────── Дочерние процессы ─────────────────────────────

    /// <summary>Проверенный сценарий и начальные данные процесса, готовые к записи.</summary>
    internal sealed record PreparedStart(StoredScenario Scenario, ScenarioDefinition Definition, JsonObject Variables, NodeDefinition Start)
    {
        /// <summary>Секретные значения, пришедшие с запуском (из вызвавшего процесса или события).</summary>
        public IReadOnlyList<string>? Secrets { get; init; }
    }

    private async Task<(PreparedStart? Prepared, NodeResult? Problem)> PrepareChildAsync(ProcessRecord parent, NodeResult.StartChild start)
    {
        if (parent.Depth + 1 > MaxDepth)
        {
            return (null, NodeResult.Fail(
                $"Слишком глубокая вложенность вызовов сценариев (больше {MaxDepth}). Возможно, сценарий вызывает сам себя.",
                retryable: false));
        }

        try
        {
            return (await PrepareStartAsync(start.ScenarioId, start.Inputs), null);
        }
        catch (ScenarioInvalidException invalid)
        {
            return (null, NodeResult.Fail(
                $"Вызываемый сценарий содержит ошибки ({invalid.Report.ErrorCount}) и не может быть запущен.", retryable: false));
        }
        catch (EngineOperationException problem)
        {
            return (null, NodeResult.Fail($"Не удалось запустить сценарий: {problem.Message}", retryable: false));
        }
    }

    private async Task<string> CreateChildAsync(IStoreWrite write, PreparedStart prepared, ProcessRecord parent, long stepId,
        DateTimeOffset now)
    {
        var (process, token) = NewProcess(InheritSecrets(parent, prepared), now, parent.Id, parent.Depth + 1, $"child:{parent.Id}:{stepId}");
        await write.InsertProcessAsync(process);
        await write.UpsertTokenAsync(token);
        await write.AddJournalAsync(Journal(process.Id, JournalLevel.Info,
            $"Процесс запущен из процесса #{parent.Id[^6..]} (сценарий «{parent.ScenarioName}»): сценарий «{prepared.Scenario.Name}», " +
            $"версия {prepared.Scenario.Version}."));
        return process.Id;
    }

    internal (ProcessRecord Process, TokenRecord Token) NewProcess(PreparedStart prepared, DateTimeOffset now,
        string? parentId, int depth, string? originKey)
    {
        var process = new ProcessRecord
        {
            Id = NewId(),
            ScenarioId = prepared.Scenario.Id,
            ScenarioName = prepared.Scenario.Name,
            ScenarioVersion = prepared.Scenario.Version,
            DefinitionJson = prepared.Scenario.DefinitionJson,
            Status = ProcessStatus.Running,
            Variables = (JsonObject)prepared.Variables.DeepClone(),
            StepBudget = _options.DefaultStepBudget,
            ParentProcessId = parentId,
            Depth = depth,
            OriginKey = originKey,
            CreatedAt = now,
            UpdatedAt = now,
            Secrets = Remember(null, [], prepared.Secrets ?? []),
        };
        var token = new TokenRecord
        {
            Id = NewId(),
            ProcessId = process.Id,
            NodeId = prepared.Start.Id,
            State = TokenState.Ready,
            CreatedAt = now,
        };
        return (process, token);
    }

    internal static JsonObject EventJson(EventRecord record) => new()
    {
        ["id"] = record.Id,
        ["source"] = record.Source,
        ["type"] = record.Type,
        ["correlationKey"] = record.CorrelationKey,
        ["externalId"] = record.ExternalId,
        ["payload"] = record.Payload?.DeepClone(),
        ["receivedAt"] = record.ReceivedAt.ToString("O"),
    };

    // ───────────────────────────── Восстановление ресурсов ─────────────────────────────

    /// <summary>
    /// После сбоя: ресурсы, которые держат завершённые или исчезнувшие процессы, освобождаются; очередь чистится от них же.
    /// Ресурсы приостановленных процессов остаются за ними — решение за пользователем.
    /// </summary>
    private async Task<int> ReconcileResourcesAsync(CancellationToken cancellationToken)
    {
        var released = 0;
        await using var write = await _store.BeginWriteAsync(cancellationToken);
        var now = _time.GetUtcNow();
        foreach (var resource in await write.GetAllResourcesAsync())
        {
            if (resource.HolderProcessId is not { } holder)
            {
                continue;
            }

            var process = await write.GetProcessAsync(holder);
            if (process is null || process.IsTerminal)
            {
                await write.UpsertResourceAsync(resource with { HolderProcessId = null, AcquiredAt = null, UpdatedAt = now });
                released++;
            }
        }

        foreach (var waiter in await write.GetAllWaitersAsync())
        {
            if (waiter.ProcessStatus is null or ProcessStatus.Completed or ProcessStatus.Failed or ProcessStatus.Cancelled)
            {
                await write.RemoveWaiterAsync(waiter.ResourceKey, waiter.ProcessId);
            }
        }

        await write.CommitAsync();
        return released;
    }
}
