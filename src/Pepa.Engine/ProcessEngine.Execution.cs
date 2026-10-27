using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pepa.Core;
using Pepa.Core.Expressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Validation;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

public sealed partial class ProcessEngine
{
    private async Task RunProcessAsync(string processId, CancellationToken stop)
    {
        var slot = Slot(processId);
        if (slot.Faulted || !slot.TryEnter())
        {
            return;
        }

        var requeue = false;
        try
        {
            requeue = await RunSliceAsync(processId, slot, stop);
        }
        catch (Exception ex)
        {
            // Сюда попадают только сбои платформы (чаще всего — хранилища), а не ошибки действий:
            // их исполнитель уже превратил в результат шага.
            _logger.LogError(ex, "Сбой при выполнении процесса {ProcessId}.", processId);
            slot.Faulted = !await TryMarkEngineErrorAsync(processId, ex);
        }
        finally
        {
            slot.Exit();
        }

        if (requeue || slot.Pending != PendingRequest.None)
        {
            Enqueue(processId);
        }
    }

    /// <returns>true — процесс не закончил работу и должен встать в очередь снова.</returns>
    private async Task<bool> RunSliceAsync(string processId, ProcessSlot slot, CancellationToken stop)
    {
        string? lastToken = null;
        var checkWaits = false;
        for (var i = 0; i < _options.StepsPerSlice; i++)
        {
            var process = await _store.GetProcessAsync(processId);
            if (process is null || process.IsTerminal)
            {
                return false;
            }

            switch (slot.TakePending())
            {
                case PendingRequest.Cancel:
                    await CancelCoreAsync(process);
                    return false;
                case PendingRequest.Pause when process.Status is ProcessStatus.Running or ProcessStatus.Waiting:
                    await PauseCoreAsync(process, PauseReason.User);
                    return false;
            }

            if (process.Status is not (ProcessStatus.Running or ProcessStatus.Waiting) || stop.IsCancellationRequested)
            {
                return false;
            }

            var tokens = await _store.GetTokensAsync(processId);
            var now = _time.GetUtcNow();
            // Ветви — по очереди: следующая после той, что выполнялась шагом раньше, — занятая ветвь не задерживает другие.
            var due = tokens.Where(t => t.State == TokenState.Ready ||
                                        (t is { State: TokenState.Waiting, WaitKind: WaitKind.Timer or WaitKind.Retry } && t.WakeAt <= now))
                .OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
            var token = due.FirstOrDefault(t => lastToken is not null && string.CompareOrdinal(t.Id, lastToken) > 0) ?? due.FirstOrDefault();
            if (token is null || checkWaits)
            {
                // Ожидание события или ресурса: проверить, не пришло ли то, чего ждём (или тайм-аут). Пока другие ветви
                // работают — после каждого их шага, чтобы ответ или тайм-аут не ждали, пока ветвь освободится.
                checkWaits = false;
                var resolved = false;
                foreach (var waiting in tokens.Where(t => t is { State: TokenState.Waiting, WaitKind: WaitKind.Event or WaitKind.Resource }))
                {
                    if (await TryResolveWaitAsync(process, Graph(process), waiting, now))
                    {
                        resolved = true;
                        break;
                    }
                }

                if (resolved)
                {
                    continue;
                }
            }

            if (token is null)
            {
                if (tokens.All(t => t.State == TokenState.Completed))
                {
                    await SetAttentionAsync(process, AttentionKind.EngineError, "У процесса нет точки, с которой можно продолжить.", null);
                }
                else if (tokens.Where(t => t.State != TokenState.Completed).All(t => t.WaitKind == WaitKind.Join))
                {
                    // Все ветви ждут слияния, а прийти больше некому — иначе процесс ждал бы вечно.
                    await SetAttentionAsync(process, AttentionKind.EngineError,
                        "Ветви ждут слияния, но остальные ветви уже не придут. Остановите процесс и проверьте схему.", null);
                }
                else if (process.Status == ProcessStatus.Running)
                {
                    await UpdateProcessAsync(process with { Status = ProcessStatus.Waiting, UpdatedAt = now });
                }

                return false;
            }

            if (process.StepCount >= process.StepBudget)
            {
                await SetAttentionAsync(process, AttentionKind.StepLimit,
                    $"Процесс выполнил {process.StepCount} шагов. Возможно, сценарий зациклился. " +
                    "Проверьте журнал и решите: продолжить или остановить.", null);
                return false;
            }

            await ExecuteTokenAsync(process, Graph(process), token, slot);
            lastToken = token.Id;
            checkWaits = tokens.Count > 1;
        }

        return true;
    }

