using System.Text.Json.Nodes;
using Pepa.Core.Model;
using Pepa.Core.Nodes;
using Pepa.Core.Validation;
using Pepa.Engine;
using Pepa.Engine.Persistence;

namespace Pepa.Host.Api;

public sealed record ErrorDto(string Error, ValidationReport? Validation = null);

public sealed record ProcessBriefDto(string Id, ProcessStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

public sealed record ScenarioSummaryDto(
    string Id,
    string Name,
    string Description,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int NodeCount,
    int ErrorCount,
    int WarningCount,
    bool HasInputs,
    ProcessBriefDto? LastRun);

public sealed record ScenarioDto(
    string Id,
    string Name,
    string Description,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    ScenarioDefinition Definition,
    ValidationReport Validation);

public sealed record SaveScenarioRequest(ScenarioDefinition Definition, int ExpectedVersion);

public sealed record RunRequest(JsonObject? Inputs);

public sealed record RunResponse(string ProcessId);

public sealed record ResumeRequest(ResumeAction Action);

public sealed record MessageDto(string Message);

public sealed record ProcessSummaryDto(
    string Id,
    string ShortId,
    string ScenarioId,
    string ScenarioName,
    int ScenarioVersion,
    ProcessStatus Status,
    AttentionKind Attention,
    PauseReason PauseReason,
    string? StatusMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? FinishedAt,
    int StepCount,
    string? CurrentNodeId,
    string? CurrentNodeLabel,
    DateTimeOffset? WakeAt,
    IReadOnlyList<ProcessAction> AllowedActions);

public sealed record StepDto(
    long Id,
    string NodeId,
    string NodeType,
    string NodeLabel,
    int Attempt,
    NodeEffect Effect,
    StepState State,
    JsonObject? Input,
    JsonObject? Output,
    string? Port,
    string? Error,
    string? Resolution,
    DateTimeOffset PlannedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record JournalDto(long Id, long? StepId, string? NodeId, DateTimeOffset At, JournalLevel Level, string Message);

public sealed record ProcessDetailsDto(
    ProcessSummaryDto Process,
    JsonObject Variables,
    JsonObject? LastError,
    IReadOnlyList<StepDto> Steps,
    IReadOnlyList<JournalDto> Journal,
    ScenarioDefinition Definition);

public sealed record OverviewCountsDto(int Running, int Waiting, int Paused, int NeedsAttention, int CompletedToday, int FailedToday);

public sealed record OverviewDto(
    OverviewCountsDto Counts,
    IReadOnlyList<ProcessSummaryDto> Attention,
    IReadOnlyList<ProcessSummaryDto> Active,
    IReadOnlyList<ProcessSummaryDto> Recent);

public sealed record NodeTypesDto(IReadOnlyList<NodeCategory> Categories, IReadOnlyList<NodeTypeDescriptor> NodeTypes);

public sealed record InfoDto(
    string Version,
    string DataDirectory,
    string WorkspaceRoot,
    bool IsDesktop,
    DateTimeOffset StartedAt,
    RecoveryReport Recovery);

public sealed record SettingsDto(
    string WorkspaceRoot,
    int MaxParallelProcesses,
    int StepBudget,
    int ActionTimeoutSeconds,
    bool RestartRequired);
