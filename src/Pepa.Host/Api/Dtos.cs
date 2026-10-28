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

/// <param name="StepMode">Отладка: пауза перед каждым блоком.</param>
public sealed record RunRequest(JsonObject? Inputs, bool? StepMode = null);

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
    IReadOnlyList<string> HeldResources,
    IReadOnlyList<string> ActiveNodeIds);

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

// ───────────── Вопросы процессов ─────────────

/// <summary>Вопрос, который ждёт ответа («Спросить пользователя»). Варианты отвечаются номером (их текст может быть скрыт).</summary>
public sealed record QuestionDto(
    long Id,
    ProcessRefDto Process,
    string Text,
    IReadOnlyList<string> Options,
    bool FreeText,
    bool Secret,
    bool HasImage,
    DateTimeOffset AskedAt,
    DateTimeOffset? ExpiresAt,
    string? Note);

public sealed record AnswerRequest(string? Answer, int? Option);

public sealed record AnswerResponse(bool Duplicate);

public sealed record ResolveEventRequest(long? SubscriptionId);

// ───────────── Ресурсы ─────────────

public sealed record ResourceWaiterDto(ProcessRefDto Process, DateTimeOffset RequestedAt);

/// <param name="HeldBy">Ресурс заняла сама Pepa, а не процесс (например, «запись действий в окнах»).</param>
public sealed record ResourceDto(
    string Key,
    string Title,
    ProcessRefDto? Holder,
    DateTimeOffset? AcquiredAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ResourceWaiterDto> Queue,
    string? HeldBy = null);

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
    bool AdsPowerHasKey = false,
    /// <summary>При сохранении: null — не менять.</summary>
    bool? NotifyTelegram = null,
    /// <summary>При сохранении: null — не менять.</summary>
    int? HistoryDays = null);

public sealed record CheckResultDto(bool Ok, string Message);

/// <param name="ApiId">Сохранённый api_id (api_hash наружу не отдаётся).</param>
/// <param name="Phone">Сохранённый номер — для повторного входа.</param>
/// <summary>Почта: состояние и ящик (пароль наружу не отдаётся).</summary>
/// <param name="SendProblem">Почему «Отправить письмо» сейчас не сработает; null — отправка настроена.</param>
public sealed record EmailStatusDto(string State, EmailAccountDto? Account, DateTimeOffset? LastCheckAt, string? Error, int? Messages, string? Note,
    string? SendProblem);

public sealed record EmailAccountDto(string Server, int Port, string Security, string Login, string Folder, bool Enabled,
    string? SmtpServer, int SmtpPort, string SmtpSecurity, string? SentFolder);

/// <summary>Ящик из формы. Пароль пусто — прежний (если сервер и логин те же). Сервер отправки пусто — письма не отправляются.</summary>
public sealed record EmailAccountRequest(string? Server, int? Port, string? Security, string? Login, string? Password, string? Folder, bool? Enabled,
    string? SmtpServer = null, int? SmtpPort = null, string? SmtpSecurity = null, string? SentFolder = null);

public sealed record TelegramStatusDto(string State, string? Account, string? Message, int? ApiId, string? Phone, TelegramProxyDto Proxy);

/// <param name="Kind">none, socks5 или mtproxy.</param>
/// <param name="Problem">Настройки прокси не прочитались: подключения нет, пока прокси не задан заново.</param>
public sealed record TelegramProxyDto(string Kind, string Host, int Port, string? Username, bool HasPassword, bool HasSecret, string? Problem);

/// <summary>Прокси: ссылкой (tg://proxy, tg://socks, t.me/…, socks5://) или полями.</summary>
/// <param name="Password">null — оставить сохранённый (при том же прокси), пустая строка — без пароля.</param>
/// <param name="Secret">null — оставить сохранённый (при том же прокси).</param>
public sealed record TelegramProxyRequest(string? Kind, string? Host, int? Port, string? Username, string? Password, string? Secret, string? Link);

public sealed record TelegramLoginRequest(int ApiId, string? ApiHash, string? Phone);

public sealed record TelegramCodeRequest(string? Value);

public sealed record DesktopWindowDto(string Title, string Process);

