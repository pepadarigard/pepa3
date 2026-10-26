using Pepa.Core.Validation;
using Pepa.Engine;
using Pepa.Engine.Persistence;

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
        return processes.Select(p => Summary(p, tokens.GetValueOrDefault(p.Id) ?? [])).ToList();
    }

    public ProcessSummaryDto Summary(ProcessRecord process, IReadOnlyList<TokenRecord> tokens)
    {
        var token = tokens.FirstOrDefault(t => t.State != TokenState.Completed);
        string? label = null;
        if (token is not null)
        {
            var node = engine.Graph(process).FindNode(token.NodeId);
            if (node is not null)
            {
                label = ScenarioValidator.DisplayName(node, engine.Registry.FindDescriptor(node.Type)).Trim('«', '»');
            }
        }

        return new ProcessSummaryDto(
            process.Id,
            process.Id[^6..],
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
            token?.State == TokenState.Waiting ? token.WakeAt : null,
            engine.GetAllowedActions(process, tokens));
    }

    public static StepDto Step(StepRecord s) => new(s.Id, s.NodeId, s.NodeType, s.NodeLabel, s.Attempt, s.Effect, s.State,
        s.Input, s.Output, s.Port, s.Error, s.Resolution, s.PlannedAt, s.StartedAt, s.FinishedAt);

    public static JournalDto Journal(JournalEntry j) => new(j.Id, j.StepId, j.NodeId, j.At, j.Level, j.Message);
}
