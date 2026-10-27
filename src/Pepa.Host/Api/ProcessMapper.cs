using System.Globalization;
using Pepa.Core;
using Pepa.Core.Validation;
using Pepa.Engine;
using Pepa.Engine.Persistence;
using Pepa.Engine.Scheduling;

namespace Pepa.Host.Api;

/// <summary>Превращает записи хранилища в ответы API с понятными для интерфейса полями.</summary>
internal sealed class ProcessMapper(ProcessEngine engine, IPepaStore store)
{
    public async Task<IReadOnlyList<ProcessSummaryDto>> SummariesAsync(IReadOnlyList<ProcessRecord> processes,
        CancellationToken cancellationToken)
    {
        if (processes.Count == 0)
        {
            return [];
        }

        var tokens = (await store.GetTokensAsync(processes.Select(p => p.Id).ToList(), cancellationToken))
            .GroupBy(t => t.ProcessId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TokenRecord>)g.ToList());
        var subscriptions = new Dictionary<long, SubscriptionRecord>();
        foreach (var token in tokens.Values.SelectMany(t => t).Where(t => t is { State: TokenState.Waiting, WaitKind: WaitKind.Event }))
        {
            if (long.TryParse(token.WaitRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                await store.GetSubscriptionAsync(id, cancellationToken) is { } subscription)
            {
                subscriptions[id] = subscription;
            }
        }

        return processes.Select(p => Summary(p, tokens.GetValueOrDefault(p.Id) ?? [], subscriptions)).ToList();
    }

    public ProcessSummaryDto Summary(ProcessRecord process, IReadOnlyList<TokenRecord> tokens,
        IReadOnlyDictionary<long, SubscriptionRecord>? subscriptions = null)
    {
        // С параллельными ветвями — та, что ждёт решения, иначе та, что работает, иначе любая незавершённая.
        var active = tokens.Where(t => t.State != TokenState.Completed).ToList();
        var token = active.FirstOrDefault(t => t.Id == process.AttentionTokenId)
                    ?? active.FirstOrDefault(t => t.State == TokenState.Ready)
                    ?? active.FirstOrDefault(t => t.WaitKind != WaitKind.Join)
                    ?? active.FirstOrDefault();
        string? label = null;
        if (token is not null)
        {
            var node = engine.Graph(process).FindNode(token.NodeId);
            if (node is not null)
            {
                label = ScenarioValidator.DisplayName(node, engine.Registry.FindDescriptor(node.Type)).Trim('«', '»');
            }
        }

        var waiting = token?.State == TokenState.Waiting ? token : null;
        return new ProcessSummaryDto(
            process.Id,
            ShortId(process.Id),
            process.ScenarioId,
            process.ScenarioName,
            process.ScenarioVersion,
            process.Status,
            process.Attention,
            process.PauseReason,
            process.StatusMessage,
            process.CreatedAt,
            process.UpdatedAt,
            process.FinishedAt,
            process.StepCount,
            token?.NodeId,
            label,
            waiting?.WakeAt,
            engine.GetAllowedActions(process, tokens),
            waiting?.WaitKind ?? WaitKind.None,
            waiting is null || process.IsTerminal ? null : DescribeWait(process, waiting, subscriptions),
            process.ParentProcessId);
    }