/// <param name="Browser">«pepa» — браузер Pepa с сохранённым входом, «adspower» — профиль AdsPower.</param>
public sealed record RecorderStartRequest(string? Browser, string? ProfileId, string? Url);

public sealed record RecorderStateDto(bool Recording, string? Browser, string? ProfileId, string? StartUrl,
    IReadOnlyList<Adapters.Browser.RecordedAction> Actions);

/// <param name="LastError">Почему не удалась последняя автоматическая копия (null — всё в порядке).</param>
public sealed record BackupsDto(string Directory, string? LastError, IReadOnlyList<Backups.BackupInfo> Items);

/// <param name="UnavailableReason">Почему запись в окнах Windows недоступна (null — доступна).</param>
public sealed record DesktopRecorderDto(string? UnavailableReason, bool Recording, IReadOnlyList<Adapters.Desktop.RecordedDesktopStep> Steps);

/// <summary>Шаблон сценария (из набора примеров): из него можно создать свой сценарий.</summary>
public sealed record TemplateDto(string Name, string Description, IReadOnlyList<string> Blocks, ScenarioDefinition Definition);

public sealed record AdsPowerProfileDto(string Id, string? Number, string Name, string? Group);

/// <summary>ИИ-помощник: задан ли ключ (сам ключ наружу не отдаётся — только последние символы).</summary>
public sealed record AssistantStatusDto(bool Configured, string? KeyHint, string Model);

public sealed record AssistantKeyRequest(string? ApiKey);

/// <param name="Current">Сценарий, который нужно изменить по описанию; null — составить новые блоки.</param>
public sealed record AssistantDraftRequest(string? Description, IReadOnlyList<VariableDefinition>? Variables, ScenarioDefinition? Current = null);

public sealed record AssistantExplainRequest(ScenarioDefinition? Scenario);

/// <summary>Объяснение сценария: в целом, по блокам и что проверить.</summary>
public sealed record AssistantExplanationDto(
    string Summary,
    IReadOnlyList<Assistant.ExplainedBlock> Steps,
    IReadOnlyList<string> Cautions,
    Assistant.AssistantUsage Usage);

/// <summary>Черновик блоков для вставки в схему (в том же виде, что скопированные блоки).</summary>
public sealed record AssistantDraftDto(
    string Summary,
    IReadOnlyList<string> Questions,
    IReadOnlyList<VariableDefinition> Variables,
    IReadOnlyList<NodeDefinition> Nodes,
    IReadOnlyList<EdgeDefinition> Edges,
    string? EntryNodeId,
    IReadOnlyList<string> Problems,
    int Requests,
    Assistant.AssistantUsage Usage);

/// <summary>Расширения: куда их класть и что загружено.</summary>
public sealed record PluginsDto(string Directory, IReadOnlyList<PluginDto> Plugins);

/// <param name="Error">Не загружено: причина (блоков этого расширения нет).</param>
public sealed record PluginDto(string Id, string Name, string? Version, IReadOnlyList<string> NodeTypes, string? Error);

/// <summary>Элементы окна для выбора в конструкторе.</summary>
/// <param name="Truncated">Элементов больше, чем показано (сложное окно) — уточните поиск в самой программе.</param>
public sealed record DesktopElementsDto(string Window, string Process, bool Truncated, IReadOnlyList<DesktopElementDto> Elements);

/// <param name="UniqueName">Подпись в окне встречается один раз — по ней элемент найдётся однозначно.</param>
/// <param name="UniqueId">Код (AutomationId) встречается один раз.</param>
public sealed record DesktopElementDto(int Depth, string Name, string ControlType, string AutomationId, bool Enabled, bool UniqueName, bool UniqueId);

public sealed record WatchesRequest(IReadOnlyList<Adapters.FileSystem.FolderWatchSettings>? Watches);

/// <param name="Status">null — папку ещё не проверяли.</param>
public sealed record WatchDto(string Id, string Folder, string Pattern, bool Subfolders, bool Changes, bool Enabled,
    Adapters.FileSystem.FolderWatchStatus? Status);

public sealed record WatchesDto(IReadOnlyList<WatchDto> Watches, string Source);
