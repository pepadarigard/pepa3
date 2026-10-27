using Pepa.Engine.Persistence;

namespace Pepa.Engine.Tests.Infrastructure;

/// <summary>
/// Обёртка над настоящим хранилищем, которая по условию «роняет» запись — так имитируется сбой
/// (выключение питания, падение процесса) в заданный момент.
/// </summary>
public sealed class FaultInjectingStore(IPepaStore inner) : IPepaStore
{
    /// <summary>Условие сбоя: проверяется на каждой записи шага. true — транзакция не будет подтверждена.</summary>
    public Func<StepRecord, bool>? FailWhenStep { get; set; }

    /// <summary>После первого сбоя все последующие записи тоже падают (жёсткий сбой).</summary>
    public bool FailEverythingAfterFirstFault { get; set; } = true;

    public bool Tripped { get; private set; }

    internal void Trip() => Tripped = true;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => inner.InitializeAsync(cancellationToken);
    public Task<IReadOnlyList<StoredScenario>> ListScenariosAsync(CancellationToken cancellationToken = default) => inner.ListScenariosAsync(cancellationToken);
    public Task<StoredScenario?> GetScenarioAsync(string id, CancellationToken cancellationToken = default) => inner.GetScenarioAsync(id, cancellationToken);
    public Task<ProcessRecord?> GetProcessAsync(string id, CancellationToken cancellationToken = default) => inner.GetProcessAsync(id, cancellationToken);
    public Task<IReadOnlyList<ProcessRecord>> ListProcessesAsync(ProcessQuery query, CancellationToken cancellationToken = default) => inner.ListProcessesAsync(query, cancellationToken);
    public Task<IReadOnlyList<TokenRecord>> GetTokensAsync(string processId, CancellationToken cancellationToken = default) => inner.GetTokensAsync(processId, cancellationToken);
    public Task<IReadOnlyList<TokenRecord>> GetTokensAsync(IReadOnlyCollection<string> processIds, CancellationToken cancellationToken = default) => inner.GetTokensAsync(processIds, cancellationToken);
    public Task<IReadOnlyList<StepRecord>> GetStepsAsync(string processId, CancellationToken cancellationToken = default) => inner.GetStepsAsync(processId, cancellationToken);
    public Task<StepRecord?> GetStepAsync(long stepId, CancellationToken cancellationToken = default) => inner.GetStepAsync(stepId, cancellationToken);
    public Task<StepRecord?> GetLastCheckpointStepAsync(string processId, string tokenId, string nodeId, CancellationToken cancellationToken = default) =>
        inner.GetLastCheckpointStepAsync(processId, tokenId, nodeId, cancellationToken);

