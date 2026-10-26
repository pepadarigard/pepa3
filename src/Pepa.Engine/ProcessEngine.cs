using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Pepa.Core;
using Pepa.Core.Expressions;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Serialization;
using Pepa.Core.Validation;
using Pepa.Engine.Persistence;

namespace Pepa.Engine;

/// <summary>
/// Движок исполнения. Каждый процесс — независимый экземпляр сценария со своим состоянием в хранилище.
/// Шаги одного процесса выполняются строго последовательно, разные процессы — параллельно
/// (не больше <see cref="EngineOptions.MaxParallelProcesses"/>). Ожидание не занимает поток:
/// это запись в хранилище, которую периодически проверяет таймер.
/// </summary>
public sealed partial class ProcessEngine : IAsyncDisposable
{
    private readonly IPepaStore _store;
    private readonly NodeRegistry _registry;
    private readonly IExpressionEvaluator _expressions;
    private readonly ScenarioValidator _validator;
    private readonly EngineOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, ProcessSlot> _slots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ScenarioGraph> _graphs = new(StringComparer.Ordinal);
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly Lock _queueLock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _background = [];
    private int _executingSteps;
    private int _state; // 0 — создан, 1 — работает, 2 — остановлен

    public ProcessEngine(
        IPepaStore store,
        NodeRegistry registry,
        IExpressionEvaluator expressions,
        EngineOptions? options = null,
        TimeProvider? time = null,
        ILogger<ProcessEngine>? logger = null)
    {
        _store = store;
        _registry = registry;
        _expressions = expressions;
        _validator = new ScenarioValidator(registry, expressions);
        _options = options ?? new EngineOptions();
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>Состояние процесса изменилось (вызывается после записи в хранилище).</summary>
    public event Action<EngineChange>? Changed;

    public NodeRegistry Registry => _registry;
    public ScenarioValidator Validator => _validator;
    public EngineOptions Options => _options;

    /// <summary>Сколько действий выполняется прямо сейчас.</summary>
    public int ExecutingStepCount => Volatile.Read(ref _executingSteps);

    /// <summary>
    /// Восстановление после прошлого запуска и старт исполнителей. Ничего не продолжается автоматически:
    /// незавершённые процессы приостанавливаются до ручного возобновления.
    /// </summary>
    public async Task<RecoveryReport> StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            throw new InvalidOperationException("Движок уже запущен.");
        }

        // События, принятые до этого запуска, сопоставляются с ожидающими, но процессы по ним сами не запускаются.
        _eventsBeforeStart = (await _store.ListEventsAsync(new EventQuery { Limit = 1 }, cancellationToken)).FirstOrDefault()?.Id ?? 0;
        var report = await RecoverAsync(cancellationToken);
        for (var i = 0; i < Math.Max(1, _options.MaxParallelProcesses); i++)
        {
            _background.Add(Task.Run(() => WorkerLoopAsync(_stop.Token)));
        }

