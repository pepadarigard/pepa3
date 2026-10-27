using Pepa.Core.Validation;

namespace Pepa.Engine;

public sealed record EngineOptions
{
    /// <summary>Сколько процессов могут выполнять шаги одновременно.</summary>
    public int MaxParallelProcesses { get; init; } = 4;

    /// <summary>Сколько шагов процесс может сделать, прежде чем движок спросит пользователя (защита от бесконечных циклов).</summary>
    public int DefaultStepBudget { get; init; } = 10_000;

    /// <summary>Тайм-аут действия по умолчанию.</summary>
    public TimeSpan DefaultActionTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>После стольких шагов подряд процесс уступает исполнителя другим.</summary>
    public int StepsPerSlice { get; init; } = 50;

    /// <summary>Как часто проверять наступление сроков ожидания.</summary>
    public TimeSpan TimerInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Запуск по расписанию, опоздавший больше чем на это время (например, компьютер спал), не выполняется,
    /// а учитывается как пропущенный — как запуски, пропущенные при закрытом приложении.
    /// </summary>
    public TimeSpan ScheduleLateGrace { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ожидание события, срок которого истёк, пока Pepa была закрыта, завершается по тайм-ауту не сразу после запуска,
    /// а спустя это время: источники успевают догрузить события, пришедшие вовремя.
    /// </summary>
    public TimeSpan CatchUpGrace { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Дольше этого после запуска тайм-ауты не ждут источник, который ещё догружает события (например, нет связи с Telegram).
    /// </summary>
    public TimeSpan MaxCatchUpWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Сколько ждать завершения выполняющихся шагов при закрытии приложения.</summary>
    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Решение пользователя для приостановленного или требующего внимания процесса.</summary>
public enum ResumeAction
{
    /// <summary>Продолжить (после паузы, перезапуска или лимита шагов).</summary>
    Continue,

    /// <summary>Проверить, выполнилось ли действие с неизвестным результатом, и продолжить по итогам.</summary>
    Verify,

    /// <summary>Выполнить шаг ещё раз.</summary>
    Retry,

    /// <summary>Считать шаг с неизвестным результатом выполненным и идти дальше.</summary>
    MarkSucceeded,

    /// <summary>Считать шаг с неизвестным результатом невыполненным: ветка «Ошибка» или остановка с ошибкой.</summary>
    MarkFailed,

    /// <summary>Пропустить шаг с ошибкой и идти дальше.</summary>
    Skip,

    /// <summary>Отладка: продолжить без пауз перед блоками (пошаговый режим и точки останова выключаются для процесса).</summary>
    RunWithoutPauses,
}

/// <summary>Действия, доступные над процессом. Интерфейс показывает только их.</summary>
public enum ProcessAction
{
    Pause,
    Cancel,
    Continue,
    Verify,
    Retry,
    MarkSucceeded,
    Skip,
    RunWithoutPauses,
    MarkFailed,
}

public sealed record EngineChange(string ProcessId);

public sealed record ControlResult(string Message);

/// <summary>Операция недопустима в текущем состоянии; сообщение предназначено пользователю.</summary>
public sealed class EngineOperationException(string message) : Exception(message);

public sealed class ScenarioInvalidException(ValidationReport report)
    : Exception("Сценарий содержит ошибки и не может быть запущен.")
{
    public ValidationReport Report { get; } = report;
}
