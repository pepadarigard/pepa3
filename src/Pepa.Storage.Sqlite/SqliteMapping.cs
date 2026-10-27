using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pepa.Core;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;

namespace Pepa.Storage.Sqlite;

/// <summary>Преобразование строк таблиц в записи и обратно. Даты — UTC в фиксированном формате (сортируются как строки).</summary>
internal static class SqliteMapping
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static string Date(DateTimeOffset value) => value.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static object DateOrNull(DateTimeOffset? value) => value is { } v ? Date(v) : DBNull.Value;

    public static object JsonOrNull(JsonNode? value) => value is null ? DBNull.Value : value.ToJsonString();

    public static object OrNull(string? value) => value is null ? DBNull.Value : value;

    public static object OrNull(long? value) => value is { } v ? v : DBNull.Value;

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.ParseExact(value, DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static string? Str(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
    }

    public static long? Long(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetInt64(ordinal);
    }

    private static DateTimeOffset? DateOf(SqliteDataReader r, string column) => Str(r, column) is { } s ? ParseDate(s) : null;

    private static JsonObject? ObjectOf(SqliteDataReader r, string column) =>
        Str(r, column) is { } s ? JsonNode.Parse(s) as JsonObject : null;

    private static JsonArray? ArrayOf(SqliteDataReader r, string column) =>
        Str(r, column) is { } s ? JsonNode.Parse(s) as JsonArray : null;

    private static JsonNode? NodeOf(SqliteDataReader r, string column) => Str(r, column) is { } s ? JsonNode.Parse(s) : null;

    private static TEnum EnumOf<TEnum>(SqliteDataReader r, string column) where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(Str(r, column)!);

    // ───────────── Чтение ─────────────

    public static StoredScenario ReadScenario(SqliteDataReader r) => new()
    {
        Id = Str(r, "id")!,
        Name = Str(r, "name")!,
        Description = Str(r, "description")!,
        DefinitionJson = Str(r, "definition")!,
        Version = (int)Long(r, "version")!,
        CreatedAt = DateOf(r, "created_at")!.Value,
        UpdatedAt = DateOf(r, "updated_at")!.Value,
    };

    public static ProcessRecord ReadProcess(SqliteDataReader r) => new()
    {
        Id = Str(r, "id")!,
        ScenarioId = Str(r, "scenario_id")!,
        ScenarioName = Str(r, "scenario_name")!,
        ScenarioVersion = (int)Long(r, "scenario_version")!,
        DefinitionJson = Str(r, "definition")!,
        Status = EnumOf<ProcessStatus>(r, "status"),
        Attention = EnumOf<AttentionKind>(r, "attention"),
        PauseReason = EnumOf<PauseReason>(r, "pause_reason"),
        StatusMessage = Str(r, "status_message"),
        AttentionStepId = Long(r, "attention_step_id"),
        AttentionTokenId = Str(r, "attention_token_id"),
        Variables = ObjectOf(r, "variables") ?? new JsonObject(),
        LastError = ObjectOf(r, "last_error"),
        Memory = ObjectOf(r, "memory"),
        Debug = Str(r, "debug") is { } debug ? System.Text.Json.JsonSerializer.Deserialize<DebugState>(debug) : null,
        ParentProcessId = Str(r, "parent_process_id"),
        OriginKey = Str(r, "origin_key"),
        Depth = (int)(Long(r, "depth") ?? 0),
        StepCount = (int)Long(r, "step_count")!,
        StepBudget = (int)Long(r, "step_budget")!,
        CreatedAt = DateOf(r, "created_at")!.Value,
        UpdatedAt = DateOf(r, "updated_at")!.Value,
        FinishedAt = DateOf(r, "finished_at"),
        Secrets = SecretsOf(r),
    };

    public static TokenRecord ReadToken(SqliteDataReader r) => new()
    {
        Id = Str(r, "id")!,
        ProcessId = Str(r, "process_id")!,
        NodeId = Str(r, "node_id")!,
        State = EnumOf<TokenState>(r, "state"),
        WaitKind = EnumOf<WaitKind>(r, "wait_kind"),
        WakeAt = DateOf(r, "wake_at"),
        Attempt = (int)Long(r, "attempt")!,
        WaitingStepId = Long(r, "waiting_step_id"),
        WaitRef = Str(r, "wait_ref"),
        FromNodeId = Str(r, "from_node_id"),
        Fork = Str(r, "fork"),
        CreatedAt = DateOf(r, "created_at")!.Value,
    };

    public static StepRecord ReadStep(SqliteDataReader r) => new()
    {
        Id = Long(r, "id")!.Value,
        ProcessId = Str(r, "process_id")!,
        TokenId = Str(r, "token_id")!,
        NodeId = Str(r, "node_id")!,
        NodeType = Str(r, "node_type")!,
        NodeLabel = Str(r, "node_label")!,
        Attempt = (int)Long(r, "attempt")!,
        Effect = EnumOf<NodeEffect>(r, "effect"),
        State = EnumOf<StepState>(r, "state"),
        Input = ObjectOf(r, "input"),
        Checkpoint = ObjectOf(r, "checkpoint"),
        Output = ObjectOf(r, "output"),
        Port = Str(r, "port"),
        Error = Str(r, "error"),
        Resolution = Str(r, "resolution"),
        PlannedAt = DateOf(r, "planned_at")!.Value,
        StartedAt = DateOf(r, "started_at"),
        FinishedAt = DateOf(r, "finished_at"),
    };

    public static JournalEntry ReadJournal(SqliteDataReader r) => new()
    {
        Id = Long(r, "id")!.Value,
        ProcessId = Str(r, "process_id")!,
        StepId = Long(r, "step_id"),
        NodeId = Str(r, "node_id"),
        At = DateOf(r, "at")!.Value,
        Level = EnumOf<JournalLevel>(r, "level"),
        Message = Str(r, "message")!,
    };

    public static EventRecord ReadEvent(SqliteDataReader r) => new()
    {
        Id = Long(r, "id")!.Value,
        Source = Str(r, "source")!,
        Type = Str(r, "type")!,
        ExternalId = Str(r, "external_id"),
        CorrelationKey = Str(r, "correlation_key"),
        Payload = NodeOf(r, "payload"),
        ReceivedAt = DateOf(r, "received_at")!.Value,
        OccurredAt = DateOf(r, "occurred_at"),
        State = EnumOf<EventState>(r, "state"),
        ProcessId = Str(r, "process_id"),
        SubscriptionId = Long(r, "subscription_id"),
        Candidates = ArrayOf(r, "candidates"),
        StartedProcesses = ArrayOf(r, "started_processes"),
        Note = Str(r, "note"),
        HandledAt = DateOf(r, "handled_at"),
        Secrets = SecretsOf(r),
    };

    private static IReadOnlyList<string>? SecretsOf(SqliteDataReader r) =>
        Str(r, "secrets") is { } json ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) : null;

    public static SubscriptionRecord ReadSubscription(SqliteDataReader r) => new()
    {
        Id = Long(r, "id")!.Value,
        ProcessId = Str(r, "process_id")!,
        TokenId = Str(r, "token_id")!,
        StepId = Long(r, "step_id")!.Value,
        NodeId = Str(r, "node_id")!,
        Source = Str(r, "source")!,
        EventType = Str(r, "event_type"),
        CorrelationKey = Str(r, "correlation_key"),
        MatchExpression = Str(r, "match_expression"),
        Priority = (int)Long(r, "priority")!,
        LookbackSeconds = (int)Long(r, "lookback_seconds")!,
        SaveTo = Str(r, "save_to"),
        Purpose = EnumOf<SubscriptionPurpose>(r, "purpose"),
        State = EnumOf<SubscriptionState>(r, "state"),
        EventId = Long(r, "event_id"),
        CreatedAt = DateOf(r, "created_at")!.Value,
        ExpiresAt = DateOf(r, "expires_at"),
    };

    public static ResourceRecord ReadResource(SqliteDataReader r) => new()
    {
        Key = Str(r, "key")!,
        Title = Str(r, "title")!,
        HolderProcessId = Str(r, "holder_process_id"),
        AcquiredAt = DateOf(r, "acquired_at"),
        UpdatedAt = DateOf(r, "updated_at")!.Value,
    };

    public static ResourceWaiterRecord ReadWaiter(SqliteDataReader r) => new()
    {
        Id = Long(r, "id")!.Value,
        ResourceKey = Str(r, "resource_key")!,
        ProcessId = Str(r, "process_id")!,
        TokenId = Str(r, "token_id")!,
        Priority = (int)Long(r, "priority")!,
        RequestedAt = DateOf(r, "requested_at")!.Value,
        ProcessStatus = Str(r, "process_status") is { } status ? Enum.Parse<ProcessStatus>(status) : null,
    };

    public static TriggerRecord ReadTrigger(SqliteDataReader r) => new()
    {
        Id = Str(r, "id")!,
        ScenarioId = Str(r, "scenario_id")!,
        Kind = EnumOf<TriggerKind>(r, "kind"),
        Enabled = Long(r, "enabled") == 1,
        Name = Str(r, "name") ?? "",
        Config = ObjectOf(r, "config") ?? new JsonObject(),
        Inputs = ObjectOf(r, "inputs"),
        Overlap = Str(r, "overlap") ?? "skip",
        NextRunAt = DateOf(r, "next_run_at"),
        LastRunAt = DateOf(r, "last_run_at"),
        LastProcessId = Str(r, "last_process_id"),
        MissedRuns = (int)(Long(r, "missed_runs") ?? 0),
        LastMissedAt = DateOf(r, "last_missed_at"),
        SkippedRuns = (int)(Long(r, "skipped_runs") ?? 0),
        LastError = Str(r, "last_error"),
        CreatedAt = DateOf(r, "created_at")!.Value,
        UpdatedAt = DateOf(r, "updated_at")!.Value,
    };

    // ───────────── Запросы ─────────────

    public static async Task<IReadOnlyList<T>> QueryAsync<T>(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, Action<SqliteCommand>? bind, Func<SqliteDataReader, T> map, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        bind?.Invoke(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(map(reader));
        }

        return result;
    }

    public const string WaiterSelect =
        "SELECT w.*, p.status AS process_status FROM resource_waiters w LEFT JOIN processes p ON p.id = w.process_id";
}
