namespace Pepa.Engine.Persistence;

/// <summary>
/// Долговременное хранилище. Чтение — без блокировок; любые изменения — только через
/// <see cref="BeginWriteAsync"/>, одной транзакцией, строго по одной за раз.
/// </summary>
public interface IPepaStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    // Сценарии
    Task<IReadOnlyList<StoredScenario>> ListScenariosAsync(CancellationToken cancellationToken = default);
    Task<StoredScenario?> GetScenarioAsync(string id, CancellationToken cancellationToken = default);

    // Процессы
    Task<ProcessRecord?> GetProcessAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProcessRecord>> ListProcessesAsync(ProcessQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TokenRecord>> GetTokensAsync(string processId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TokenRecord>> GetTokensAsync(IReadOnlyCollection<string> processIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StepRecord>> GetStepsAsync(string processId, CancellationToken cancellationToken = default);
    Task<StepRecord?> GetStepAsync(long stepId, CancellationToken cancellationToken = default);

    /// <summary>Незавершённые шаги: «запланирован», «выполняется», «ожидает».</summary>
    Task<IReadOnlyList<StepRecord>> GetOpenStepsAsync(string processId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string processId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Процессы, которым есть что делать: «ждёт» с наступившим сроком или «выполняется» с готовым маркером.
    /// Приостановленные и требующие решения сюда не попадают никогда.
    /// </summary>
    Task<IReadOnlyList<string>> GetRunnableProcessIdsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Последний запуск каждого сценария (для списка сценариев).</summary>
    Task<IReadOnlyDictionary<string, ProcessRecord>> GetLatestProcessPerScenarioAsync(CancellationToken cancellationToken = default);

    // Настройки
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<IStoreWrite> BeginWriteAsync(CancellationToken cancellationToken = default);
}

/// <summary>Транзакция записи. Без <see cref="CommitAsync"/> все изменения отменяются.</summary>
public interface IStoreWrite : IAsyncDisposable
{
    Task SaveScenarioAsync(StoredScenario scenario, int? expectedVersion);
    Task<bool> DeleteScenarioAsync(string id);

    Task InsertProcessAsync(ProcessRecord process);
    Task UpdateProcessAsync(ProcessRecord process);
    Task UpsertTokenAsync(TokenRecord token);
    Task<long> InsertStepAsync(StepRecord step);
    Task UpdateStepAsync(StepRecord step);
    Task AddJournalAsync(JournalEntry entry);
    Task SetSettingAsync(string key, string value);

    Task CommitAsync();
}
