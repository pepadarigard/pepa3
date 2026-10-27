using System.Text.Json.Nodes;
using Pepa.Core;
using Pepa.Core.Nodes;

namespace Pepa.Engine.Persistence;

/// <summary>Состояние экземпляра процесса (конкретного запуска сценария).</summary>
public enum ProcessStatus
{
    /// <summary>Выполняется или ждёт свободного исполнителя.</summary>
    Running,

    /// <summary>Ждёт таймер (в будущем — событие). Поток не занят.</summary>
    Waiting,

    /// <summary>Приостановлен пользователем или после перезапуска приложения.</summary>
    Paused,

    /// <summary>Остановлен до решения пользователя: неизвестный результат, ошибка без обработчика и т.п.</summary>
    NeedsAttention,

    Completed,
    Failed,
    Cancelled,
}

public enum AttentionKind
{
    None,

    /// <summary>Неизвестно, выполнилось ли последнее действие.</summary>
    UnknownStep,

    /// <summary>Шаг завершился ошибкой, обработчик не задан, повторы исчерпаны.</summary>
    StepFailed,

    /// <summary>Превышен лимит шагов — возможно, бесконечный цикл.</summary>
    StepLimit,

    /// <summary>Внутренняя ошибка платформы (например, блок недоступен в этой версии).</summary>
    EngineError,

    /// <summary>
    /// База восстановлена из резервной копии: что процесс сделал после копии, в базе нет — продолжать можно только после
    /// проверки пользователем.
    /// </summary>
    RestoredFromBackup,
}

public enum PauseReason
{
    None,
    User,

    /// <summary>Приложение было закрыто или перезапущено.</summary>
    Restart,

    /// <summary>Отладка: пауза перед блоком (точка останова или пошаговый запуск).</summary>
    Breakpoint,
}

/// <summary>Отладка процесса: пошаговый запуск и точки останова (настройка блока «Пауза перед блоком»).</summary>
public sealed record DebugState
{
    /// <summary>Пауза перед каждым блоком.</summary>
    public bool StepMode { get; init; }

    /// <summary>Пользователь выбрал «Продолжить без остановок»: точки останова этого процесса не действуют.</summary>
    public bool IgnoreBreakpoints { get; init; }

    /// <summary>Пауза перед этим шагом уже была («маркер:номер шага») — при продолжении шаг выполняется.</summary>
    public string? Passed { get; init; }
}

public sealed record ProcessRecord
{
    public required string Id { get; init; }
    public required string ScenarioId { get; init; }
    public required string ScenarioName { get; init; }
    public required int ScenarioVersion { get; init; }

    /// <summary>Снимок сценария на момент запуска: правки сценария не меняют уже запущенные процессы.</summary>
    public required string DefinitionJson { get; init; }

    public required ProcessStatus Status { get; init; }
    public AttentionKind Attention { get; init; }
    public PauseReason PauseReason { get; init; }

    /// <summary>Понятное объяснение текущего состояния.</summary>
    public string? StatusMessage { get; init; }

    /// <summary>Шаг, по которому нужно решение пользователя.</summary>
    public long? AttentionStepId { get; init; }

    /// <summary>Ветвь (маркер), которая остановилась и ждёт решения, — важно, если ветвей несколько.</summary>
    public string? AttentionTokenId { get; init; }

    public required JsonObject Variables { get; init; }
    public JsonObject? LastError { get; init; }

    /// <summary>Память блоков (позиция циклов и т.п.): ключ — идентификатор блока.</summary>
    public JsonObject? Memory { get; init; }

    /// <summary>
    /// Значения, которые в этом процессе секретны и не показываются: начальные и прежние значения секретных переменных,
    /// секреты вызвавшего процесса, переданные в этот, и секреты из полученных событий.
    /// </summary>
    public IReadOnlyList<string>? Secrets { get; init; }

    /// <summary>Отладка (null — обычный запуск).</summary>
    public DebugState? Debug { get; init; }

    /// <summary>Процесс, который запустил этот (вызов сценария).</summary>
    public string? ParentProcessId { get; init; }

    /// <summary>Уникальный ключ происхождения (расписание, событие, родительский шаг) — защита от двойного запуска.</summary>
    public string? OriginKey { get; init; }

    /// <summary>Глубина вложенности вызовов сценариев.</summary>
    public int Depth { get; init; }

