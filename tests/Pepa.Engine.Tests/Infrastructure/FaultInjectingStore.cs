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
    public Task<IReadOnlyList<StepRecord>> GetOpenStepsAsync(string processId, CancellationToken cancellationToken = default) => inner.GetOpenStepsAsync(processId, cancellationToken);
    public Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string processId, CancellationToken cancellationToken = default) => inner.GetJournalAsync(processId, cancellationToken);
    public Task<IReadOnlyList<string>> GetRunnableProcessIdsAsync(DateTimeOffset now, CancellationToken cancellationToken = default) => inner.GetRunnableProcessIdsAsync(now, cancellationToken);
    public Task<IReadOnlyDictionary<string, ProcessRecord>> GetLatestProcessPerScenarioAsync(CancellationToken cancellationToken = default) => inner.GetLatestProcessPerScenarioAsync(cancellationToken);
    public Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default) => inner.GetSettingsAsync(cancellationToken);

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