    private async Task ExecuteTokenAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token, ProcessSlot slot)
    {
        var node = graph.FindNode(token.NodeId);
        if (node is null)
        {
            await SetAttentionAsync(process, AttentionKind.EngineError, $"Блок «{token.NodeId}» не найден в сценарии.", null, token.Id);
            return;
        }

        if (!_registry.TryGet(node.Type, out var executor))
        {
            await SetAttentionAsync(process, AttentionKind.EngineError,
                $"Тип блока «{node.Type}» недоступен в этой версии Pepa.", null, token.Id);
            return;
        }

        var descriptor = executor.Descriptor;

        if (token is { State: TokenState.Waiting, WaitKind: WaitKind.Timer, WaitingStepId: { } waitingStepId })
        {
            await CompleteWaitAsync(process, graph, token, node, descriptor, waitingStepId);
            return;
        }

        if (BreakpointBefore(process, token, node, descriptor) is { } marker)
        {
            await PauseAtBreakpointAsync(process, node, descriptor, marker);
            return;
        }

        var plannedAt = _time.GetUtcNow();
        JsonObject input;
        try
        {
            input = InputResolver.Resolve(node, descriptor, Scope(process), _expressions);
        }
        catch (Exception ex) when (ex is ExpressionException or NodeInputException)
        {
            // Параметры не вычислились — действие заведомо не выполнялось.
            var notStarted = NewStep(process, token, node, descriptor, null, StepState.Planned, plannedAt);
            await ApplyResultAsync(process, graph, token, node, descriptor, notStarted,
                NodeResult.Fail($"Не удалось подготовить параметры: {ex.Message}", retryable: false), null);
            return;
        }

        // Блок сам занимает ресурс (например, профиль браузера): сначала ресурс — сразу или в очереди, — затем действие.
        if (executor.RequiredResource(input) is { } need && need.Key.Trim() is { Length: > 0 } resourceKey &&
            !await HoldsResourceAsync(process.Id, resourceKey))
        {
            var resourceStep = NewStep(process, token, node, descriptor, new JsonObject { ["resource"] = resourceKey },
                StepState.Planned, plannedAt) with { StartedAt = plannedAt, Effect = NodeEffect.None };
            await ApplyResultAsync(process, graph, token, node, descriptor, resourceStep,
                new NodeResult.AcquireResource(resourceKey, need.Title, null) { RepeatNode = true }, null);
            return;
        }

        var context = new NodeExecutionContext
        {
            ProcessId = process.Id,
            Node = node,
            Input = input,
            Variables = (JsonObject)process.Variables.DeepClone(),
            Attempt = token.Attempt,
            Time = _time,
            Memory = process.Memory?[node.Id]?.DeepClone() as JsonObject,
            Graph = graph,
            EnteredFrom = token.FromNodeId,
        };

        if (descriptor.Effect == NodeEffect.None)
        {
            await ExecutePureAsync(process, graph, token, node, executor, context, plannedAt);
        }
        else
        {
            await ExecuteActionAsync(process, graph, token, node, executor, context, slot, plannedAt);
        }
    }

    private async Task ExecutePureAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token,
        NodeDefinition node, INodeExecutor executor, NodeExecutionContext context, DateTimeOffset plannedAt)
    {
        NodeResult result;
        try
        {
            result = await executor.ExecuteAsync(context, CancellationToken.None);
        }
        catch (Exception ex)
        {
            result = NodeResult.Fail(ex.Message, retryable: false);
        }

        var step = NewStep(process, token, node, executor.Descriptor, context.Input, StepState.Planned, plannedAt)
            with { StartedAt = plannedAt };
        await ApplyResultAsync(process, graph, token, node, executor.Descriptor, step, result, null);
    }

    /// <summary>
    /// Шаг с вводом-выводом: «запланирован» → снимок «до» → «выполняется» (запись на диск ДО действия)
    /// → действие → при неизвестном итоге сразу проверка → итог одной транзакцией.
    /// </summary>
    private async Task ExecuteActionAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token,
        NodeDefinition node, INodeExecutor executor, NodeExecutionContext context, ProcessSlot slot,
        DateTimeOffset plannedAt)
    {
        var descriptor = executor.Descriptor;
        var previousCheckpoint = token.Attempt > 1 && await _store.GetLastCheckpointStepAsync(process.Id, token.Id, node.Id) is { Checkpoint: { } previous }
            ? (JsonObject)previous.DeepClone()
            : null;

        // 1. Запланирован.
        var step = NewStep(process, token, node, descriptor, context.Input, StepState.Planned, plannedAt);
        process = process with
        {
            Status = ProcessStatus.Running,
            StepCount = process.StepCount + 1,
            UpdatedAt = plannedAt,
        };
        token = token with { State = TokenState.Ready, WaitKind = WaitKind.None, WakeAt = null, WaitingStepId = null };
        await using (var write = await _store.BeginWriteAsync())
        {
            step = step with { Id = await write.InsertStepAsync(step) };
            await write.UpsertTokenAsync(token);
            await write.UpdateProcessAsync(process);
            await write.CommitAsync();
        }

        Notify(process.Id);

        // 2. Снимок состояния до действия (только чтение). При повторе блок видит снимок прошлой попытки.
        if (previousCheckpoint is not null)
        {
            context = context with { PreviousCheckpoint = previousCheckpoint };
        }

        JsonObject? checkpoint;
        try
        {
            using var prepareTimeout = new CancellationTokenSource(_options.DefaultActionTimeout, _time);
            checkpoint = await executor.PrepareAsync(context, prepareTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            await ApplyResultAsync(process, graph, token, node, descriptor, step,
                NodeResult.Fail($"Не удалось подготовить действие за {FormatDuration(_options.DefaultActionTimeout)} — действие не выполнялось."), null);
            return;
        }
        catch (Exception ex)
        {
            await ApplyResultAsync(process, graph, token, node, descriptor, step,
                NodeResult.Fail($"Не удалось подготовить действие: {ex.Message}",
                    retryable: ex is not NodeInputException), null);
            return;
        }

        // 3. Выполняется — запись ДО вызова внешнего действия.
        step = step with { State = StepState.Running, StartedAt = _time.GetUtcNow(), Checkpoint = checkpoint };
        await using (var write = await _store.BeginWriteAsync())
        {
            await write.UpdateStepAsync(step);
            await write.CommitAsync();
        }

        Notify(process.Id);

        // 4. Действие.
        var execution = context with { Checkpoint = checkpoint };
        var timeout = node.Settings.TimeoutSeconds is { } seconds
            ? TimeSpan.FromSeconds(seconds)
            : executor.ExpectedDuration(context.Input) is { } expected && expected + TimeSpan.FromSeconds(30) > _options.DefaultActionTimeout
                ? expected + TimeSpan.FromSeconds(30)
                : _options.DefaultActionTimeout;
        var stepCancellation = slot.BeginStep();
        using var timeoutCancellation = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stepCancellation.Token, timeoutCancellation.Token);
        Interlocked.Increment(ref _executingSteps);
        NodeResult result;
        try
        {
            result = await executor.ExecuteAsync(execution, linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            result = new NodeResult.Unknown(
                $"Действие не завершилось за {FormatDuration(timeout)} — неизвестно, успело ли оно выполниться.");
        }
        catch (OperationCanceledException) when (stepCancellation.IsCancellationRequested)
        {
            result = new NodeResult.Unknown("Действие прервано остановкой процесса — неизвестно, успело ли оно выполниться.");
        }
        catch (NodeInputException ex)
        {
            // По контракту такие ошибки возникают до какого-либо внешнего эффекта.
            result = NodeResult.Fail(ex.Message, retryable: false);
        }
        catch (Exception ex)
        {
            result = new NodeResult.Unknown($"Непредвиденная ошибка во время действия: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _executingSteps);
            slot.EndStep(stepCancellation);
        }

        // Действие только читало внешнее состояние — ничего не изменилось, итог не может быть «неизвестен».
        if (result is NodeResult.Unknown readOnlyUnknown && descriptor.Effect == NodeEffect.ReadOnly)
        {
            result = NodeResult.Fail(readOnlyUnknown.Reason);
        }

        // 5. Неизвестный итог — сверка с внешней системой (это проверка, а не повтор действия).
        string? resolution = null;
        if (result is NodeResult.Unknown { AlreadyChecked: false } unknown && descriptor.SupportsVerification)
        {
            var verification = await SafeVerifyAsync(executor, execution);
            switch (verification.Outcome)
            {
                case VerificationOutcome.Done:
                    result = NodeResult.Ok(verification.Port, verification.VariableUpdates ?? EmptyUpdates, verification.Output);
                    resolution = $"Итог был неизвестен ({unknown.Reason}). Проверка: {verification.Message}";
                    break;
                case VerificationOutcome.NotDone:
                    result = NodeResult.Fail($"{unknown.Reason} Проверка: {verification.Message}");
                    resolution = "Проверено: действие не выполнено.";
                    break;
                default:
                    result = new NodeResult.Unknown($"{unknown.Reason} {verification.Message}");
                    break;
            }
        }

        // 6. Итог.
        await ApplyResultAsync(process, graph, token, node, descriptor, step, result, resolution);
    }

    private async Task CompleteWaitAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token,
        NodeDefinition node, NodeTypeDescriptor descriptor, long waitingStepId)
    {
        var step = await _store.GetStepAsync(waitingStepId);
        if (step is null)
        {
            await SetAttentionAsync(process, AttentionKind.EngineError, "Не найден шаг ожидания.", null);
            return;
        }

        var now = _time.GetUtcNow();
        var late = token.WakeAt is { } wakeAt ? now - wakeAt : TimeSpan.Zero;
        var message = late > TimeSpan.FromMinutes(1)
            ? $"Ожидание завершено (на {FormatDuration(late)} позже срока: процесс был приостановлен или приложение закрыто)."
            : null;
        await ApplyResultAsync(process, graph, token, node, descriptor, step, NodeResult.Ok(Ports.Out, message), null);
    }

    /// <summary>
    /// Применяет итог шага одной транзакцией: состояние шага, переменные, переход маркера, состояние процесса,
    /// действия платформы (подписки, ресурсы, дочерние процессы, события) и журнал.
    /// </summary>
    /// <param name="decide">
    /// Необязательное решение внутри транзакции (например, успел ли прийти ресурс). Если возвращает null —
    /// ничего не записывается, и метод возвращает false.
    /// </param>
    private async Task<bool> ApplyResultAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token,
        NodeDefinition node, NodeTypeDescriptor descriptor, StepRecord step, NodeResult result, string? resolution,
        StepState successState = StepState.Succeeded, Func<IStoreWrite, Task<NodeResult?>>? decide = null,
        Func<IStoreWrite, long, NodeResult, CommitEffects, Task>? extraWrites = null)
    {
        var effects = new CommitEffects();
        var committed = await CommitResultAsync(process, graph, token, node, descriptor, step, result, resolution, successState,
            decide, extraWrites, effects);
        if (committed is null)
        {
            return false;
        }

        // Только после того, как транзакция закрыта: последствия сами пишут в хранилище.
        AfterCommit(committed);
        await ApplyEffectsAsync(effects);
        return true;
    }

    private async Task<ProcessRecord?> CommitResultAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token,
        NodeDefinition node, NodeTypeDescriptor descriptor, StepRecord step, NodeResult result, string? resolution,
        StepState successState, Func<IStoreWrite, Task<NodeResult?>>? decide, Func<IStoreWrite, long, NodeResult, CommitEffects, Task>? extraWrites,
        CommitEffects effects)
    {
        var label = Label(node, descriptor);

        // Подготовка вне транзакции: дочернему процессу нужен проверенный сценарий.
        PreparedStart? child = null;
        if (result is NodeResult.StartChild start)
        {
            (child, var problem) = await PrepareChildAsync(process, start);
            if (problem is not null)
            {
                result = problem;
            }
        }

        await using var write = await _store.BeginWriteAsync();

        // Время — после получения права записи: подписка и событие, записанные позже, не должны получить более раннее время.
        var now = _time.GetUtcNow();
        if (decide is not null)
        {
            var decided = await decide(write);
            if (decided is null)
            {
                return null;
            }

            result = decided;
        }

        // Секреты — до проверок: сообщение о неверном типе содержит присвоенное значение.
        var secrets = SecretValues(process, graph.Definition, result);
        result = CheckVariableTypes(result, graph.Definition, descriptor);
        result = await ResolveIntentAsync(write, process, token, label, result, effects, now);

        // Необязательный выход (например, «Тайм-аут») не подключён — процессу некуда идти: это невыполненный шаг.
        if (result is NodeResult.Success { Port: var port } routed && descriptor.FindOutput(port) is { Kind: PortKind.Optional } optional &&
            graph.Next(node.Id, port) is null)
        {
            result = NodeResult.Fail(
                (routed.Message is { Length: > 0 } reason ? reason + " " : "") + $"Выход «{optional.Label}» не подключён.",
                retryable: false);
        }

        // Тексты для истории — без значений секретных переменных (и тех, что шаг им присваивает), уже после всех проверок:
        // их сообщения тоже могут содержать значение. Параметры шага (Input) остаются настоящими: по ним проверяется
        // результат после перезапуска; при показе их скрывает SecretMasker.
        result = Redact(result, secrets);
        resolution = Redact(resolution, secrets);

        // Итоговые поля шага.
        var finalStep = step with { FinishedAt = now, Resolution = resolution ?? step.Resolution };
        switch (result)
        {
            case NodeResult.Success ok:
                finalStep = finalStep with { State = successState, Port = ok.Port, Output = BuildOutput(ok), Error = null };
                break;
            case NodeResult.Wait wait:
                finalStep = finalStep with
                {
                    State = StepState.Waiting,
                    FinishedAt = null,
                    Output = new JsonObject { ["until"] = wait.Until.ToString("O") },
                };
                break;
            case NodeResult.WaitForEvent waitEvent:
                finalStep = finalStep with { State = StepState.Waiting, FinishedAt = null, Output = DescribeWait(waitEvent) };
                break;
            case NodeResult.AcquireResource acquire:
                finalStep = finalStep with
                {
                    State = StepState.Waiting,
                    FinishedAt = null,
                    Output = new JsonObject
                    {
                        ["resource"] = acquire.Key,
                        ["until"] = acquire.TimeoutAt?.ToString("O"),
                        ["repeat"] = acquire.RepeatNode ? true : null,
                    },
                };
                break;
            case NodeResult.RunAgain:
                finalStep = finalStep with { State = StepState.Succeeded, Port = null, Error = null };
                break;
            case NodeResult.StartChild { WaitForCompletion: true }:
                finalStep = finalStep with { State = StepState.Waiting, FinishedAt = null };
                break;
            case NodeResult.StartChild:
                finalStep = finalStep with { State = successState, Port = Ports.Out };
                break;
            case NodeResult.Finish:
                finalStep = finalStep with { State = StepState.Succeeded, Port = null };
                break;
            case NodeResult.Split split:
                finalStep = finalStep with
                {
                    State = successState,
                    Port = null,
                    Output = new JsonObject { ["branches"] = new JsonArray(split.Ports.Select(port => (JsonNode)port).ToArray()) },
                };
                break;
            case NodeResult.Join:
                finalStep = finalStep with { State = successState, Port = Ports.Out };
                break;
            case NodeResult.Failure failure:
                finalStep = finalStep with { State = StepState.Failed, Error = failure.Error };
                break;
            case NodeResult.Unknown unknown:
                finalStep = finalStep with { State = StepState.Unknown, Error = unknown.Reason };
                break;
        }

        var isNew = step.Id == 0;
        var p = process with { UpdatedAt = now, StepCount = isNew ? process.StepCount + 1 : process.StepCount };
        var t = token;
        var journal = new List<(JournalLevel Level, string Message)>();

        long stepId;
        if (isNew)
        {
            stepId = await write.InsertStepAsync(finalStep);
        }
        else
        {
            stepId = finalStep.Id;
            await write.UpdateStepAsync(finalStep);
        }

        if (extraWrites is not null)
        {
            await extraWrites(write, stepId, result, effects);
        }

        // Дочерний процесс создаётся в той же транзакции: он начнёт работу только после её подтверждения.
        string? childId = null;
        if (result is NodeResult.StartChild startChild && child is not null)
        {
            childId = await CreateChildAsync(write, child, process, stepId, now);
            effects.Enqueue.Add(childId);
            if (startChild.WaitForCompletion)
            {
                await write.UpdateStepAsync(finalStep with
                {
                    Id = stepId,
                    Output = new JsonObject { ["childProcessId"] = childId, ["scenario"] = child.Scenario.Name },
                });
                result = new NodeResult.WaitForEvent(new EventSubscriptionSpec
                {
                    Source = EventSources.Platform,
                    EventType = "finished",
                    CorrelationKey = childId,
                    SaveTo = startChild.SaveTo,
                    Purpose = SubscriptionPurpose.ChildProcess,
                }, startChild.TimeoutAt);
                journal.Add((JournalLevel.Info,
                    $"Запущен сценарий «{child.Scenario.Name}» (процесс #{childId[^6..]}), ожидание его завершения."));
            }
            else
            {
                var updates = new Dictionary<string, JsonNode?>();
                if (!string.IsNullOrEmpty(startChild.SaveTo))
                {
                    updates[startChild.SaveTo] = childId;
                }

                await write.UpdateStepAsync(finalStep with
                {
                    Id = stepId,
                    Output = new JsonObject { ["childProcessId"] = childId, ["scenario"] = child.Scenario.Name },
                });
                result = NodeResult.Ok(Ports.Out, updates, null,
                    $"Запущен сценарий «{child.Scenario.Name}» (процесс #{childId[^6..]}).");
            }
        }

        switch (result)
        {
            case NodeResult.Success ok:
            {
                var edge = graph.Next(node.Id, ok.Port);
                if (edge is null)
                {
                    p = Attention(p, AttentionKind.EngineError, $"У блока {label} не подключён выход «{ok.Port}».", null, t.Id);
                    journal.Add((JournalLevel.Error, p.StatusMessage!));
                    break;
                }

                p = Cleared(p) with
                {
                    Variables = ApplyUpdates(p.Variables, ok.VariableUpdates),
                    Memory = ok.ReplaceMemory ? WithMemory(p.Memory, node.Id, ok.Memory) : p.Memory,
                    Status = ProcessStatus.Running,
                };
                t = Moved(t, edge.Target);
                if (!string.IsNullOrWhiteSpace(ok.Message))
                {
                    journal.Add((ok.Level == MessageLevel.Warning ? JournalLevel.Warning : JournalLevel.Info, ok.Message));
                }

                break;
            }

            case NodeResult.RunAgain again:
                p = Cleared(p) with { Status = ProcessStatus.Running };
                t = t with { State = TokenState.Ready, Attempt = 1, WaitKind = WaitKind.None, WakeAt = null, WaitingStepId = null, WaitRef = null };
                if (!string.IsNullOrWhiteSpace(again.Message))
                {
                    journal.Add((JournalLevel.Info, again.Message));
                }

                break;

            case NodeResult.Wait wait:
                p = Cleared(p) with { Status = ProcessStatus.Waiting };
                t = t with { State = TokenState.Waiting, WaitKind = WaitKind.Timer, WakeAt = wait.Until, WaitingStepId = stepId, WaitRef = null };
                journal.Add((JournalLevel.Info, wait.Message ?? $"Ожидание до {FormatLocal(wait.Until)}."));
                break;

            case NodeResult.WaitForEvent waitEvent:
            {
                var spec = waitEvent.Subscription;
                var subscriptionId = await write.InsertSubscriptionAsync(new SubscriptionRecord
                {
                    ProcessId = p.Id,
                    TokenId = t.Id,
                    StepId = stepId,
                    NodeId = node.Id,
                    Source = NormalizeSource(spec.Source),
                    EventType = string.IsNullOrWhiteSpace(spec.EventType) ? null : spec.EventType,
                    CorrelationKey = string.IsNullOrEmpty(spec.CorrelationKey) ? null : spec.CorrelationKey,
                    MatchExpression = string.IsNullOrWhiteSpace(spec.MatchExpression) ? null : spec.MatchExpression,
                    Priority = spec.Priority,
                    LookbackSeconds = Math.Max(0, spec.LookbackSeconds),
                    SaveTo = string.IsNullOrEmpty(spec.SaveTo) ? null : spec.SaveTo,
                    Purpose = spec.Purpose,
                    State = SubscriptionState.Active,
                    CreatedAt = now,
                    ExpiresAt = waitEvent.TimeoutAt,
                });
                p = Cleared(p) with { Status = ProcessStatus.Waiting };
                t = t with
                {
                    State = TokenState.Waiting, WaitKind = WaitKind.Event, WakeAt = waitEvent.TimeoutAt, WaitingStepId = stepId,
                    WaitRef = subscriptionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (spec.Purpose == SubscriptionPurpose.Event)
                {
                    journal.Add((JournalLevel.Info, $"Ожидание события {DescribeSpec(spec)}" +
                                                    (waitEvent.TimeoutAt is { } until ? $" до {FormatLocal(until)}." : ".")));
                }

                effects.Reclaim.Add(new ReclaimRequest(NormalizeSource(spec.Source),
                    string.IsNullOrWhiteSpace(spec.EventType) ? null : spec.EventType,
                    string.IsNullOrEmpty(spec.CorrelationKey) ? null : spec.CorrelationKey,
                    now - TimeSpan.FromSeconds(Math.Max(0, spec.LookbackSeconds))));
                break;
            }

            case NodeResult.AcquireResource acquire:
            {
                await write.AddWaiterAsync(new ResourceWaiterRecord
                {
                    ResourceKey = acquire.Key,
                    ProcessId = p.Id,
                    TokenId = t.Id,
                    RequestedAt = now,
                });
                var holder = (await write.GetResourceAsync(acquire.Key))?.HolderProcessId;
                p = Cleared(p) with { Status = ProcessStatus.Waiting };
                t = t with
                {
                    State = TokenState.Waiting, WaitKind = WaitKind.Resource, WakeAt = acquire.TimeoutAt, WaitingStepId = stepId,
                    WaitRef = acquire.Key,
                };
                journal.Add((JournalLevel.Info,
                    $"Ресурс «{acquire.Key}» занят" +
                    (ExternalHolderTitle(holder) is { } by ? $" ({by})" : holder is null ? "" : $" процессом #{holder[^Math.Min(6, holder.Length)..]}") +
                    " — процесс встал в очередь" + (acquire.TimeoutAt is { } until ? $" (не дольше чем до {FormatLocal(until)})." : ".")));
                break;
            }

            case NodeResult.Split split:
            {
                var branches = split.Ports.Select(port => graph.Next(node.Id, port)).ToList();
                if (branches.Count == 0 || branches.Any(e => e is null))
                {
                    p = Attention(p, AttentionKind.EngineError, $"У блока {label} не подключена ветвь.", null, t.Id);
                    journal.Add((JournalLevel.Error, p.StatusMessage!));
                    break;
                }

                // Участок: «id:число ветвей»; вложенные участки — через «/». Слияние ждёт ветви своего участка.
                var frame = $"{NewId()}:{branches.Count}";
                var fork = t.Fork is null ? frame : $"{t.Fork}/{frame}";
                p = Cleared(p) with { Status = ProcessStatus.Running };
                t = Moved(t, branches[0]!.Target) with { Fork = fork };
                foreach (var branch in branches.Skip(1))
                {
                    await write.UpsertTokenAsync(new TokenRecord
                    {
                        Id = NewId(),
                        ProcessId = p.Id,
                        NodeId = branch!.Target,
                        State = TokenState.Ready,
                        FromNodeId = node.Id,
                        Fork = fork,
                        CreatedAt = now,
                    });
                }

                journal.Add((JournalLevel.Info, $"Процесс разделился на параллельные ветви: {branches.Count}."));
                break;
            }

            case NodeResult.Join:
            {
                var edge = graph.Next(node.Id, Ports.Out);
                var (frame, outer) = TopFork(t.Fork);
                if (frame is not { } top || edge is null)
                {
                    p = Attention(p, AttentionKind.EngineError,
                        edge is null ? $"У блока {label} не подключён выход «Далее»." : $"К слиянию {label} пришла ветвь не из параллельного участка.", null, t.Id);
                    journal.Add((JournalLevel.Error, p.StatusMessage!));
                    break;
                }

                var arrived = (await write.GetTokensAsync(p.Id))
                    .Where(o => o.Id != t.Id && o is { State: TokenState.Waiting, WaitKind: WaitKind.Join } && o.WaitRef == top.Id)
                    .ToList();
                p = Cleared(p) with { Status = ProcessStatus.Running };
                if (arrived.Count + 1 < top.Count)
                {
                    t = t with { State = TokenState.Waiting, WaitKind = WaitKind.Join, WakeAt = null, WaitingStepId = null, WaitRef = top.Id };
                    journal.Add((JournalLevel.Info, $"Ветвь пришла к слиянию {label} ({arrived.Count + 1} из {top.Count}) — ждём остальные."));
                }
                else
                {
                    foreach (var other in arrived)
                    {
                        await write.UpsertTokenAsync(other with { State = TokenState.Completed, WaitKind = WaitKind.None, WaitRef = null });
                    }

                    t = Moved(t, edge.Target) with { Fork = outer };
                    journal.Add((JournalLevel.Info, $"Все ветви ({top.Count}) пришли к слиянию {label} — процесс продолжается одной ветвью."));
                }

                break;
            }

            case NodeResult.Finish finish:
                var finishMessage = finish.Message ?? (finish.Succeeded ? "Процесс завершён успешно." : "Процесс завершён с ошибкой.");
                p = Cleared(p) with
                {
                    Status = finish.Succeeded ? ProcessStatus.Completed : ProcessStatus.Failed,
                    StatusMessage = finishMessage,
                    FinishedAt = now,
                };
                t = t with { State = TokenState.Completed, WaitKind = WaitKind.None, WakeAt = null, WaitingStepId = null, WaitRef = null };
                journal.Add((finish.Succeeded ? JournalLevel.Info : JournalLevel.Error, finishMessage));
                break;

            case NodeResult.Failure failure:
            {
                p = p with
                {
                    LastError = new JsonObject
                    {
                        ["message"] = failure.Error,
                        ["nodeId"] = node.Id,
                        ["nodeLabel"] = label.Trim('«', '»'),
                        ["at"] = now.ToString("O"),
                    },
                };
                var maxAttempts = descriptor.SupportsRetry ? Math.Max(0, node.Settings.RetryCount) + 1 : 1;
                var errorEdge = descriptor.FindOutput(Ports.Error) is not null ? graph.Next(node.Id, Ports.Error) : null;
                if (failure.Retryable && token.Attempt < maxAttempts)
                {
                    var delay = TimeSpan.FromSeconds(Math.Max(0, node.Settings.RetryDelaySeconds));
                    t = delay == TimeSpan.Zero
                        ? t with { State = TokenState.Ready, Attempt = token.Attempt + 1, WaitKind = WaitKind.None, WakeAt = null, WaitRef = null }
                        : t with { State = TokenState.Waiting, Attempt = token.Attempt + 1, WaitKind = WaitKind.Retry, WakeAt = now + delay, WaitRef = null };
                    p = Cleared(p) with { Status = delay == TimeSpan.Zero ? ProcessStatus.Running : ProcessStatus.Waiting };
                    journal.Add((JournalLevel.Warning,
                        $"Шаг {label} не выполнен: {failure.Error} Попытка {token.Attempt + 1} из {maxAttempts}" +
                        (delay == TimeSpan.Zero ? " — сразу." : $" — через {FormatDuration(delay)}.")));
                }
                else if (errorEdge is not null)
                {
                    t = Moved(t, errorEdge.Target);
                    p = Cleared(p) with { Status = ProcessStatus.Running };
                    journal.Add((JournalLevel.Warning, $"Шаг {label} не выполнен: {failure.Error} Процесс продолжен по ветке «Ошибка»."));
                }
                else
                {
                    p = Attention(p, AttentionKind.StepFailed, $"Шаг {label} не выполнен: {failure.Error}", stepId, t.Id);
                    journal.Add((JournalLevel.Error, p.StatusMessage!));
                }

                break;
            }

            case NodeResult.Unknown unknown:
                p = Attention(p, AttentionKind.UnknownStep, $"Неизвестно, выполнился ли шаг {label}. {unknown.Reason}", stepId, t.Id);
                journal.Add((JournalLevel.Error, p.StatusMessage!));
                break;
        }

        if (resolution is not null && result is NodeResult.Success { Message: null or "" })
        {
            journal.Insert(0, (JournalLevel.Info, $"Шаг {label}: {resolution}"));
        }

        if (p.IsTerminal)
        {
            // Параллельные ветви: процесс закончился в одной — остальные ветви и их ожидания снимаются.
            foreach (var other in (await write.GetTokensAsync(p.Id)).Where(o => o.Id != t.Id && o.State != TokenState.Completed))
            {
                await write.UpsertTokenAsync(other with { State = TokenState.Completed, WaitKind = WaitKind.None, WakeAt = null, WaitRef = null });
            }

            await write.CancelOpenStepsAsync(p.Id, stepId, "Отменён: процесс завершился в другой ветви.", now);
            journal.AddRange(await OnTerminalAsync(write, p, effects, now));
        }

        await write.UpsertTokenAsync(t);
        await write.UpdateProcessAsync(p);
        foreach (var (level, message) in journal)
        {
            await write.AddJournalAsync(Journal(p.Id, level, message, stepId, node.Id));
        }

        await write.CommitAsync();
        return p;
    }

    /// <summary>Последний (внутренний) параллельный участок маркера и то, что снаружи него.</summary>
    private static ((string Id, int Count)? Top, string? Outer) TopFork(string? fork)
    {
        if (string.IsNullOrEmpty(fork))
        {
            return (null, null);
        }

        var slash = fork.LastIndexOf('/');
        var last = slash < 0 ? fork : fork[(slash + 1)..];
        var colon = last.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(last[(colon + 1)..], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var count))
        {
            return (null, null);
        }

        return ((last[..colon], count), slash < 0 ? null : fork[..slash]);
    }

    private static TokenRecord Moved(TokenRecord token, string target) => token with
    {
        NodeId = target,
        FromNodeId = token.NodeId,
        State = TokenState.Ready,
        WaitKind = WaitKind.None,
        WakeAt = null,
        Attempt = 1,
        WaitingStepId = null,
        WaitRef = null,
    };

    private static JsonObject? WithMemory(JsonObject? memory, string nodeId, JsonObject? value)
    {
        var result = (JsonObject?)memory?.DeepClone() ?? new JsonObject();
        if (value is null)
        {
            result.Remove(nodeId);
        }
        else
        {
            result[nodeId] = value.DeepClone();
        }

        return result.Count == 0 ? null : result;
    }

    private NodeResult CheckVariableTypes(NodeResult result, ScenarioDefinition definition, NodeTypeDescriptor descriptor)
    {
        // Для внешних действий тип не проверяем после факта: действие уже выполнено.
        if (result is not NodeResult.Success ok || descriptor.Effect == NodeEffect.External)
        {
            return result;
        }

        foreach (var (name, value) in ok.VariableUpdates)
        {
            var declared = definition.Variables.FirstOrDefault(v => v.Name == name);
            if (declared is not null && !ScenarioValidator.MatchesType(value, declared.Type))
            {
                var preview = value?.ToJsonString() ?? "null";
                if (preview.Length > 60)
                {
                    preview = preview[..60] + "…";
                }

                return NodeResult.Fail(
                    $"Переменная «{name}» имеет тип «{TypeTitle(declared.Type)}», а получено значение {preview}.",
                    retryable: false);
            }
        }

        return result;
    }

    private static JsonObject? BuildOutput(NodeResult.Success ok)
    {
        if (ok.VariableUpdates.Count == 0)
        {
            return ok.Output;
        }

        var output = (JsonObject?)ok.Output?.DeepClone() ?? new JsonObject();
        var variables = new JsonObject();
        foreach (var (name, value) in ok.VariableUpdates)
        {
            variables[name] = value?.DeepClone();
        }

        output["variables"] = variables;
        return output;
    }

    private static JsonObject ApplyUpdates(JsonObject variables, IReadOnlyDictionary<string, JsonNode?> updates)
    {
        var result = (JsonObject)variables.DeepClone();
        foreach (var (name, value) in updates)
        {
            result[name] = value?.DeepClone();
        }

        return result;
    }

    private async Task<VerificationResult> SafeVerifyAsync(INodeExecutor executor, NodeExecutionContext context)
    {
        using var timeout = new CancellationTokenSource(_options.DefaultActionTimeout, _time);
        try
        {
            return await executor.VerifyAsync(context, timeout.Token);
        }
        catch (Exception ex)
        {
            return VerificationResult.Inconclusive($"Проверка не удалась: {ex.Message}");
        }
    }

    private StepRecord NewStep(ProcessRecord process, TokenRecord token, NodeDefinition node, NodeTypeDescriptor descriptor,
        JsonObject? input, StepState state, DateTimeOffset plannedAt) => new()
    {
        ProcessId = process.Id,
        TokenId = token.Id,
        NodeId = node.Id,
        NodeType = node.Type,
        NodeLabel = Label(node, descriptor).Trim('«', '»'),
        Attempt = token.Attempt,
        Effect = descriptor.Effect,
        State = state,
        Input = input,
        PlannedAt = plannedAt,
    };

    // ───────────────────────────── Переходы состояния процесса ─────────────────────────────

    private static ProcessRecord Cleared(ProcessRecord process) => process with
    {
        Attention = AttentionKind.None,
        AttentionStepId = null,
        AttentionTokenId = null,
        PauseReason = PauseReason.None,
        StatusMessage = null,
    };

    private static ProcessRecord Attention(ProcessRecord process, AttentionKind kind, string message, long? stepId,
        string? tokenId = null) =>
        process with
        {
            Status = ProcessStatus.NeedsAttention,
            Attention = kind,
            AttentionStepId = stepId,
            AttentionTokenId = tokenId,
            PauseReason = PauseReason.None,
            StatusMessage = message,
        };

    private async Task SetAttentionAsync(ProcessRecord process, AttentionKind kind, string message, long? stepId,
        string? tokenId = null)
    {
        var updated = Attention(process, kind, message, stepId, tokenId) with { UpdatedAt = _time.GetUtcNow() };
        await using var write = await _store.BeginWriteAsync();
        await write.UpdateProcessAsync(updated);
        await write.AddJournalAsync(Journal(process.Id, JournalLevel.Error, message, stepId));
        await write.CommitAsync();
        AfterCommit(updated);
    }

    private async Task UpdateProcessAsync(ProcessRecord process)
    {
        await using var write = await _store.BeginWriteAsync();
        await write.UpdateProcessAsync(process);
        await write.CommitAsync();
        AfterCommit(process);
    }

    /// <summary>
    /// Отладка: нужна ли пауза перед блоком (пошаговый запуск или «Пауза перед блоком»). Возвращает маркер паузы; после
    /// «Продолжить» маркер совпадает — и блок выполняется. Повторы после ошибки не останавливаются.
    /// </summary>
    private static string? BreakpointBefore(ProcessRecord process, TokenRecord token, NodeDefinition node, NodeTypeDescriptor descriptor)
    {
        if (descriptor.IsStart || token.Attempt > 1)
        {
            return null;
        }

        var debug = process.Debug;
        if (debug?.StepMode != true && !(node.Settings.Breakpoint && debug?.IgnoreBreakpoints != true))
        {
            return null;
        }

        var marker = $"{token.Id}:{process.StepCount}";
        return debug?.Passed == marker ? null : marker;
    }

    private async Task PauseAtBreakpointAsync(ProcessRecord process, NodeDefinition node, NodeTypeDescriptor descriptor, string marker)
    {
        var label = string.IsNullOrWhiteSpace(node.Label) ? descriptor.Title : node.Label;
        var why = process.Debug?.StepMode == true ? "пошаговый запуск" : "пауза перед блоком";
        var message = $"Пауза перед блоком «{label}» ({why}). Посмотрите переменные и нажмите «Продолжить».";
        var updated = Cleared(process) with
        {
            Status = ProcessStatus.Paused,
            PauseReason = PauseReason.Breakpoint,
            StatusMessage = message,
            Debug = (process.Debug ?? new DebugState()) with { Passed = marker },
            UpdatedAt = _time.GetUtcNow(),
        };
        await using var write = await _store.BeginWriteAsync();
        await write.UpdateProcessAsync(updated);
        await write.AddJournalAsync(Journal(process.Id, JournalLevel.Info, message, null, node.Id));
        await write.CommitAsync();
        AfterCommit(updated);
    }

    private async Task PauseCoreAsync(ProcessRecord process, PauseReason reason)
    {
        var message = reason == PauseReason.User
            ? "Процесс приостановлен пользователем."
            : "Приложение было закрыто. Процесс приостановлен — проверьте его состояние и возобновите вручную.";
        var updated = Cleared(process) with
        {
            Status = ProcessStatus.Paused,
            PauseReason = reason,
            StatusMessage = message,
            UpdatedAt = _time.GetUtcNow(),
        };
        await using var write = await _store.BeginWriteAsync();
        await write.UpdateProcessAsync(updated);
        await write.AddJournalAsync(Journal(process.Id, JournalLevel.Info, message));
        await write.CommitAsync();
        AfterCommit(updated);
    }

    private async Task CancelCoreAsync(ProcessRecord process)
    {
        var now = _time.GetUtcNow();
        var tokens = await _store.GetTokensAsync(process.Id);
        var openSteps = await _store.GetOpenStepsAsync(process.Id);
        var message = process.ParentProcessId is not null && process.Status != ProcessStatus.Paused
            ? "Процесс остановлен."
            : "Процесс остановлен пользователем.";
        var updated = Cleared(process) with
        {
            Status = ProcessStatus.Cancelled,
            StatusMessage = message,
            FinishedAt = now,
            UpdatedAt = now,
        };
        var effects = new CommitEffects();
        await using (var write = await _store.BeginWriteAsync())
        {
            foreach (var token in tokens.Where(t => t.State != TokenState.Completed))
            {
                await write.UpsertTokenAsync(token with { State = TokenState.Completed, WaitKind = WaitKind.None, WakeAt = null, WaitRef = null });
            }

            foreach (var step in openSteps.Where(s => s.State is StepState.Planned or StepState.Waiting))
            {
                await write.UpdateStepAsync(step with
                {
                    State = StepState.Cancelled,
                    FinishedAt = now,
                    Resolution = "Отменён: процесс остановлен.",
                });
            }

            var journal = await OnTerminalAsync(write, updated, effects, now);
            await write.UpdateProcessAsync(updated);
            await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning, message));
            foreach (var (level, text) in journal)
            {
                await write.AddJournalAsync(Journal(process.Id, level, text));
            }

            await write.CommitAsync();
        }

        AfterCommit(updated);
        await ApplyEffectsAsync(effects);
    }

    private async Task ApplyPendingAsync(ProcessSlot slot, string processId)
    {
        var request = slot.TakePending();
        if (request == PendingRequest.None)
        {
            return;
        }

        var process = await _store.GetProcessAsync(processId);
        if (process is null || process.IsTerminal)
        {
            return;
        }

        if (request == PendingRequest.Cancel)
        {
            await CancelCoreAsync(process);
        }
        else if (process.Status is ProcessStatus.Running or ProcessStatus.Waiting)
        {
            await PauseCoreAsync(process, PauseReason.User);
        }
    }

    private void AfterCommit(ProcessRecord process)
    {
        if (process.IsTerminal)
        {
            _graphs.TryRemove(process.Id, out _);
            try
            {
                ProcessFinished?.Invoke(process.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка в обработчике завершения процесса {ProcessId}.", process.Id);
            }
        }

        Notify(process.Id);
    }

    /// <summary>Процесс завершён (успешно, с ошибкой или остановлен) — например, чтобы адаптер закрыл свои подключения.</summary>
    public event Action<string>? ProcessFinished;

    private async Task<bool> HoldsResourceAsync(string processId, string key) =>
        (await _store.ListResourcesAsync()).Any(r => r.Key == key && r.HolderProcessId == processId);

    /// <summary>
    /// После сбоя платформы приводит процесс в согласованное состояние: начатые шаги — «неизвестно»,
    /// процесс — «требует решения». Возвращает false, если и это записать не удалось.
    /// </summary>
    private async Task<bool> TryMarkEngineErrorAsync(string processId, Exception error)
    {
        try
        {
            var process = await _store.GetProcessAsync(processId);
            if (process is null || process.IsTerminal)
            {
                return true;
            }

            var now = _time.GetUtcNow();
            var running = (await _store.GetOpenStepsAsync(processId)).Where(s => s.State == StepState.Running).ToList();
            var message = running.Count > 0
                ? $"Не удалось сохранить результат шага «{running[^1].NodeLabel}» ({error.Message}). Неизвестно, выполнилось ли действие."
                : $"Внутренняя ошибка платформы: {error.Message}";
            var updated = Attention(process,
                running.Count > 0 ? AttentionKind.UnknownStep : AttentionKind.EngineError,
                message, running.Count > 0 ? running[^1].Id : null, running.Count > 0 ? running[^1].TokenId : null) with { UpdatedAt = now };

            await using var write = await _store.BeginWriteAsync();
            foreach (var step in running)
            {
                await write.UpdateStepAsync(step with { State = StepState.Unknown, Error = message, FinishedAt = now });
            }

            await write.UpdateProcessAsync(updated);
            await write.AddJournalAsync(Journal(processId, JournalLevel.Error, message));
            await write.CommitAsync();
            AfterCommit(updated);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось записать сбой процесса {ProcessId}. Он будет восстановлен при перезапуске.", processId);
            return false;
        }
    }

    private static readonly IReadOnlyDictionary<string, JsonNode?> EmptyUpdates = new Dictionary<string, JsonNode?>();

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
        {
            return $"{Math.Max(0, Math.Round(duration.TotalSeconds)):0} с";
        }

        if (duration.TotalMinutes < 60)
        {
            return $"{Math.Round(duration.TotalMinutes):0} мин";
        }

        if (duration.TotalHours < 48)
        {
            return $"{duration.TotalHours:0.#} ч";
        }

        return $"{duration.TotalDays:0.#} дн";
    }

    private string FormatLocal(DateTimeOffset moment) =>
        TimeZoneInfo.ConvertTime(moment, _time.LocalTimeZone).ToString("dd.MM.yyyy HH:mm:ss");
}
