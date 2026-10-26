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
    Task<IReadOnlyList<ProcessRecord>> GetChildProcessesAsync(string parentProcessId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TokenRecord>> GetTokensAsync(string processId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TokenRecord>> GetTokensAsync(IReadOnlyCollection<string> processIds, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StepRecord>> GetStepsAsync(string processId, CancellationToken cancellationToken = default);
    Task<StepRecord?> GetStepAsync(long stepId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Последний шаг со снимком в текущем посещении блока маркером (для повторной попытки — снимок прежней): шаги прежних
    /// посещений (цикл) не учитываются, шаги без снимка (подготовка не удалась) пропускаются.
    /// </summary>
    Task<StepRecord?> GetLastCheckpointStepAsync(string processId, string tokenId, string nodeId, CancellationToken cancellationToken = default);

    /// <summary>Незавершённые шаги: «запланирован», «выполняется», «ожидает».</summary>
    Task<IReadOnlyList<StepRecord>> GetOpenStepsAsync(string processId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string processId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Процессы, которым есть что делать: «ждёт» с наступившим сроком, получившие событие или ресурс,
    /// «выполняется» с готовым маркером. Приостановленные и требующие решения сюда не попадают никогда.
    /// </summary>
    Task<IReadOnlyList<string>> GetRunnableProcessIdsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Последний запуск каждого сценария (для списка сценариев).</summary>
    Task<IReadOnlyDictionary<string, ProcessRecord>> GetLatestProcessPerScenarioAsync(CancellationToken cancellationToken = default);

    // События и подписки
    Task<EventRecord?> GetEventAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EventRecord>> ListEventsAsync(EventQuery query, CancellationToken cancellationToken = default);
    Task<SubscriptionRecord?> GetSubscriptionAsync(long id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubscriptionRecord>> GetActiveSubscriptionsAsync(string source, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubscriptionRecord>> GetSubscriptionsAsync(string processId, CancellationToken cancellationToken = default);

    // Ресурсы
    Task<IReadOnlyList<ResourceRecord>> ListResourcesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ResourceWaiterRecord>> ListResourceWaitersAsync(CancellationToken cancellationToken = default);

    // Триггеры
    Task<IReadOnlyList<TriggerRecord>> ListTriggersAsync(string? scenarioId = null, CancellationToken cancellationToken = default);
    Task<TriggerRecord?> GetTriggerAsync(string id, CancellationToken cancellationToken = default);

    // Настройки
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<IStoreWrite> BeginWriteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Транзакция записи. Без <see cref="CommitAsync"/> все изменения отменяются.
/// Методы чтения внутри транзакции видят её собственные изменения и не пересекаются с другими записями.
/// </summary>
public interface IStoreWrite : IAsyncDisposable
{
    Task SaveScenarioAsync(StoredScenario scenario, int? expectedVersion);
    Task<bool> DeleteScenarioAsync(string id);

    Task InsertProcessAsync(ProcessRecord process);
    Task UpdateProcessAsync(ProcessRecord process);
    Task<ProcessRecord?> GetProcessByOriginAsync(string originKey);
    Task<ProcessRecord?> GetProcessAsync(string id);
    Task<IReadOnlyList<TokenRecord>> GetTokensAsync(string processId);
    Task UpsertTokenAsync(TokenRecord token);

    /// <summary>Отменить незавершённые («запланирован», «ожидает») шаги процесса, кроме указанного.</summary>
    Task CancelOpenStepsAsync(string processId, long exceptStepId, string resolution, DateTimeOffset at);

    Task<long> InsertStepAsync(StepRecord step);
    Task UpdateStepAsync(StepRecord step);
    Task AddJournalAsync(JournalEntry entry);
    Task SetSettingAsync(string key, string value);

    // События: вставка с защитой от повторов (null — такое событие уже есть).
    Task<long?> TryInsertEventAsync(EventRecord record);
    Task<EventRecord?> FindEventByExternalIdAsync(string source, string externalId);
    /// <summary>Изменить событие, только если оно сейчас в состоянии <paramref name="expected"/>.</summary>
    Task<bool> TryUpdateEventAsync(EventRecord record, EventState expected);
    Task<EventRecord?> GetEventAsync(long id);
    Task<IReadOnlyList<EventRecord>> GetEventsForSubscriptionAsync(long subscriptionId);

    Task<long> InsertSubscriptionAsync(SubscriptionRecord record);

    /// <summary>Сменить состояние подписки, только если оно сейчас <paramref name="expected"/>.</summary>
    Task<bool> TrySetSubscriptionStateAsync(long id, SubscriptionState expected, SubscriptionState state, long? eventId);
    Task<SubscriptionRecord?> GetSubscriptionAsync(long id);
    Task<IReadOnlyList<SubscriptionRecord>> GetSubscriptionsAsync(string processId);

    // Ресурсы
    Task<ResourceRecord?> GetResourceAsync(string key);
    Task UpsertResourceAsync(ResourceRecord resource);
    Task<IReadOnlyList<ResourceRecord>> GetResourcesHeldByAsync(string processId);
    Task<IReadOnlyList<ResourceRecord>> GetAllResourcesAsync();

    /// <summary>Очередь за ресурсом по порядку (с состоянием процессов).</summary>
    Task<IReadOnlyList<ResourceWaiterRecord>> GetWaitersAsync(string resourceKey);
    Task<IReadOnlyList<ResourceWaiterRecord>> GetAllWaitersAsync();
    Task AddWaiterAsync(ResourceWaiterRecord waiter);
    /// <summary>Все места процесса в очереди ресурса (ресурс достался процессу — его получили все его ветви).</summary>
    Task RemoveWaiterAsync(string resourceKey, string processId);

    /// <summary>Место одной ветви (тайм-аут, «Пропустить», «Повторить») — места других ветвей остаются.</summary>
    Task RemoveTokenWaiterAsync(string resourceKey, string tokenId);
    Task RemoveWaitersOfProcessAsync(string processId);

    // Триггеры
    Task SaveTriggerAsync(TriggerRecord trigger);
    Task<TriggerRecord?> GetTriggerAsync(string id);
    Task<bool> DeleteTriggerAsync(string id);

    Task CommitAsync();
}