    public int StepCount { get; init; }
    public required int StepBudget { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    public bool IsTerminal => Status is ProcessStatus.Completed or ProcessStatus.Failed or ProcessStatus.Cancelled;
}

public enum TokenState
{
    Ready,
    Waiting,
    Completed,
}

public enum WaitKind
{
    None,

    /// <summary>Ветвь пришла к слиянию и ждёт остальные ветви (WaitRef — id разделения).</summary>
    Join,

    /// <summary>Блок «Ожидание»: по пробуждению шаг завершается и маркер идёт дальше.</summary>
    Timer,

    /// <summary>Пауза перед повтором: по пробуждению шаг выполняется снова.</summary>
    Retry,

    /// <summary>Ожидание события (подписка) с тайм-аутом.</summary>
    Event,

    /// <summary>Очередь за ресурсом с тайм-аутом.</summary>
    Resource,
}

/// <summary>Маркер исполнения — «где сейчас процесс». В первой версии у процесса один маркер.</summary>
public sealed record TokenRecord
{
    public required string Id { get; init; }
    public required string ProcessId { get; init; }
    public required string NodeId { get; init; }
    public required TokenState State { get; init; }
    public WaitKind WaitKind { get; init; }
    public DateTimeOffset? WakeAt { get; init; }

    /// <summary>Номер попытки текущего шага (1 — первая).</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>Шаг в состоянии «ожидает», который завершится по пробуждению.</summary>
    public long? WaitingStepId { get; init; }

    /// <summary>Чего ждёт маркер: номер подписки или ключ ресурса.</summary>
    public string? WaitRef { get; init; }

    /// <summary>Из какого блока маркер пришёл в текущий (для циклов: вход «снаружи» или «по кругу»).</summary>
    public string? FromNodeId { get; init; }

