using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pepa.Core;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Serialization;
using Pepa.Core.Validation;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

/// <param name="Restored">База восстановлена из копии: столько процессов ждут решения пользователя.</param>
public sealed record RecoveryReport(int Paused, int UnknownSteps, int CancelledPlannedSteps, int ReleasedResources = 0,
    int MissedRuns = 0, int Restored = 0)
{
    public override string ToString() =>
        $"Восстановление: приостановлено процессов — {Paused}, шагов с неизвестным итогом — {UnknownSteps}, " +
        $"отменено неначатых шагов — {CancelledPlannedSteps}, освобождено ресурсов — {ReleasedResources}, " +
        $"пропущено запусков по расписанию — {MissedRuns}" +
        (Restored > 0 ? $", база восстановлена из копии — процессов ждут решения: {Restored}." : ".");
}

public sealed partial class ProcessEngine
{
    /// <summary>
    /// Приводит в порядок процессы, прерванные закрытием или сбоем приложения.
    /// Ничего не выполняет и ничего не возобновляет.
    /// </summary>
    private async Task<RecoveryReport> RecoverAsync(CancellationToken cancellationToken)
    {
        var unfinished = await _store.ListProcessesAsync(new ProcessQuery
        {
            Statuses = [ProcessStatus.Running, ProcessStatus.Waiting, ProcessStatus.Paused, ProcessStatus.NeedsAttention],
            Limit = int.MaxValue,
        }, cancellationToken);

        int paused = 0, unknown = 0, cancelled = 0, restored = 0;
        // База — восстановленная копия: всё, что процессы делали после копии, в ней не записано. Продолжать их «как ни
        // в чём не бывало» значило бы повторить уже сделанное — каждый ждёт решения пользователя.
        var restoredNote = RestoredFromBackupAt is { } copyTime
            ? $"База Pepa восстановлена из резервной копии от {copyTime.ToLocalTime():dd.MM.yyyy HH:mm}: что процесс сделал после этого, " +
              "в базе не записано, и Pepa этого не знает."
            : null;
        foreach (var process in unfinished)
        {
            var open = await _store.GetOpenStepsAsync(process.Id, cancellationToken);
            var interrupted = open.Where(s => s.State == StepState.Running).ToList();
            var planned = open.Where(s => s.State == StepState.Planned).ToList();
            var wasActive = process.Status is ProcessStatus.Running or ProcessStatus.Waiting;
            var restore = restoredNote is not null && process.StatusMessage?.StartsWith(restoredNote, StringComparison.Ordinal) != true;
            if (interrupted.Count == 0 && planned.Count == 0 && !wasActive && !restore)
            {
                continue;
            }

            var now = _time.GetUtcNow();
            var updated = process with { UpdatedAt = now };
            await using var write = await _store.BeginWriteAsync(cancellationToken);
            foreach (var step in interrupted)
            {
                await write.UpdateStepAsync(step with
                {
                    State = StepState.Unknown,
                    Error = "Приложение было закрыто во время выполнения шага — неизвестно, выполнилось ли действие.",
                    FinishedAt = now,
                });
                unknown++;
            }

            foreach (var step in planned)
            {
                await write.UpdateStepAsync(step with
                {
                    State = StepState.Cancelled,
                    Resolution = "Не начат: приложение было закрыто. При возобновлении шаг будет запланирован заново.",
                    FinishedAt = now,
                });
                cancelled++;
            }

            if (interrupted.Count > 0)
            {
                var last = interrupted[^1];
                updated = Attention(updated, AttentionKind.UnknownStep,
                    (restore ? restoredNote + " " : "") +
                    $"Приложение было закрыто во время шага «{last.NodeLabel}». Неизвестно, выполнилось ли действие. " +
                    "Проверьте результат и решите, что делать дальше.", last.Id, last.TokenId);
                await write.AddJournalAsync(Journal(process.Id, JournalLevel.Error, updated.StatusMessage!, last.Id, last.NodeId));
                restored += restore ? 1 : 0;
            }
            else if (restore && process.Status == ProcessStatus.NeedsAttention)
            {
                // Решение и так за пользователем — добавить, что база восстановлена.
                updated = updated with { StatusMessage = $"{restoredNote} {process.StatusMessage}" };
                await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning, restoredNote!));
                restored++;
            }
            else if (restore)
            {
                updated = Attention(updated, AttentionKind.RestoredFromBackup,
                    $"{restoredNote} Проверьте во внешних программах, что уже сделано, и продолжите процесс или остановите его.", null);
                await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning, updated.StatusMessage!));
                restored++;
            }
            else if (wasActive)
            {
                updated = Cleared(updated) with
                {
                    Status = ProcessStatus.Paused,
                    PauseReason = PauseReason.Restart,
                    StatusMessage = "Приложение было закрыто. Процесс приостановлен — проверьте его состояние и возобновите вручную.",
                };
                await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning, updated.StatusMessage!));
                paused++;
            }

            await write.UpdateProcessAsync(updated);
            await write.CommitAsync();
        }

        var released = await ReconcileResourcesAsync(cancellationToken);
        var missed = await RecordMissedSchedulesAsync(cancellationToken);
        var report = new RecoveryReport(paused, unknown, cancelled, released, missed, restored);
        if (unknown > 0 || paused > 0 || released > 0 || missed > 0 || restored > 0)
        {
            _logger.LogWarning("{Report}", report);
        }

        return report;
    }

    // ───────────────────────────── Решения пользователя ─────────────────────────────

    private async Task<ControlResult> ContinueCoreAsync(ProcessRecord process, IReadOnlyList<TokenRecord> tokens)
    {
        var now = _time.GetUtcNow();
        var active = tokens.Where(t => t.State != TokenState.Completed).ToList();
        if (active.Count == 0)
        {
            throw new EngineOperationException("У процесса нет точки, с которой можно продолжить.");
        }

        var overdue = active.Any(t => t.State == TokenState.Waiting && t.WakeAt <= now);
        var updated = Cleared(process) with
        {
            Status = active.Any(t => t.State == TokenState.Ready) ? ProcessStatus.Running : ProcessStatus.Waiting,
            StepBudget = process.Attention == AttentionKind.StepLimit
                ? process.StepCount + _options.DefaultStepBudget
                : process.StepBudget,
            UpdatedAt = now,
        };
        var message = "Процесс возобновлён пользователем." +
                      (overdue ? " Срок ожидания уже наступил — выполнение продолжится сразу." : "");
        await using (var write = await _store.BeginWriteAsync())
        {
            await write.UpdateProcessAsync(updated);
            await write.AddJournalAsync(Journal(process.Id, JournalLevel.Info, message));
            await write.CommitAsync();
        }

        AfterCommit(updated);
        return new ControlResult(message);
    }

    private async Task<ControlResult> VerifyCoreAsync(ProcessRecord process, IReadOnlyList<TokenRecord> tokens)
    {
        var (step, token, node, executor) = await LoadAttentionAsync(process, tokens);
        // В этом посещении блока раньше была попытка, которая могла выполниться: сверяется она (с её параметрами и снимком) —
        // снимок этой попытки уже видел бы её результат. Если эта попытка окончательно не удалась, «не выполнено» значит:
        // окончательна её ошибка.
        var earlier = step.Output?["uncertainStepId"]?.AsNumber() is { } uncertainId ? await _store.GetStepAsync((long)uncertainId) : null;
        if (earlier is not null && step.Output?["failure"] is not null)
        {
            return await VerifyEarlierAsync(process, step, earlier, token, node, executor);
        }

        var verification = await SafeVerifyAsync(executor, VerifyContext(process, node, earlier ?? step));
        // Пояснение проверки может содержать значения (адрес страницы, текст расширения) — без секретов.
        var secrets = SecretValues(process, ExecutionGraph(process).Definition, NodeResult.Ok(verification.Port, verification.VariableUpdates ?? EmptyUpdates));
        verification = verification with { Message = Redact(verification.Message, secrets)! };
        var label = Label(node, executor.Descriptor);
        switch (verification.Outcome)
        {
            case VerificationOutcome.Done:
                await ApplyResultAsync(process, ExecutionGraph(process), token, node, executor.Descriptor, step,
                    NodeResult.Ok(verification.Port, verification.VariableUpdates ?? EmptyUpdates, verification.Output),
                    $"проверено — действие выполнено. {verification.Message}");
                return new ControlResult($"Проверка подтвердила, что действие выполнено. Процесс продолжен. {verification.Message}");

            case VerificationOutcome.NotDone:
            {
                var now = _time.GetUtcNow();
                var updated = Cleared(process) with { Status = ProcessStatus.Running, UpdatedAt = now };
                var message = $"Проверка показала, что шаг {label} не выполнен ({verification.Message}). Шаг будет выполнен снова.";
                var effects = new CommitEffects();
                var uncertain = await _store.GetUncertainAttemptsAsync(process.Id, token.Id, node.Id);
                await using (var write = await _store.BeginWriteAsync())
                {
                    await ReleaseTokenWaitAsync(write, process.Id, token, effects);
                    // Прошлые попытки, которые могли выполниться, проверены этой же проверкой — тоже не выполнены.
                    foreach (var settled in uncertain.Where(s => s.Id != step.Id))
                    {
                        await write.UpdateStepAsync(Settled(settled, $"Проверено: не выполнено. {verification.Message}", now));
                    }

                    await write.UpdateStepAsync(step with
                    {
                        State = StepState.Failed,
                        Resolution = $"Проверено: не выполнено. {verification.Message}",
                        FinishedAt = step.FinishedAt ?? now,
                    });
                    await write.UpsertTokenAsync(token with
                    {
                        State = TokenState.Ready, Attempt = token.Attempt + 1, WaitKind = WaitKind.None, WakeAt = null,
                        WaitingStepId = null, WaitRef = null,
                    });
                    await write.UpdateProcessAsync(updated);
                    await write.AddJournalAsync(Journal(process.Id, JournalLevel.Info, message, step.Id, node.Id));
                    await write.CommitAsync();
                }

                AfterCommit(updated);
                await ApplyEffectsAsync(effects);
                return new ControlResult(message);
            }

            default:
            {
                var message = $"Проверка не дала однозначного ответа: {verification.Message} " +
                              "Решите вручную: считать шаг выполненным или невыполненным, выполнить его ещё раз или остановить процесс.";
                var updated = process with { StatusMessage = message, UpdatedAt = _time.GetUtcNow() };
                await using (var write = await _store.BeginWriteAsync())
                {
                    await write.UpdateProcessAsync(updated);
                    await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning, message, step.Id, node.Id));
                    await write.CommitAsync();
                }

                AfterCommit(updated);
                return new ControlResult(message);
            }
        }
    }

    private async Task<ControlResult> VerifyEarlierAsync(ProcessRecord process, StepRecord step, StepRecord earlier, TokenRecord token,
        NodeDefinition node, INodeExecutor executor)
    {
        var verification = await SafeVerifyAsync(executor, VerifyContext(process, node, earlier));
        var secrets = SecretValues(process, ExecutionGraph(process).Definition, NodeResult.Ok(verification.Port, verification.VariableUpdates ?? EmptyUpdates));
        verification = verification with { Message = Redact(verification.Message, secrets)! };
        switch (verification.Outcome)
        {
            case VerificationOutcome.Done:
                await ApplyResultAsync(process, ExecutionGraph(process), token, node, executor.Descriptor, step,
                    NodeResult.Ok(verification.Port, verification.VariableUpdates ?? EmptyUpdates, verification.Output),
                    $"проверено — прошлая попытка выполнила действие. {verification.Message}",
                    extraWrites: (write, _, _, effects) => ReleaseTokenWaitAsync(write, process.Id, token, effects));
                return new ControlResult($"Проверка подтвердила, что действие выполнено (прошлой попыткой). Процесс продолжен. {verification.Message}");

            case VerificationOutcome.NotDone:
                return await NotExecutedAsync(process, step, token, node, executor.Descriptor,
                    $"Проверено: не выполнено. {verification.Message}",
                    step.Output?["failure"]?.AsString() ?? step.Error ?? "Шаг не выполнен.");

            default:
            {
                var message = $"Проверка не дала однозначного ответа: {verification.Message} " +
                              "Решите вручную: считать шаг выполненным или невыполненным, выполнить его ещё раз или остановить процесс.";
                var updated = process with { StatusMessage = message, UpdatedAt = _time.GetUtcNow() };
                await using (var write = await _store.BeginWriteAsync())
                {
                    await write.UpdateProcessAsync(updated);
                    await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning, message, step.Id, node.Id));
                    await write.CommitAsync();
                }

                AfterCommit(updated);
                return new ControlResult(message);
            }
        }
    }

    /// <summary>
    /// Действие шага не выполнено (решение пользователя или проверка прошлой попытки): попытки этого посещения, которые
    /// могли выполниться, становятся невыполненными, а сам шаг — ошибкой без повтора: процесс идёт по ветке «Ошибка», а
    /// если её нет — останавливается (шаг можно повторить или пропустить).
    /// </summary>
    private async Task<ControlResult> NotExecutedAsync(ProcessRecord process, StepRecord step, TokenRecord token, NodeDefinition node,
        NodeTypeDescriptor descriptor, string resolution, string error)
    {
        var uncertain = await _store.GetUncertainAttemptsAsync(process.Id, token.Id, node.Id);
        await ApplyResultAsync(process, ExecutionGraph(process), token, node, descriptor, step,
            NodeResult.Fail(error, retryable: false), resolution,
            extraWrites: (write, _, _, effects) => ReleaseTokenWaitAsync(write, process.Id, token, effects),
            settle: new Settlement(uncertain.Where(s => s.Id != step.Id).ToList(), resolution));
        return new ControlResult($"Шаг {Label(node, descriptor)} не выполнен: {error}");
    }

    private async Task<ControlResult> MarkFailedCoreAsync(ProcessRecord process, IReadOnlyList<TokenRecord> tokens)
    {
        var (step, token, node, executor) = await LoadAttentionAsync(process, tokens);
        return await NotExecutedAsync(process, step, token, node, executor.Descriptor, "Пользователь: действие не выполнено.",
            $"Пользователь отметил шаг {Label(node, executor.Descriptor)} невыполненным.");
    }

    private async Task<ControlResult> RetryCoreAsync(ProcessRecord process, IReadOnlyList<TokenRecord> tokens, JsonObject? changes = null,
        BlockReplacement? block = null)
    {
        // Исправления переменных — до повтора, в той же записи: шаг возьмёт новые значения.
        var (changed, changeNotes) = changes is null ? (process, []) : ApplyChanges(process, changes);
        process = changed;
        if (block is not null)
        {
            process = process with { DefinitionJson = block.DefinitionJson };
            changeNotes.Add(block.Note);
        }

        var fresh = changes is not null || block is not null;
        var step = process.AttentionStepId is { } stepId ? await _store.GetStepAsync(stepId) : null;
        // Повторяется шаг той ветви, что остановилась (у процесса с параллельными ветвями их несколько).
        var token = AttentionToken(process, tokens, step?.TokenId)
                    ?? throw new EngineOperationException(tokens.Count(t => t.State != TokenState.Completed) > 1
                        ? "Непонятно, шаг какой ветви повторить. Остановите процесс и запустите заново."
                        : "У процесса нет точки, с которой можно продолжить.");
        var node = ExecutionGraph(process).FindNode(token.NodeId);
        var label = node is null ? $"«{token.NodeId}»" : Label(node, _registry.FindDescriptor(node.Type));
        var now = _time.GetUtcNow();
        var updated = Cleared(process) with { Status = ProcessStatus.Running, UpdatedAt = now };
        var message = $"Шаг {label} будет выполнен ещё раз по решению пользователя.";
        var effects = new CommitEffects();
        await using (var write = await _store.BeginWriteAsync())
        {
            // Прежнее ожидание этого шага (событие, ресурс, вызванный сценарий) больше не действует.
            await ReleaseTokenWaitAsync(write, process.Id, token, effects);
            if (step is not null)
            {
                await write.UpdateStepAsync(step with { Resolution = "Пользователь решил выполнить шаг ещё раз." });
            }

            // С исправленными данными — новое посещение блока (первая попытка): прежние попытки окончательно не выполнены, а
            // то, что блок берёт из прошлой попытки (ключ от повтора у «Отправить HTTP-запрос»), относилось к другим данным.
            await write.UpsertTokenAsync(token with
            {
                State = TokenState.Ready, Attempt = fresh ? 1 : token.Attempt + 1, WaitKind = WaitKind.None, WakeAt = null,
                WaitingStepId = null, WaitRef = null,
            });
            await write.UpdateProcessAsync(updated);
            if (block is not null)
            {
                await write.UpdateProcessDefinitionAsync(process.Id, block.DefinitionJson);
            }
            foreach (var note in changeNotes)
            {
                await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning, note, step?.Id, token.NodeId));
            }

            await write.AddJournalAsync(Journal(process.Id, JournalLevel.Warning, message, step?.Id, token.NodeId));
            await write.CommitAsync();
        }

        if (block is not null)
        {
            // Процесс дальше идёт по сценарию с новым блоком (до этого — по запомненному прежнему).
            _graphs[process.Id] = ParseGraph(block.DefinitionJson);
        }

        AfterCommit(updated);
        await ApplyEffectsAsync(effects);
        return new ControlResult(block is not null ? $"Блок взят из сохранённого сценария. {message}"
            : changeNotes.Count > 0 ? $"Переменные исправлены ({changeNotes.Count}). {message}" : message);
    }

    /// <summary>Сценарий процесса с заменённым блоком и запись об этом для журнала.</summary>
    private sealed record BlockReplacement(string DefinitionJson, string Note);

    /// <summary>
    /// «Взять блок из сохранённого сценария и повторить» (D-085): блок с ошибкой в настройке (селектор, адрес, текст) исправили
    /// в конструкторе и сохранили. Процесс берёт из сохранённого сценария только этот блок — тот же блок (по номеру) того же
    /// типа; схема, переменные и всё, что процесс уже сделал, остаются. Шаг выполняется снова с первой попытки.
    /// </summary>
    private async Task<ControlResult> RetryWithNewBlockAsync(ProcessRecord process, IReadOnlyList<TokenRecord> tokens)
    {
        var step = process.AttentionStepId is { } stepId ? await _store.GetStepAsync(stepId) : null;
        var token = AttentionToken(process, tokens, step?.TokenId)
                    ?? throw new EngineOperationException("У процесса нет точки, с которой можно продолжить.");
        var graph = ExecutionGraph(process);
        var current = graph.FindNode(token.NodeId)
                      ?? throw new EngineOperationException("Блока, на котором остановился процесс, нет в его сценарии.");
        var descriptor = _registry.FindDescriptor(current.Type);
        var label = Label(current, descriptor);
        var stored = await _store.GetScenarioAsync(process.ScenarioId)
                     ?? throw new EngineOperationException("Сценарий процесса удалён — взять блок неоткуда.");
        var saved = PepaJson.Deserialize<ScenarioDefinition>(stored.DefinitionJson);
        var replacement = saved.Nodes.FirstOrDefault(n => n.Id == current.Id)
                          ?? throw new EngineOperationException(
                              $"В сохранённом сценарии нет блока {label}: его удалили или заменили другим. Взять можно только тот же блок.");
        if (replacement.Type != current.Type)
        {
            throw new EngineOperationException($"В сохранённом сценарии на месте блока {label} блок другого типа — взять можно только тот же блок.");
        }

        var secrets = SecretValues(process, graph.Definition);
        var differences = BlockDifferences(current, replacement, descriptor, secrets);
        if (differences.Count == 0)
        {
            throw new EngineOperationException(
                $"Блок {label} в сохранённом сценарии такой же, как в процессе. Исправьте его в конструкторе, сохраните сценарий и выберите это действие снова.");
        }

        var patched = graph.Definition with
        {
            Nodes = [.. graph.Definition.Nodes.Select(n => n.Id == current.Id ? replacement with { Position = n.Position } : n)],
        };
        var outgoing = patched.Edges.Where(e => e.Source == current.Id).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var problems = _validator.Validate(patched).Issues
            .Where(i => i.Severity == IssueSeverity.Error && (i.NodeId == current.Id || (i.EdgeId is { } edge && outgoing.Contains(edge))))
            .Select(i => i.Message)
            .ToList();
        if (problems.Count > 0)
        {
            throw new EngineOperationException(
                $"Блок {label} из сохранённого сценария с ошибкой: {string.Join(" ", problems)} Исправьте его в конструкторе и сохраните сценарий.");
        }

        var note = Redact($"Блок {label} взят из сохранённого сценария (версия {stored.Version}): {string.Join("; ", differences)}.", secrets)!;
        return await RetryCoreAsync(process, tokens, block: new BlockReplacement(PepaJson.Serialize(patched), note));
    }

    /// <summary>Что изменилось в блоке: подпись, параметры (со значениями, без секретов), настройки повтора.</summary>
    private static List<string> BlockDifferences(NodeDefinition before, NodeDefinition after, NodeTypeDescriptor? descriptor,
        IReadOnlyList<string> secrets)
    {
        var result = new List<string>();
        if ((before.Label ?? "") != (after.Label ?? ""))
        {
            result.Add($"подпись «{before.Label}» → «{after.Label}»");
        }

        foreach (var name in before.Config.Select(p => p.Key).Union(after.Config.Select(p => p.Key)).Distinct(StringComparer.Ordinal))
        {
            var old = before.Config[name];
            var now = after.Config[name];
            if (JsonNode.DeepEquals(old, now))
            {
                continue;
            }

            var title = descriptor?.Fields.FirstOrDefault(f => f.Name == name)?.Label ?? name;
            result.Add($"«{title}»: {Preview(old, secrets)} → {Preview(now, secrets)}");
        }

        if (before.Settings != after.Settings)
        {
            result.Add("настройки повтора и тайм-аута");
        }

        return result;
    }

    /// <summary>
    /// Новые значения переменных процесса (D-085): только объявленные в сценарии и своего типа. В журнал — что было и что
    /// стало; секретная переменная — только «изменена», её прежнее значение запоминается как секрет процесса, а в тексте
    /// любого значения скрываются секреты — и прежние, и введённые этим же исправлением. Переменная, в значении которой был
    /// секрет, показывается пользователю пустой: введённое для неё значение — тоже секрет процесса, а значение со скрытой
    /// частью («••••») не принимается — иначе вместо секрета записалась бы маска.
    /// </summary>
    private (ProcessRecord Process, List<string> Notes) ApplyChanges(ProcessRecord process, JsonObject changes)
    {
        var definition = ExecutionGraph(process).Definition;
        var known = SecretValues(process, definition);
        var variables = (JsonObject)process.Variables.DeepClone();
        var edits = new List<(string Name, JsonNode? Before, JsonNode? Value, bool Secret, bool HeldSecret)>();
        var replaced = new List<string>();
        var entered = new List<string>();
        foreach (var (name, value) in changes)
        {
            var declared = definition.Variables.FirstOrDefault(v => v.Name == name)
                           ?? throw new EngineOperationException($"Переменная «{name}» не объявлена в сценарии.");
            if (!ScenarioValidator.MatchesType(value, declared.Type))
            {
                throw new EngineOperationException($"Значение переменной «{name}» должно иметь тип «{TypeTitle(declared.Type)}».");
            }

            var before = variables[name];
            if (JsonNode.DeepEquals(before, value))
            {
                continue;
            }

            var heldSecret = !declared.Secret && before is not null && known.Count > 0 &&
                             Redact(before.DeepClone(), known)?.ToJsonString() != before.ToJsonString();
            if (heldSecret && HasMask(value))
            {
                throw new EngineOperationException(
                    $"В значении переменной «{name}» есть «{SecretMask}» — так показана скрытая часть прежнего значения (секрет). " +
                    "Введите значение целиком.");
            }

            if (declared.Secret || heldSecret)
            {
                Collect(before, replaced);
            }

            if (heldSecret)
            {
                Collect(value, entered);
            }

            edits.Add((name, before, value, declared.Secret, heldSecret));
            variables[name] = value?.DeepClone();
        }

        if (edits.Count == 0)
        {
            throw new EngineOperationException("Значения не изменились — повторите шаг обычным «Выполнить шаг ещё раз».");
        }

        var changed = process with { Variables = variables, Secrets = Remember(process.Secrets, replaced, entered) };
        // Секреты после исправления: прежние и новые значения секретных переменных.
        var secrets = SecretValues(changed, definition);
        var notes = edits.Select(e => e switch
            {
                { Secret: true } => $"Пользователь исправил секретную переменную «{e.Name}» (значение не показывается).",
                { HeldSecret: true } => $"Пользователь исправил переменную «{e.Name}» (значение не показывается: в нём был секрет).",
                _ => Redact($"Пользователь исправил переменную «{e.Name}»: {Preview(e.Before, secrets)} → {Preview(e.Value, secrets)}.", secrets)!,
            })
            .ToList();
        return (changed, notes);
    }

    /// <summary>Есть ли в значении (в его строках) маска скрытого секрета.</summary>
    private static bool HasMask(JsonNode? value) => value switch
    {
        JsonObject obj => obj.Any(p => HasMask(p.Value)),
        JsonArray array => array.Any(HasMask),
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Contains(SecretMask, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Значение для журнала: секреты скрываются до обрезки — иначе обрезанное начало секрета не узнать.</summary>
    private static string Preview(JsonNode? value, IReadOnlyList<string> secrets)
    {
        var text = value switch
        {
            null => "пусто",
            JsonValue scalar when scalar.TryGetValue<string>(out var s) => $"«{s}»",
            _ => value.ToJsonString(),
        };
        text = Redact(text, secrets)!;
        return text.Length > 120 ? text[..119] + "…" : text;
    }

    /// <summary>Считать шаг выполненным или пропустить его и идти дальше по единственному выходу.</summary>
    private async Task<ControlResult> ProceedCoreAsync(ProcessRecord process, IReadOnlyList<TokenRecord> tokens,
        StepState finalState)
    {
        var (step, token, node, executor) = await LoadAttentionAsync(process, tokens);
        var descriptor = executor.Descriptor;
        var port = descriptor.SuccessOutputs.Single().Id;
        var label = Label(node, descriptor);
        var message = finalState == StepState.Skipped
            ? $"Шаг {label} пропущен по решению пользователя."
            : $"Пользователь подтвердил, что шаг {label} выполнен. Значения, которые шаг должен был сохранить, не записаны.";
        await ApplyResultAsync(process, ExecutionGraph(process), token, node, descriptor, step,
            new NodeResult.Success(port, EmptyUpdates, null, message, MessageLevel.Warning),
            finalState == StepState.Skipped ? "Пропущен пользователем." : "Отмечен выполненным пользователем.",
            finalState, extraWrites: (write, _, _, effects) => ReleaseTokenWaitAsync(write, process.Id, token, effects));
        return new ControlResult(message);
    }

    private async Task<(StepRecord Step, TokenRecord Token, NodeDefinition Node, INodeExecutor Executor)> LoadAttentionAsync(
        ProcessRecord process, IReadOnlyList<TokenRecord> tokens)
    {
        var step = process.AttentionStepId is { } stepId ? await _store.GetStepAsync(stepId) : null;
        if (step is null)
        {
            throw new EngineOperationException("Не найден шаг, по которому нужно решение.");
        }

        var token = AttentionToken(process, tokens, step.TokenId)
                    ?? throw new EngineOperationException("У процесса нет точки, с которой можно продолжить.");
        var node = ExecutionGraph(process).FindNode(step.NodeId)
                   ?? throw new EngineOperationException($"Блок «{step.NodeId}» не найден в сценарии.");
        if (!_registry.TryGet(node.Type, out var executor))
        {
            throw new EngineOperationException($"Тип блока «{node.Type}» недоступен в этой версии Pepa.");
        }

        return (step, token, node, executor);
    }
}