        _background.Add(Task.Run(() => TimerLoopAsync(_stop.Token)));
        _background.Add(Task.Run(() => RouterLoopAsync(_stop.Token)));
        _background.Add(Task.Run(() => TriggerLoopAsync(_stop.Token)));
        // События, принятые до закрытия, но не сопоставленные, — сопоставить. Применятся они только после ручного возобновления,
        // а триггеры по ним сами не сработают.
        SignalRouter();
        _logger.LogInformation("Движок запущен: {Workers} исполнителей. {Report}", _options.MaxParallelProcesses, report);
        return report;
    }

    /// <summary>
    /// Остановка. Выполняющиеся действия не прерываются — движок ждёт их завершения в пределах
    /// <see cref="EngineOptions.ShutdownGracePeriod"/>. Не успевшие шаги после перезапуска получат статус «неизвестно».
    /// </summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _state, 2) != 1)
        {
            return;
        }

        await _stop.CancelAsync();
        _queue.Writer.TryComplete();
        var all = Task.WhenAll(_background);
        var finished = await Task.WhenAny(all, Task.Delay(_options.ShutdownGracePeriod));
        if (finished != all)
        {
            _logger.LogWarning("Не все шаги завершились за {Grace}. После перезапуска их результат будет «неизвестно».",
                _options.ShutdownGracePeriod);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stop.Dispose();
    }

    // ───────────────────────────── Запуск процесса ─────────────────────────────

    public Task<string> StartProcessAsync(string scenarioId, JsonObject? inputs = null,
        CancellationToken cancellationToken = default) =>
        StartProcessAsync(scenarioId, inputs, originKey: null, origin: null, cancellationToken);

    /// <summary>
    /// Запустить процесс. <paramref name="originKey"/> защищает от двойного запуска (расписание, событие):
    /// если процесс с таким ключом уже есть, новый не создаётся и возвращается существующий.
    /// </summary>
    public async Task<string> StartProcessAsync(string scenarioId, JsonObject? inputs, string? originKey, string? origin,
        CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var prepared = await PrepareStartAsync(scenarioId, inputs, cancellationToken);
        ProcessRecord process;
        bool created;
        await using (var write = await _store.BeginWriteAsync(cancellationToken))
        {
            (process, created) = await InsertStartedProcessAsync(write, prepared, originKey, origin);
            await write.CommitAsync();
        }

        if (created)
        {
            Notify(process.Id);
            Enqueue(process.Id);
        }

        return process.Id;
    }

    /// <summary>Записать новый процесс в транзакции. Если процесс с таким ключом происхождения уже есть — вернуть его.</summary>
    private async Task<(ProcessRecord Process, bool Created)> InsertStartedProcessAsync(IStoreWrite write, PreparedStart prepared,
        string? originKey, string? origin)
    {
        if (originKey is not null && await write.GetProcessByOriginAsync(originKey) is { } existing)
        {
            return (existing, false);
        }

        var (process, token) = NewProcess(prepared, _time.GetUtcNow(), parentId: null, depth: 0, originKey);
        await write.InsertProcessAsync(process);
        await write.UpsertTokenAsync(token);
        await write.AddJournalAsync(Journal(process.Id, JournalLevel.Info,
            $"Процесс запущен{(origin is null ? "" : $" ({origin})")}: сценарий «{prepared.Scenario.Name}», " +
            $"версия {prepared.Scenario.Version}."));
        return (process, true);
    }

    /// <summary>Загрузить и проверить сценарий, собрать начальные переменные.</summary>
    internal async Task<PreparedStart> PrepareStartAsync(string scenarioId, JsonObject? inputs,
        CancellationToken cancellationToken = default)
    {
        var stored = await _store.GetScenarioAsync(scenarioId, cancellationToken)
                     ?? throw new EngineOperationException("Сценарий не найден.");
        var definition = PepaJson.Deserialize<ScenarioDefinition>(stored.DefinitionJson);
        var report = _validator.Validate(definition);
        if (!report.IsValid)
        {
            throw new ScenarioInvalidException(report);
        }

        var variables = BuildVariables(definition, inputs);
        var start = definition.Nodes.First(n => _registry.FindDescriptor(n.Type)?.IsStart == true);
        return new PreparedStart(stored, definition, variables, start);
    }

    private static JsonObject BuildVariables(ScenarioDefinition definition, JsonObject? inputs)
    {
        var variables = new JsonObject();
        foreach (var variable in definition.Variables)
        {
            variables[variable.Name] = variable.Default?.DeepClone();
        }

        if (inputs is null)
        {
            return variables;
        }

        foreach (var (name, value) in inputs)
        {
            var declared = definition.Variables.FirstOrDefault(v => v.Name == name)
                           ?? throw new EngineOperationException($"Переменная «{name}» не объявлена в сценарии.");
            if (!ScenarioValidator.MatchesType(value, declared.Type))
            {
                throw new EngineOperationException(
                    $"Значение переменной «{name}» должно иметь тип «{TypeTitle(declared.Type)}».");
            }

            variables[name] = value?.DeepClone();
        }

        return variables;
    }

    public static string TypeTitle(VariableType type) => type switch
    {
        VariableType.Text => "текст",
        VariableType.Number => "число",
        VariableType.Boolean => "да/нет",
        VariableType.List => "список",
        VariableType.Object => "объект",
        _ => "любой",
    };

    // ───────────────────────────── Управление ─────────────────────────────

    public async Task<ControlResult> PauseAsync(string processId, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var process = await GetOrThrowAsync(processId, cancellationToken);
        if (process.Status is not (ProcessStatus.Running or ProcessStatus.Waiting))
        {
            throw new EngineOperationException("Приостановить можно только выполняющийся или ожидающий процесс.");
        }

        var slot = Slot(processId);
        slot.Request(PendingRequest.Pause);
        if (!slot.TryEnter())
        {
            return new ControlResult("Процесс будет приостановлен после завершения текущего шага.");
        }

        try
        {
            await ApplyPendingAsync(slot, processId);
        }
        finally
        {
            slot.Exit();
        }

        return new ControlResult("Процесс приостановлен.");
    }

    public async Task<ControlResult> CancelAsync(string processId, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var process = await GetOrThrowAsync(processId, cancellationToken);
        if (process.IsTerminal)
        {
            throw new EngineOperationException("Процесс уже завершён.");
        }

        var slot = Slot(processId);
        slot.Request(PendingRequest.Cancel);
        if (!slot.TryEnter())
        {
            return new ControlResult("Процесс будет остановлен, как только текущее действие завершится или прервётся.");
        }

        try
        {
            await ApplyPendingAsync(slot, processId);
        }
        finally
        {
            slot.Exit();
        }

        return new ControlResult("Процесс остановлен.");
    }

    /// <summary>Решение пользователя по приостановленному процессу или процессу, требующему внимания.</summary>
    public async Task<ControlResult> ResumeAsync(string processId, ResumeAction action,
        CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        var slot = Slot(processId);
        if (slot.Faulted)
        {
            throw new EngineOperationException(
                "С этим процессом не удалось сохранить данные. Перезапустите приложение — затем процесс можно будет возобновить.");
        }

        if (!await slot.EnterAsync(TimeSpan.FromSeconds(10)))
        {
            throw new EngineOperationException("Процесс сейчас выполняет шаг. Попробуйте ещё раз через несколько секунд.");
        }

        ControlResult result;
        try
        {
            var process = await GetOrThrowAsync(processId, cancellationToken);
            var tokens = await _store.GetTokensAsync(processId, cancellationToken);
            var allowed = GetAllowedActions(process, tokens);
            if (!allowed.Contains(ToProcessAction(action)))
            {
                throw new EngineOperationException("Это действие сейчас недоступно для процесса.");
            }

            result = action switch
            {
                ResumeAction.Continue => await ContinueCoreAsync(process, tokens),
                ResumeAction.Verify => await VerifyCoreAsync(process, tokens),
                ResumeAction.Retry => await RetryCoreAsync(process, tokens),
                ResumeAction.MarkSucceeded => await ProceedCoreAsync(process, tokens, StepState.Succeeded),
                ResumeAction.Skip => await ProceedCoreAsync(process, tokens, StepState.Skipped),
                _ => throw new EngineOperationException("Неизвестное действие."),
            };
        }
        finally
        {
            slot.Exit();
        }

        Enqueue(processId);
        return result;
    }

    /// <summary>Какие действия пользователь может выполнить над процессом сейчас.</summary>
    public IReadOnlyList<ProcessAction> GetAllowedActions(ProcessRecord process, IReadOnlyList<TokenRecord> tokens)
    {
        switch (process.Status)
        {
            case ProcessStatus.Running or ProcessStatus.Waiting:
                return [ProcessAction.Pause, ProcessAction.Cancel];
            case ProcessStatus.Paused:
                return [ProcessAction.Continue, ProcessAction.Cancel];
            case ProcessStatus.NeedsAttention:
                break;
            default:
                return [];
        }

        var descriptor = AttentionDescriptor(process, tokens);
        var singleSuccessPort = descriptor?.SuccessOutputs.Count() == 1;
        var actions = new List<ProcessAction>();
        switch (process.Attention)
        {
            case AttentionKind.UnknownStep:
                if (descriptor is { SupportsVerification: true, Effect: NodeEffect.External })
                {
                    actions.Add(ProcessAction.Verify);
                }

                actions.Add(ProcessAction.Retry);
                if (singleSuccessPort)
                {
                    actions.Add(ProcessAction.MarkSucceeded);
                }

                break;
            case AttentionKind.StepFailed:
                actions.Add(ProcessAction.Retry);
                if (singleSuccessPort)
                {
                    actions.Add(ProcessAction.Skip);
                }

                break;
            case AttentionKind.StepLimit:
                actions.Add(ProcessAction.Continue);
                break;
            default:
                // Сбой платформы во время ожидания: само ожидание (событие, ресурс, срок) записано целиком —
                // его можно просто продолжить, не начиная шаг заново.
                if (tokens.Any(t => t is { State: TokenState.Waiting, WaitKind: WaitKind.Event or WaitKind.Resource or WaitKind.Timer }))
                {
                    actions.Add(ProcessAction.Continue);
                }

                actions.Add(ProcessAction.Retry);
                break;
        }

        actions.Add(ProcessAction.Cancel);
        return actions;
    }

    /// <summary>Описание блока, на котором стоит процесс.</summary>
    public NodeTypeDescriptor? AttentionDescriptor(ProcessRecord process, IReadOnlyList<TokenRecord> tokens)
    {
        var token = tokens.FirstOrDefault(t => t.State != TokenState.Completed);
        var node = token is null ? null : Graph(process).FindNode(token.NodeId);
        return node is null ? null : _registry.FindDescriptor(node.Type);
    }

    public ScenarioGraph Graph(ProcessRecord process) =>
        _graphs.GetOrAdd(process.Id,
            static (_, json) => new ScenarioGraph(PepaJson.Deserialize<ScenarioDefinition>(json)),
            process.DefinitionJson);

    private static ProcessAction ToProcessAction(ResumeAction action) => action switch
    {
        ResumeAction.Continue => ProcessAction.Continue,
        ResumeAction.Verify => ProcessAction.Verify,
        ResumeAction.Retry => ProcessAction.Retry,
        ResumeAction.MarkSucceeded => ProcessAction.MarkSucceeded,
        ResumeAction.Skip => ProcessAction.Skip,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    // ───────────────────────────── Очередь, исполнители, таймер ─────────────────────────────

    private void Enqueue(string processId)
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        lock (_queueLock)
        {
            if (!_queued.Add(processId))
            {
                return;
            }
        }

        _queue.Writer.TryWrite(processId);
    }

    private async Task WorkerLoopAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var processId in _queue.Reader.ReadAllAsync(stop))
            {
                lock (_queueLock)
                {
                    _queued.Remove(processId);
                }

                await RunProcessAsync(processId, stop);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Остановка.
        }
    }

    private async Task TimerLoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                foreach (var processId in await _store.GetRunnableProcessIdsAsync(_time.GetUtcNow(), stop))
                {
                    Enqueue(processId);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка таймера ожиданий.");
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

    // ───────────────────────────── Вспомогательное ─────────────────────────────

    private ProcessSlot Slot(string processId) => _slots.GetOrAdd(processId, static _ => new ProcessSlot());

    private async Task<ProcessRecord> GetOrThrowAsync(string processId, CancellationToken cancellationToken) =>
        await _store.GetProcessAsync(processId, cancellationToken)
        ?? throw new EngineOperationException("Процесс не найден.");

    private void EnsureRunning()
    {
        if (Volatile.Read(ref _state) != 1)
        {
            throw new EngineOperationException("Движок не запущен.");
        }
    }

    private void Notify(string processId)
    {
        try
        {
            Changed?.Invoke(new EngineChange(processId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка в обработчике изменений процесса.");
        }
    }

    private JournalEntry Journal(string processId, JournalLevel level, string message, long? stepId = null,
        string? nodeId = null) => new()
    {
        ProcessId = processId,
        StepId = stepId,
        NodeId = nodeId,
        At = _time.GetUtcNow(),
        Level = level,
        Message = message,
    };

    internal static string NewId() => Guid.CreateVersion7().ToString("N");

    private static string Label(NodeDefinition node, NodeTypeDescriptor? descriptor) =>
        ScenarioValidator.DisplayName(node, descriptor);

    private ExpressionScope Scope(ProcessRecord process) => new(
        process.Variables,
        process.LastError,
        new JsonObject
        {
            ["id"] = process.Id,
            ["scenarioId"] = process.ScenarioId,
            ["scenarioName"] = process.ScenarioName,
            ["startedAt"] = process.CreatedAt.ToString("O"),
        });
}
