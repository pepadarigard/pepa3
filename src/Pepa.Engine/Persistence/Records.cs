using System.Text.Json.Nodes;
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
}

public enum PauseReason
{
    None,
    User,

    /// <summary>Приложение было закрыто или перезапущено.</summary>
    Restart,
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

    public required JsonObject Variables { get; init; }
    public JsonObject? LastError { get; init; }
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

    /// <summary>Блок «Ожидание»: по пробуждению шаг завершается и маркер идёт дальше.</summary>
    Timer,

    /// <summary>Пауза перед повтором: по пробуждению шаг выполняется снова.</summary>
    Retry,
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