    /// <summary>
    /// Параллельные участки, в которых маркер сейчас (вложенные — через «/»): «id-разделения:число-ветвей». Слияние ждёт
    /// маркеры с тем же последним участком. null — маркер вне параллельных участков.
    /// </summary>
    public string? Fork { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Состояние шага — главная гарантия от повторного выполнения.</summary>
public enum StepState
{
    /// <summary>Запланирован, ещё не начат.</summary>
    Planned,

    /// <summary>Выполняется: запись сделана ДО вызова внешнего действия.</summary>
    Running,

    /// <summary>Ждёт (таймер). Внешнее действие не выполняется.</summary>
    Waiting,

    /// <summary>Подтверждён выполненным.</summary>
    Succeeded,

    /// <summary>Подтверждён не выполненным.</summary>
    Failed,

    /// <summary>Результат неизвестен.</summary>
    Unknown,

    /// <summary>Пропущен по решению пользователя.</summary>
    Skipped,

    /// <summary>Отменён (не начинался или прерван остановкой процесса).</summary>
    Cancelled,
}

public sealed record StepRecord
{
    public long Id { get; init; }
    public required string ProcessId { get; init; }
    public required string TokenId { get; init; }
    public required string NodeId { get; init; }
    public required string NodeType { get; init; }
    public required string NodeLabel { get; init; }
    public required int Attempt { get; init; }
    public required NodeEffect Effect { get; init; }
    public required StepState State { get; init; }
    public JsonObject? Input { get; init; }
    public JsonObject? Checkpoint { get; init; }
    public JsonObject? Output { get; init; }
    public string? Port { get; init; }
    public string? Error { get; init; }

    /// <summary>Как разрешён неизвестный/ошибочный результат (проверка, решение пользователя).</summary>
    public string? Resolution { get; init; }

    public required DateTimeOffset PlannedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
}

public enum JournalLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>Запись журнала процесса: понятные человеку сообщения о важных событиях.</summary>
public sealed record JournalEntry
{
    public long Id { get; init; }
    public required string ProcessId { get; init; }
    public long? StepId { get; init; }
    public string? NodeId { get; init; }
    public required DateTimeOffset At { get; init; }
    public required JournalLevel Level { get; init; }
    public required string Message { get; init; }
}

public sealed record StoredScenario
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string DefinitionJson { get; init; }
    public required int Version { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ProcessQuery
{
    public IReadOnlyCollection<ProcessStatus>? Statuses { get; init; }
    public string? ScenarioId { get; init; }
    public DateTimeOffset? CreatedAfter { get; init; }
    public int Limit { get; init; } = 200;
}

public sealed class ConcurrencyException(string message) : Exception(message);

// ───────────────────────────── События ─────────────────────────────

public enum EventState
{
    /// <summary>Принято, ещё не сопоставлено.</summary>
    Received,

    /// <summary>Назначено подписке процесса, ждёт применения (например, пока процесс на паузе).</summary>
    Matched,

    /// <summary>Применено: процесс получил событие или по нему запущен новый процесс.</summary>
    Delivered,

    /// <summary>Подходящих процессов нет. Ничего не запускалось.</summary>
    Unmatched,

    /// <summary>Подходит нескольким процессам одинаково — нужно решение пользователя.</summary>
    Conflict,

    /// <summary>Пользователь решил не обрабатывать.</summary>
    Ignored,
}

public sealed record EventRecord
{
    public long Id { get; init; }
    public required string Source { get; init; }
    public required string Type { get; init; }
    public string? ExternalId { get; init; }
    public string? CorrelationKey { get; init; }
    public JsonNode? Payload { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Когда событие произошло у источника (например, время сообщения в Telegram); null — тогда же, когда принято.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    public required EventState State { get; init; }
    public string? ProcessId { get; init; }
    public long? SubscriptionId { get; init; }

    /// <summary>При конфликте — подходящие подписки: [{subscriptionId, processId}].</summary>
    public JsonArray? Candidates { get; init; }

    /// <summary>Процессы, запущенные по этому событию триггерами.</summary>
    public JsonArray? StartedProcesses { get; init; }

    public string? Note { get; init; }
    public DateTimeOffset? HandledAt { get; init; }

    /// <summary>Секретные значения внутри события (от процесса, который его отправил): при показе — «••••», получателю — тоже секретны.</summary>
    public IReadOnlyList<string>? Secrets { get; init; }
}

public sealed record EventQuery
{
    public IReadOnlyCollection<EventState>? States { get; init; }
    public string? ProcessId { get; init; }
    public int Limit { get; init; } = 200;
}

public enum SubscriptionState
{
    Active,
    Fulfilled,
    Expired,
    Cancelled,
}

/// <summary>Ожидание события конкретным шагом процесса.</summary>
public sealed record SubscriptionRecord
{
    public long Id { get; init; }
    public required string ProcessId { get; init; }
    public required string TokenId { get; init; }
    public required long StepId { get; init; }
    public required string NodeId { get; init; }
    public required string Source { get; init; }
    public string? EventType { get; init; }
    public string? CorrelationKey { get; init; }
    public string? MatchExpression { get; init; }
    public int Priority { get; init; }
    public int LookbackSeconds { get; init; }
    public string? SaveTo { get; init; }
    public SubscriptionPurpose Purpose { get; init; }
    public required SubscriptionState State { get; init; }
    public long? EventId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

// ───────────────────────────── Ресурсы ─────────────────────────────

/// <summary>Общий ресурс, которым одновременно может пользоваться только один процесс.</summary>
public sealed record ResourceRecord
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string? HolderProcessId { get; init; }
    public DateTimeOffset? AcquiredAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ResourceWaiterRecord
{
    public long Id { get; init; }
    public required string ResourceKey { get; init; }
    public required string ProcessId { get; init; }
    public required string TokenId { get; init; }
    public int Priority { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>Состояние процесса-ожидающего (заполняется при чтении).</summary>
    public ProcessStatus? ProcessStatus { get; init; }
}

// ───────────────────────────── Триггеры ─────────────────────────────

public enum TriggerKind
{
    /// <summary>По расписанию.</summary>
    Schedule,

    /// <summary>По входящему событию.</summary>
    Event,
}

public sealed record TriggerRecord
{
    public required string Id { get; init; }
    public required string ScenarioId { get; init; }
    public required TriggerKind Kind { get; init; }
    public bool Enabled { get; init; } = true;
    public string Name { get; init; } = "";

    /// <summary>Расписание или правило события.</summary>
    public required JsonObject Config { get; init; }

    /// <summary>Значения входных переменных; для событий — выражения от <c>event</c>.</summary>
    public JsonObject? Inputs { get; init; }

    /// <summary>«skip» — не запускать, пока работает предыдущий запуск; «allow» — запускать всегда.</summary>
    public string Overlap { get; init; } = "skip";

    public DateTimeOffset? NextRunAt { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
    public string? LastProcessId { get; init; }

    /// <summary>Сколько запусков пропущено, пока Pepa была закрыта (не выполняются автоматически).</summary>
    public int MissedRuns { get; init; }

    public DateTimeOffset? LastMissedAt { get; init; }

    /// <summary>Сколько запусков пропущено, потому что предыдущий ещё работал.</summary>
    public int SkippedRuns { get; init; }

    public string? LastError { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}