    private static string? DescribeWait(ProcessRecord process, TokenRecord token,
        IReadOnlyDictionary<long, SubscriptionRecord>? subscriptions)
    {
        switch (token.WaitKind)
        {
            case WaitKind.Retry:
                return "повтор после ошибки";
            case WaitKind.Resource:
                return $"ресурс «{token.WaitRef}»";
            case WaitKind.Event:
            {
                SubscriptionRecord? subscription = null;
                if (long.TryParse(token.WaitRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    subscriptions?.TryGetValue(id, out subscription);
                }

                if (subscription is null)
                {
                    return "событие";
                }

                var later = process.Status is ProcessStatus.Paused or ProcessStatus.NeedsAttention ? " — применится после возобновления" : "";
                if (subscription.Purpose == SubscriptionPurpose.ChildProcess)
                {
                    return subscription.State == SubscriptionState.Fulfilled
                        ? "вызванный сценарий завершился" + later
                        : $"завершение вызванного сценария (процесс #{ShortId(subscription.CorrelationKey ?? "")})";
                }

                return subscription.State == SubscriptionState.Fulfilled
                    ? "событие получено" + later
                    : DescribeSubscription(subscription);
            }

            default:
                return null;
        }
    }

    public static string DescribeSubscription(SubscriptionRecord subscription) =>
        $"событие «{subscription.Source}" + (subscription.EventType is { Length: > 0 } type ? $" / {type}" : "") + "»" +
        (subscription.CorrelationKey is { Length: > 0 } key ? $" с ключом «{key}»" : "");

    public static ProcessRefDto Ref(ProcessRecord process) =>
        new(process.Id, ShortId(process.Id), process.ScenarioName, process.Status);

    public static string ShortId(string id) => id.Length > 6 ? id[^6..] : id;

    public static WaitDto Wait(SubscriptionRecord s) => new(s.Id, s.NodeId, s.Source, s.EventType, s.CorrelationKey,
        s.MatchExpression, s.Priority, s.Purpose, s.State, s.EventId, s.CreatedAt, s.ExpiresAt);

    public static StepDto Step(StepRecord s) => new(s.Id, s.NodeId, s.NodeType, s.NodeLabel, s.Attempt, s.Effect, s.State,
        s.Input, s.Output, s.Port, s.Error, s.Resolution, s.PlannedAt, s.StartedAt, s.FinishedAt);

    public static JournalDto Journal(JournalEntry j) => new(j.Id, j.StepId, j.NodeId, j.At, j.Level, j.Message);

    // ───────────── События ─────────────

    public async Task<IReadOnlyList<EventDto>> EventsAsync(IReadOnlyList<EventRecord> records, CancellationToken cancellationToken)
    {
        var processes = new Dictionary<string, ProcessRecord?>(StringComparer.Ordinal);
        async Task<ProcessRecord?> Load(string id)
        {
            if (!processes.TryGetValue(id, out var process))
            {
                processes[id] = process = await store.GetProcessAsync(id, cancellationToken);
            }

            return process;
        }

        var result = new List<EventDto>(records.Count);
        foreach (var record in records)
        {
            var owner = record.ProcessId is { } processId ? await Load(processId) : null;
            var candidates = new List<EventCandidateDto>();
            foreach (var candidate in record.Candidates?.OfType<System.Text.Json.Nodes.JsonObject>() ?? [])
            {
                if (candidate.GetString("processId") is not { } candidateProcessId || candidate.GetNumber("subscriptionId") is not { } subscriptionId)
                {
                    continue;
                }

                var process = await Load(candidateProcessId);
                if (process is null)
                {
                    continue;
                }

                var subscription = await store.GetSubscriptionAsync((long)subscriptionId, cancellationToken);
                candidates.Add(new EventCandidateDto((long)subscriptionId, Ref(process),
                    subscription?.State == SubscriptionState.Active && !process.IsTerminal));
            }

            result.Add(new EventDto(record.Id, record.Source, record.Type, record.ExternalId, record.CorrelationKey,
                record.Payload?.DeepClone(), record.ReceivedAt, record.State, owner is null ? null : Ref(owner), candidates,
                record.StartedProcesses?.Select(n => n?.GetValue<string>()).OfType<string>().ToList() ?? [],
                record.Note, record.HandledAt));
        }

        return result;
    }

    // ───────────── Ресурсы ─────────────

    public async Task<IReadOnlyList<ResourceDto>> ResourcesAsync(CancellationToken cancellationToken)
    {
        var resources = await store.ListResourcesAsync(cancellationToken);
        var waiters = (await store.ListResourceWaitersAsync(cancellationToken)).ToLookup(w => w.ResourceKey);
        var result = new List<ResourceDto>(resources.Count);
        foreach (var resource in resources)
        {
            var holder = resource.HolderProcessId is { } holderId ? await store.GetProcessAsync(holderId, cancellationToken) : null;
            var queue = new List<ResourceWaiterDto>();
            foreach (var waiter in waiters[resource.Key])
            {
                if (await store.GetProcessAsync(waiter.ProcessId, cancellationToken) is { } process)
                {
                    queue.Add(new ResourceWaiterDto(Ref(process), waiter.RequestedAt));
                }
            }

            result.Add(new ResourceDto(resource.Key, resource.Title, holder is null ? null : Ref(holder), resource.AcquiredAt,
                resource.UpdatedAt, queue, ProcessEngine.ExternalHolderTitle(resource.HolderProcessId)));
        }

        return result;
    }

    // ───────────── Триггеры ─────────────

    public async Task<IReadOnlyList<TriggerDto>> TriggersAsync(IReadOnlyList<TriggerRecord> triggers, CancellationToken cancellationToken)
    {
        var names = (await store.ListScenariosAsync(cancellationToken)).ToDictionary(s => s.Id, s => s.Name, StringComparer.Ordinal);
        return triggers.Select(t => Trigger(t, names.GetValueOrDefault(t.ScenarioId) ?? "Сценарий удалён")).ToList();
    }

    public static TriggerDto Trigger(TriggerRecord t, string scenarioName) => new(t.Id, t.ScenarioId, scenarioName, t.Kind,
        t.Enabled, t.Name, t.Config, t.Inputs, t.Overlap, DescribeTrigger(t), t.NextRunAt, t.LastRunAt, t.LastProcessId,
        t.MissedRuns, t.LastMissedAt, t.SkippedRuns, t.LastError, t.CreatedAt, t.UpdatedAt);

    public static string DescribeTrigger(TriggerRecord trigger)
    {
        if (trigger.Kind == TriggerKind.Schedule)
        {
            return ScheduleCalculator.Describe(trigger.Config);
        }

        var source = trigger.Config.GetString("source") ?? "";
        var type = trigger.Config.GetString("eventType");
        return $"событие «{source}" + (string.IsNullOrWhiteSpace(type) ? "" : $" / {type}") + "»" +
               (string.IsNullOrWhiteSpace(trigger.Config.GetString("filter")) ? "" : " с условием");
    }
}
