using System.Text.Json.Nodes;
using Pepa.Core;
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
    IReadOnlyList<ProcessAction> AllowedActions,
    WaitKind WaitKind,
    string? WaitingFor,
    string? ParentProcessId);

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
    ScenarioDefinition Definition,
    ProcessRefDto? Parent,
    IReadOnlyList<ProcessSummaryDto> Children,
    IReadOnlyList<WaitDto> Waits,
    IReadOnlyList<string> HeldResources);

/// <summary>Краткая ссылка на процесс.</summary>
public sealed record ProcessRefDto(string Id, string ShortId, string ScenarioName, ProcessStatus Status);

/// <summary>Чего ждал или ждёт шаг процесса.</summary>
public sealed record WaitDto(
    long Id,
    string NodeId,
    string Source,
    string? EventType,
    string? CorrelationKey,
    string? MatchExpression,
    int Priority,
    SubscriptionPurpose Purpose,
    SubscriptionState State,
    long? EventId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

// ───────────── События ─────────────

public sealed record EventCandidateDto(long SubscriptionId, ProcessRefDto Process, bool StillWaiting);

public sealed record EventDto(
    long Id,
    string Source,
    string Type,
    string? ExternalId,
    string? CorrelationKey,
    JsonNode? Payload,
    DateTimeOffset ReceivedAt,
    EventState State,
    ProcessRefDto? Process,
    IReadOnlyList<EventCandidateDto> Candidates,
    IReadOnlyList<string> StartedProcesses,
    string? Note,
    DateTimeOffset? HandledAt);

public sealed record PublishEventRequest(string Source, string? Type, string? CorrelationKey, string? ExternalId, JsonNode? Payload);

public sealed record PublishEventResponse(long EventId, bool Duplicate);

public sealed record ResolveEventRequest(long? SubscriptionId);

// ───────────── Ресурсы ─────────────

public sealed record ResourceWaiterDto(ProcessRefDto Process, DateTimeOffset RequestedAt);

public sealed record ResourceDto(
    string Key,
    string Title,
    ProcessRefDto? Holder,
    DateTimeOffset? AcquiredAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ResourceWaiterDto> Queue);

public sealed record ReleaseResourceRequest(string Key);

// ───────────── Триггеры ─────────────

public sealed record TriggerDto(
    string Id,
    string ScenarioId,
    string ScenarioName,
    TriggerKind Kind,
    bool Enabled,
    string Name,
    JsonObject Config,
    JsonObject? Inputs,
    string Overlap,
    string Description,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastRunAt,
    string? LastProcessId,
    int MissedRuns,
    DateTimeOffset? LastMissedAt,
    int SkippedRuns,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SaveTriggerRequest(
    string ScenarioId,
    TriggerKind Kind,
    bool Enabled,
    string? Name,
    JsonObject Config,
    JsonObject? Inputs,
    string? Overlap);

public sealed record OverviewCountsDto(int Running, int Waiting, int Paused, int NeedsAttention, int CompletedToday, int FailedToday);

public sealed record OverviewDto(
    OverviewCountsDto Counts,
    IReadOnlyList<ProcessSummaryDto> Attention,
    IReadOnlyList<ProcessSummaryDto> Active,
    IReadOnlyList<ProcessSummaryDto> Recent,
    int EventConflicts,
    IReadOnlyList<TriggerDto> MissedTriggers,
    IReadOnlyList<TriggerDto> UpcomingTriggers);

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
    bool RestartRequired,
    string? AdsPowerUrl = null,
    /// <summary>При сохранении: null — не менять, пустая строка — удалить. В ответах не возвращается.</summary>
    string? AdsPowerApiKey = null,
    bool AdsPowerHasKey = false);

public sealed record CheckResultDto(bool Ok, string Message);

/// <param name="ApiId">Сохранённый api_id (api_hash наружу не отдаётся).</param>
/// <param name="Phone">Сохранённый номер — для повторного входа.</param>
public sealed record TelegramStatusDto(string State, string? Account, string? Message, int? ApiId, string? Phone);

public sealed record TelegramLoginRequest(int ApiId, string? ApiHash, string? Phone);

public sealed record TelegramCodeRequest(string? Value);

public sealed record DesktopWindowDto(string Title, string Process);

public sealed record AdsPowerProfileDto(string Id, string? Number, string Name, string? Group);
