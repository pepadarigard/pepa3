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
        public List<(string Source, DateTimeOffset Since)> Reclaim { get; } = [];
        public bool RouteEvents { get; set; }
    }

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

        foreach (var (source, since) in effects.Reclaim)
        {
            await ReclaimUnmatchedAsync(source, since);
        }

        if (effects.RouteEvents || effects.Reclaim.Count > 0)
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
                    return NodeResult.Ok(Ports.Out, EmptyUpdates, new JsonObject { ["resource"] = key },
                        $"Ресурс «{key}» уже занят этим процессом.");
                }

                if (resource?.HolderProcessId is null)
                {
                    // Свободен — но если в очереди уже есть активные ожидающие, честно встаём за ними.
                    var waiters = await write.GetWaitersAsync(key);
                    if (!waiters.Any(w => w.ProcessId != process.Id && w.ProcessStatus == ProcessStatus.Waiting))
                    {
                        await write.UpsertResourceAsync(new ResourceRecord
                        {
                            Key = key,
                            Title = acquire.Title ?? resource?.Title ?? key,
                            HolderProcessId = process.Id,
                            AcquiredAt = now,
                            UpdatedAt = now,
                        });
                        return NodeResult.Ok(Ports.Out, EmptyUpdates, new JsonObject { ["resource"] = key },
                            $"Ресурс «{key}» занят процессом.");
                    }
                }
                else if (await WouldDeadlockAsync(write, process.Id, resource.HolderProcessId))
                {
                    return NodeResult.Fail(
                        $"Взаимная блокировка: ресурс «{key}» занят процессом #{resource.HolderProcessId[^6..]}, " +
                        "который сам ждёт ресурс этого процесса. Освободите ресурс или измените порядок.", retryable: false);
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

                var id = await write.TryInsertEventAsync(NewEventRecord(input, now));
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

            default:
                return result;
        }
    }

    internal static EventRecord NewEventRecord(EventInput input, DateTimeOffset now) => new()
    {
        Source = input.Source.Trim(),
        Type = input.Type?.Trim() ?? "",
        ExternalId = string.IsNullOrWhiteSpace(input.ExternalId) ? null : input.ExternalId.Trim(),
        CorrelationKey = string.IsNullOrEmpty(input.CorrelationKey) ? null : input.CorrelationKey,
        Payload = input.Payload?.DeepClone(),
        ReceivedAt = now,
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

            return await ApplyResultAsync(process, graph, token, node, descriptor, step, result, null,
                extraWrites: async (write, _) => await write.UpdateEventAsync(record with
                {
                    State = EventState.Delivered,
                    ProcessId = process.Id,
                    SubscriptionId = subscription.Id,
                    HandledAt = now,
                }));
        }

        if (subscription.State == SubscriptionState.Active && token.WakeAt is { } wakeAt && wakeAt <= now)
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

    private async Task<bool> TryResolveResourceWaitAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token,
        NodeDefinition node, NodeTypeDescriptor descriptor, StepRecord step, DateTimeOffset now)
    {
        var key = token.WaitRef;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        return await ApplyResultAsync(process, graph, token, node, descriptor, step, NodeResult.Ok(), null, decide: async write =>
        {
            var resource = await write.GetResourceAsync(key);
            if (resource?.HolderProcessId == process.Id)
            {
                await write.RemoveWaiterAsync(key, process.Id);
                return NodeResult.Ok(Ports.Out, EmptyUpdates, new JsonObject { ["resource"] = key },
                    $"Ресурс «{key}» получен из очереди.");
            }

            if (resource is null || resource.HolderProcessId is null)
            {
                var first = (await write.GetWaitersAsync(key))
                    .FirstOrDefault(w => w.ProcessId == process.Id || w.ProcessStatus == ProcessStatus.Waiting);
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
                    return NodeResult.Ok(Ports.Out, EmptyUpdates, new JsonObject { ["resource"] = key },
                        $"Ресурс «{key}» освободился и занят процессом.");
                }
            }

            if (token.WakeAt is { } wakeAt && wakeAt <= now)
            {
                await write.RemoveWaiterAsync(key, process.Id);
                return NodeResult.Ok(Ports.Timeout, $"Ресурс «{key}» не освободился до {FormatLocal(wakeAt)} — тайм-аут.");
            }

            return null;
        });
    }

    // ───────────────────────────── Ресурсы ─────────────────────────────

    /// <summary>Освободить ресурс и сразу передать первому в очереди активному процессу. Возвращает получателя.</summary>
    private static async Task<string?> ReleaseAndGrantAsync(IStoreWrite write, ResourceRecord resource, DateTimeOffset now)
    {
        var next = (await write.GetWaitersAsync(resource.Key)).FirstOrDefault(w => w.ProcessStatus == ProcessStatus.Waiting);
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

    /// <summary>Ждёт ли держатель (по цепочке) ресурс, занятый этим процессом.</summary>
    private static async Task<bool> WouldDeadlockAsync(IStoreWrite write, string processId, string holderId)
    {
        var waiters = await write.GetAllWaitersAsync();
        var resources = (await write.GetAllResourcesAsync()).ToDictionary(r => r.Key, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = holderId;
        while (visited.Add(current))
        {
            if (current == processId)
            {
                return true;
            }

            var waiting = waiters.FirstOrDefault(w => w.ProcessId == current);
            if (waiting is null || !resources.TryGetValue(waiting.ResourceKey, out var resource) || resource.HolderProcessId is null)
            {
                return false;
            }

            current = resource.HolderProcessId;
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
            await write.AddJournalAsync(Journal(holder, JournalLevel.Warning,
                $"Ресурс «{key}» освобождён пользователем принудительно."));
            if (granted is not null)
            {
                effects.Enqueue.Add(granted);
            }

            effects.Notify.Add(holder);
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
            }

            // Назначенное, но не применённое событие возвращается на сопоставление — его может ждать другой процесс.
            foreach (var record in await write.GetEventsForSubscriptionAsync(subscription.Id))
            {
                if (record.State == EventState.Matched)
                {
                    await write.UpdateEventAsync(record with
                    {
                        State = EventState.Received,
                        ProcessId = null,
                        SubscriptionId = null,
                        Note = $"Процесс #{process.Id[^6..]} завершился, не применив событие.",
                    });
                    effects.RouteEvents = true;
                }
            }
        }

        if (process.ParentProcessId is not null)
        {
            var status = process.Status switch
            {
                ProcessStatus.Completed => "completed",
                ProcessStatus.Cancelled => "cancelled",
                _ => "failed",
            };
            await write.TryInsertEventAsync(NewEventRecord(new EventInput
            {
                Source = EventSources.Platform,
                Type = "finished",
                ExternalId = $"finished:{process.Id}",
                CorrelationKey = process.Id,
                Payload = new JsonObject
                {
                    ["status"] = status,
                    ["message"] = process.StatusMessage,
                    ["variables"] = process.Variables.DeepClone(),
                },
            }, now));
            effects.RouteEvents = true;
        }

        return journal;
    }

    // ───────────────────────────── Дочерние процессы ─────────────────────────────

    /// <summary>Проверенный сценарий и начальные данные процесса, готовые к записи.</summary>
    internal sealed record PreparedStart(StoredScenario Scenario, ScenarioDefinition Definition, JsonObject Variables, NodeDefinition Start);

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
        var (process, token) = NewProcess(prepared, now, parent.Id, parent.Depth + 1, $"child:{parent.Id}:{stepId}");
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
