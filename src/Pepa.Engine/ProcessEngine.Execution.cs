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
            var token = tokens.FirstOrDefault(t => t.State == TokenState.Ready)
                        ?? tokens.FirstOrDefault(t => t.State == TokenState.Waiting && t.WakeAt <= now);
            if (token is null)
            {
                if (tokens.All(t => t.State == TokenState.Completed))
                {
                    await SetAttentionAsync(process, AttentionKind.EngineError, "У процесса нет точки, с которой можно продолжить.", null);
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
        }

        return true;
    }

    private async Task ExecuteTokenAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token, ProcessSlot slot)
    {
        var node = graph.FindNode(token.NodeId);
        if (node is null)
        {
            await SetAttentionAsync(process, AttentionKind.EngineError, $"Блок «{token.NodeId}» не найден в сценарии.", null);
            return;
        }

        if (!_registry.TryGet(node.Type, out var executor))
        {
            await SetAttentionAsync(process, AttentionKind.EngineError,
                $"Тип блока «{node.Type}» недоступен в этой версии Pepa.", null);
            return;
        }

        var descriptor = executor.Descriptor;

        if (token is { State: TokenState.Waiting, WaitKind: WaitKind.Timer, WaitingStepId: { } waitingStepId })
        {
            await CompleteWaitAsync(process, graph, token, node, descriptor, waitingStepId);
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

        var context = new NodeExecutionContext
        {
            ProcessId = process.Id,
            Node = node,
            Input = input,
            Variables = (JsonObject)process.Variables.DeepClone(),
            Attempt = token.Attempt,
            Time = _time,
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

        // 2. Снимок состояния до действия (только чтение).
        JsonObject? checkpoint;
        try
        {
            checkpoint = await executor.PrepareAsync(context, CancellationToken.None);
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
        if (result is NodeResult.Unknown unknown && descriptor.SupportsVerification)
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
    /// Применяет итог шага одной транзакцией: состояние шага, переменные, переход маркера, состояние процесса, журнал.
    /// </summary>
    private async Task ApplyResultAsync(ProcessRecord process, ScenarioGraph graph, TokenRecord token,
        NodeDefinition node, NodeTypeDescriptor descriptor, StepRecord step, NodeResult result, string? resolution,
        StepState successState = StepState.Succeeded)
    {
        var now = _time.GetUtcNow();
        var label = Label(node, descriptor);
        result = CheckVariableTypes(result, graph.Definition, descriptor);

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
            case NodeResult.Finish:
                finalStep = finalStep with { State = StepState.Succeeded, Port = null };
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

        await using var write = await _store.BeginWriteAsync();
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

        switch (result)
        {
            case NodeResult.Success ok:
            {
                var edge = graph.Next(node.Id, ok.Port);
                if (edge is null)
                {
                    p = Attention(p, AttentionKind.EngineError, $"У блока {label} не подключён выход «{ok.Port}».", null);
                    journal.Add((JournalLevel.Error, p.StatusMessage!));
                    break;
                }

                p = Cleared(p) with { Variables = ApplyUpdates(p.Variables, ok.VariableUpdates), Status = ProcessStatus.Running };
                t = t with
                {
                    NodeId = edge.Target, State = TokenState.Ready, WaitKind = WaitKind.None, WakeAt = null, Attempt = 1,
                    WaitingStepId = null,
                };
                if (!string.IsNullOrWhiteSpace(ok.Message))
                {
                    journal.Add((ok.Level == MessageLevel.Warning ? JournalLevel.Warning : JournalLevel.Info, ok.Message));
                }

                break;
            }

            case NodeResult.Wait wait:
                p = Cleared(p) with { Status = ProcessStatus.Waiting };
                t = t with { State = TokenState.Waiting, WaitKind = WaitKind.Timer, WakeAt = wait.Until, WaitingStepId = stepId };
                journal.Add((JournalLevel.Info, wait.Message ?? $"Ожидание до {FormatLocal(wait.Until)}."));
                break;

            case NodeResult.Finish finish:
                var finishMessage = finish.Message ?? (finish.Succeeded ? "Процесс завершён успешно." : "Процесс завершён с ошибкой.");
                p = Cleared(p) with
                {
                    Status = finish.Succeeded ? ProcessStatus.Completed : ProcessStatus.Failed,
                    StatusMessage = finishMessage,
                    FinishedAt = now,
                };
                t = t with { State = TokenState.Completed, WaitKind = WaitKind.None, WakeAt = null, WaitingStepId = null };
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
                        ? t with { State = TokenState.Ready, Attempt = token.Attempt + 1, WaitKind = WaitKind.None, WakeAt = null }
                        : t with { State = TokenState.Waiting, Attempt = token.Attempt + 1, WaitKind = WaitKind.Retry, WakeAt = now + delay };
                    p = Cleared(p) with { Status = delay == TimeSpan.Zero ? ProcessStatus.Running : ProcessStatus.Waiting };
                    journal.Add((JournalLevel.Warning,
                        $"Шаг {label} не выполнен: {failure.Error} Попытка {token.Attempt + 1} из {maxAttempts}" +
                        (delay == TimeSpan.Zero ? " — сразу." : $" — через {FormatDuration(delay)}.")));
                }
                else if (errorEdge is not null)
                {
                    t = t with { NodeId = errorEdge.Target, State = TokenState.Ready, Attempt = 1, WaitKind = WaitKind.None, WakeAt = null };
                    p = Cleared(p) with { Status = ProcessStatus.Running };
                    journal.Add((JournalLevel.Warning, $"Шаг {label} не выполнен: {failure.Error} Процесс продолжен по ветке «Ошибка»."));
                }
                else
                {
                    p = Attention(p, AttentionKind.StepFailed, $"Шаг {label} не выполнен: {failure.Error}", stepId);
                    journal.Add((JournalLevel.Error, p.StatusMessage!));
                }

                break;
            }

            case NodeResult.Unknown unknown:
                p = Attention(p, AttentionKind.UnknownStep, $"Неизвестно, выполнился ли шаг {label}. {unknown.Reason}", stepId);
                journal.Add((JournalLevel.Error, p.StatusMessage!));
                break;
        }

        if (resolution is not null && result is NodeResult.Success { Message: null or "" })
        {
            journal.Insert(0, (JournalLevel.Info, $"Шаг {label}: {resolution}"));
        }

        await write.UpsertTokenAsync(t);
        await write.UpdateProcessAsync(p);
        foreach (var (level, message) in journal)
        {
            await write.AddJournalAsync(Journal(p.Id, level, message, stepId, node.Id));
        }

        await write.CommitAsync();
        AfterCommit(p);
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
        PauseReason = PauseReason.None,
        StatusMessage = null,
    };

    private static ProcessRecord Attention(ProcessRecord process, AttentionKind kind, string message, long? stepId) =>
        process with
        {
            Status = ProcessStatus.NeedsAttention,
            Attention = kind,
            AttentionStepId = stepId,
            PauseReason = PauseReason.None,
            StatusMessage = message,
        };

    private async Task SetAttentionAsync(ProcessRecord process, AttentionKind kind, string message, long? stepId)
    {
        var updated = Attention(process, kind, message, stepId) with { UpdatedAt = _time.GetUtcNow() };
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
        var updated = Cleared(process) with
        {
            Status = ProcessStatus.Cancelled,
            StatusMessage = "Процесс остановлен пользователем.",
            FinishedAt = now,
            UpdatedAt = now,
        };
        await using var write = await _store.BeginWriteAsync();
        foreach (var token in tokens.Where(t => t.State != TokenState.Completed))
        {
            await write.UpsertTokenAsync(token with { State = TokenState.Completed, WaitKind = WaitKind.None, WakeAt = null });
        }

        foreach (var step in openSteps.Where(s => s.State is StepState.Planned or StepState.Waiting))
        {
            await write.UpdateStepAsync(step with
            {
                State = StepState.Cancelled,
                FinishedAt = now,
                Resolution = "Отменён: процесс остановлен пользователем.",
            });
        }

        await write.UpdateProcessAsync(updated);
        await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning, "Процесс остановлен пользователем."));
        await write.CommitAsync();
        AfterCommit(updated);
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
        }

        Notify(process.Id);
    }

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
                message, running.Count > 0 ? running[^1].Id : null) with { UpdatedAt = now };

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