    public Task<bool> HasUncertainAttemptAsync(string processId, string tokenId, string nodeId, CancellationToken cancellationToken = default) =>
        inner.HasUncertainAttemptAsync(processId, tokenId, nodeId, cancellationToken);
    public Task<IReadOnlyList<StepRecord>> GetOpenStepsAsync(string processId, CancellationToken cancellationToken = default) => inner.GetOpenStepsAsync(processId, cancellationToken);
    public Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string processId, CancellationToken cancellationToken = default) => inner.GetJournalAsync(processId, cancellationToken);
    public Task<IReadOnlyList<string>> GetRunnableProcessIdsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => inner.GetRunnableProcessIdsAsync(now, cancellationToken);
    public Task<IReadOnlyDictionary<string, ProcessRecord>> GetLatestProcessPerScenarioAsync(CancellationToken cancellationToken = default) => inner.GetLatestProcessPerScenarioAsync(cancellationToken);
    public Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default) => inner.GetSettingsAsync(cancellationToken);
    public Task<IReadOnlyList<ProcessRecord>> GetChildProcessesAsync(string parentProcessId, CancellationToken cancellationToken = default) => inner.GetChildProcessesAsync(parentProcessId, cancellationToken);
    public Task<EventRecord?> GetEventAsync(long id, CancellationToken cancellationToken = default) => inner.GetEventAsync(id, cancellationToken);
    public Task<IReadOnlyList<EventRecord>> ListEventsAsync(EventQuery query, CancellationToken cancellationToken = default) => inner.ListEventsAsync(query, cancellationToken);
    public Task<SubscriptionRecord?> GetSubscriptionAsync(long id, CancellationToken cancellationToken = default) => inner.GetSubscriptionAsync(id, cancellationToken);
    public Task<IReadOnlyList<SubscriptionRecord>> GetActiveSubscriptionsAsync(string source, CancellationToken cancellationToken = default) => inner.GetActiveSubscriptionsAsync(source, cancellationToken);
    public Task<IReadOnlyList<SubscriptionRecord>> GetSubscriptionsAsync(string processId, CancellationToken cancellationToken = default) => inner.GetSubscriptionsAsync(processId, cancellationToken);
    public Task<IReadOnlyList<ResourceRecord>> ListResourcesAsync(CancellationToken cancellationToken = default) => inner.ListResourcesAsync(cancellationToken);
    public Task<IReadOnlyList<ResourceWaiterRecord>> ListResourceWaitersAsync(CancellationToken cancellationToken = default) => inner.ListResourceWaitersAsync(cancellationToken);
    public Task<IReadOnlyList<TriggerRecord>> ListTriggersAsync(string? scenarioId = null, CancellationToken cancellationToken = default) => inner.ListTriggersAsync(scenarioId, cancellationToken);
    public Task<TriggerRecord?> GetTriggerAsync(string id, CancellationToken cancellationToken = default) => inner.GetTriggerAsync(id, cancellationToken);

    public async Task<IStoreWrite> BeginWriteAsync(CancellationToken cancellationToken = default) =>
        new FaultInjectingWrite(await inner.BeginWriteAsync(cancellationToken), this);

    private sealed class FaultInjectingWrite(IStoreWrite inner, FaultInjectingStore owner) : IStoreWrite
    {
        private bool _fail;

        public Task SaveScenarioAsync(StoredScenario scenario, int? expectedVersion) => inner.SaveScenarioAsync(scenario, expectedVersion);
        public Task<bool> DeleteScenarioAsync(string id) => inner.DeleteScenarioAsync(id);
        public Task InsertProcessAsync(ProcessRecord process) => inner.InsertProcessAsync(process);
        public Task UpdateProcessAsync(ProcessRecord process) => inner.UpdateProcessAsync(process);
        public Task UpsertTokenAsync(TokenRecord token) => inner.UpsertTokenAsync(token);
        public Task<IReadOnlyList<TokenRecord>> GetTokensAsync(string processId) => inner.GetTokensAsync(processId);

        public Task CancelOpenStepsAsync(string processId, long exceptStepId, string resolution, DateTimeOffset at) =>
            inner.CancelOpenStepsAsync(processId, exceptStepId, resolution, at);

        public Task<long> InsertStepAsync(StepRecord step)
        {
            Check(step);
            return inner.InsertStepAsync(step);
        }

        public Task UpdateStepAsync(StepRecord step)
        {
            Check(step);
            return inner.UpdateStepAsync(step);
        }

        public Task AddJournalAsync(JournalEntry entry) => inner.AddJournalAsync(entry);
        public Task SetSettingAsync(string key, string value) => inner.SetSettingAsync(key, value);
        public Task<ProcessRecord?> GetProcessByOriginAsync(string originKey) => inner.GetProcessByOriginAsync(originKey);
        public Task<ProcessRecord?> GetProcessAsync(string id) => inner.GetProcessAsync(id);
        public Task<long?> TryInsertEventAsync(EventRecord record) => inner.TryInsertEventAsync(record);
        public Task<EventRecord?> FindEventByExternalIdAsync(string source, string externalId) => inner.FindEventByExternalIdAsync(source, externalId);
        public Task<bool> TryUpdateEventAsync(EventRecord record, EventState expected) => inner.TryUpdateEventAsync(record, expected);
        public Task<EventRecord?> GetEventAsync(long id) => inner.GetEventAsync(id);
        public Task<TriggerRecord?> GetTriggerAsync(string id) => inner.GetTriggerAsync(id);
        public Task<IReadOnlyList<EventRecord>> GetEventsForSubscriptionAsync(long subscriptionId) => inner.GetEventsForSubscriptionAsync(subscriptionId);
        public Task<long> InsertSubscriptionAsync(SubscriptionRecord record) => inner.InsertSubscriptionAsync(record);
        public Task<bool> TrySetSubscriptionStateAsync(long id, SubscriptionState expected, SubscriptionState state, long? eventId) => inner.TrySetSubscriptionStateAsync(id, expected, state, eventId);
        public Task<SubscriptionRecord?> GetSubscriptionAsync(long id) => inner.GetSubscriptionAsync(id);
        public Task<IReadOnlyList<SubscriptionRecord>> GetSubscriptionsAsync(string processId) => inner.GetSubscriptionsAsync(processId);
        public Task<ResourceRecord?> GetResourceAsync(string key) => inner.GetResourceAsync(key);
        public Task UpsertResourceAsync(ResourceRecord resource) => inner.UpsertResourceAsync(resource);
        public Task<IReadOnlyList<ResourceRecord>> GetResourcesHeldByAsync(string processId) => inner.GetResourcesHeldByAsync(processId);
        public Task<IReadOnlyList<ResourceRecord>> GetAllResourcesAsync() => inner.GetAllResourcesAsync();
        public Task<IReadOnlyList<ResourceWaiterRecord>> GetWaitersAsync(string resourceKey) => inner.GetWaitersAsync(resourceKey);
        public Task<IReadOnlyList<ResourceWaiterRecord>> GetAllWaitersAsync() => inner.GetAllWaitersAsync();
        public Task AddWaiterAsync(ResourceWaiterRecord waiter) => inner.AddWaiterAsync(waiter);
        public Task RemoveWaiterAsync(string resourceKey, string processId) => inner.RemoveWaiterAsync(resourceKey, processId);
        public Task RemoveTokenWaiterAsync(string resourceKey, string tokenId) => inner.RemoveTokenWaiterAsync(resourceKey, tokenId);
        public Task RemoveWaitersOfProcessAsync(string processId) => inner.RemoveWaitersOfProcessAsync(processId);
        public Task SaveTriggerAsync(TriggerRecord trigger) => inner.SaveTriggerAsync(trigger);
        public Task<bool> DeleteTriggerAsync(string id) => inner.DeleteTriggerAsync(id);

        public Task CommitAsync()
        {
            if (_fail || (owner.Tripped && owner.FailEverythingAfterFirstFault))
            {
                owner.Trip();
                throw new IOException("Имитация сбоя: запись не подтверждена.");
            }

            return inner.CommitAsync();
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private void Check(StepRecord step)
        {
            if (owner.FailWhenStep?.Invoke(step) == true)
            {
                _fail = true;
            }
        }
    }
}
