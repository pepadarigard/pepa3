using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Pepa.Core.Nodes;
using Pepa.Engine.Persistence;

namespace Pepa.Storage.Sqlite;

/// <summary>
/// Хранилище в одном файле SQLite. Журнал WAL, synchronous=FULL: подтверждённая транзакция переживает
/// сбой питания. Все записи выполняются строго по одной.
/// </summary>
public sealed partial class SqliteStore : IPepaStore
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqliteStore(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL;", cancellationToken);
            var version = Convert.ToInt32(await ScalarAsync(connection, null, "PRAGMA user_version;", cancellationToken),
                CultureInfo.InvariantCulture);
            if (version > Migrations.Length)
            {
                throw new InvalidOperationException(
                    $"База данных создана более новой версией Pepa (схема {version}). Обновите приложение.");
            }

            for (var i = version; i < Migrations.Length; i++)
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, transaction, Migrations[i], cancellationToken);
                await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {i + 1};", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ───────────────────────────── Чтение ─────────────────────────────

    public Task<IReadOnlyList<StoredScenario>> ListScenariosAsync(CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM scenarios ORDER BY updated_at DESC;", null, ReadScenario, cancellationToken);

    public async Task<StoredScenario?> GetScenarioAsync(string id, CancellationToken cancellationToken = default) =>
        (await QueryAsync("SELECT * FROM scenarios WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id),
            ReadScenario, cancellationToken)).FirstOrDefault();

    public async Task<ProcessRecord?> GetProcessAsync(string id, CancellationToken cancellationToken = default) =>
        (await QueryAsync("SELECT * FROM processes WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id),
            ReadProcess, cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<ProcessRecord>> ListProcessesAsync(ProcessQuery query,
        CancellationToken cancellationToken = default)
    {
        var sql = new StringBuilder("SELECT * FROM processes WHERE 1 = 1");
        if (query.Statuses is { Count: > 0 } statuses)
        {
            sql.Append(" AND status IN (")
                .Append(string.Join(", ", statuses.Select((_, i) => $"$s{i}")))
                .Append(')');
        }

        if (query.ScenarioId is not null)
        {
            sql.Append(" AND scenario_id = $scenario");
        }

        if (query.CreatedAfter is not null)
        {
            sql.Append(" AND created_at >= $after");
        }

        sql.Append(" ORDER BY created_at DESC LIMIT $limit;");
        return QueryAsync(sql.ToString(), command =>
        {
            var i = 0;
            foreach (var status in query.Statuses ?? [])
            {
                command.Parameters.AddWithValue($"$s{i++}", status.ToString());
            }

            if (query.ScenarioId is not null)
            {
                command.Parameters.AddWithValue("$scenario", query.ScenarioId);
            }

            if (query.CreatedAfter is { } after)
            {
                command.Parameters.AddWithValue("$after", Date(after));
            }

            command.Parameters.AddWithValue("$limit", query.Limit);
        }, ReadProcess, cancellationToken);
    }

    public Task<IReadOnlyList<TokenRecord>> GetTokensAsync(string processId, CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM tokens WHERE process_id = $p ORDER BY created_at;",
            c => c.Parameters.AddWithValue("$p", processId), ReadToken, cancellationToken);

    public async Task<IReadOnlyList<TokenRecord>> GetTokensAsync(IReadOnlyCollection<string> processIds,
        CancellationToken cancellationToken = default)
    {
        if (processIds.Count == 0)
        {
            return [];
        }

        var result = new List<TokenRecord>();
        // Порциями, чтобы не упереться в предел числа параметров SQLite.
        foreach (var chunk in processIds.Chunk(500))
        {
            var names = chunk.Select((_, i) => $"$p{i}").ToList();
            result.AddRange(await QueryAsync(
                $"SELECT * FROM tokens WHERE process_id IN ({string.Join(", ", names)}) ORDER BY created_at;",
                command =>
                {
                    for (var i = 0; i < chunk.Length; i++)
                    {
                        command.Parameters.AddWithValue(names[i], chunk[i]);
                    }
                }, ReadToken, cancellationToken));
        }

        return result;
    }

    public Task<IReadOnlyList<StepRecord>> GetStepsAsync(string processId, CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM steps WHERE process_id = $p ORDER BY id;",
            c => c.Parameters.AddWithValue("$p", processId), ReadStep, cancellationToken);

    public async Task<StepRecord?> GetStepAsync(long stepId, CancellationToken cancellationToken = default) =>
        (await QueryAsync("SELECT * FROM steps WHERE id = $id;", c => c.Parameters.AddWithValue("$id", stepId),
            ReadStep, cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<StepRecord>> GetOpenStepsAsync(string processId, CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM steps WHERE process_id = $p AND state IN ('Planned', 'Running', 'Waiting') ORDER BY id;",
            c => c.Parameters.AddWithValue("$p", processId), ReadStep, cancellationToken);

    public Task<IReadOnlyList<JournalEntry>> GetJournalAsync(string processId, CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM journal WHERE process_id = $p ORDER BY id;",
            c => c.Parameters.AddWithValue("$p", processId), ReadJournal, cancellationToken);

    public Task<IReadOnlyList<string>> GetRunnableProcessIdsAsync(DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        QueryAsync("""
                   SELECT DISTINCT p.id FROM processes p JOIN tokens t ON t.process_id = p.id
                   WHERE (p.status = 'Waiting' AND t.state = 'Waiting' AND t.wake_at <= $now)
                      OR (p.status = 'Running' AND (t.state = 'Ready' OR (t.state = 'Waiting' AND t.wake_at <= $now)));
                   """,
            c => c.Parameters.AddWithValue("$now", Date(now)), r => r.GetString(0), cancellationToken);

    public async Task<IReadOnlyDictionary<string, ProcessRecord>> GetLatestProcessPerScenarioAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync("""
                                    SELECT p.* FROM processes p
                                    JOIN (SELECT scenario_id, MAX(created_at) AS latest FROM processes GROUP BY scenario_id) x
                                      ON x.scenario_id = p.scenario_id AND x.latest = p.created_at;
                                    """, null, ReadProcess, cancellationToken);
        var result = new Dictionary<string, ProcessRecord>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result.TryAdd(row.ScenarioId, row);
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync("SELECT key, value FROM settings;", null,
            r => (Key: r.GetString(0), Value: r.GetString(1)), cancellationToken);
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }

    // ───────────────────────────── Запись ─────────────────────────────

    public async Task<IStoreWrite> BeginWriteAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        SqliteConnection? connection = null;
        try
        {
            connection = await OpenAsync(cancellationToken);
            var transaction = connection.BeginTransaction(deferred: false);
            return new SqliteWrite(connection, transaction, _writeLock);
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            _writeLock.Release();
            throw;
        }
    }

    // ───────────────────────────── Внутреннее ─────────────────────────────

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null,
            "PRAGMA busy_timeout = 10000; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;", cancellationToken);
        return connection;
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Action<SqliteCommand>? bind,
        Func<SqliteDataReader, T> map, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
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

    internal static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    internal static string Date(DateTimeOffset value) =>
        value.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);

    internal static object DateOrNull(DateTimeOffset? value) => value is { } v ? Date(v) : DBNull.Value;

    internal static object JsonOrNull(JsonNode? value) => value is null ? DBNull.Value : value.ToJsonString();

    internal static object OrNull(string? value) => value is null ? DBNull.Value : value;

    internal static object OrNull(long? value) => value is { } v ? v : DBNull.Value;

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.ParseExact(value, DateFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string? Str(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetString(ordinal);
    }

    private static long? Long(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetInt64(ordinal);
    }

    private static DateTimeOffset? DateOf(SqliteDataReader r, string column) =>
        Str(r, column) is { } s ? ParseDate(s) : null;

    private static JsonObject? JsonOf(SqliteDataReader r, string column) =>
        Str(r, column) is { } s ? JsonNode.Parse(s) as JsonObject : null;

    private static TEnum EnumOf<TEnum>(SqliteDataReader r, string column) where TEnum : struct, Enum =>
        Enum.Parse<TEnum>(Str(r, column)!);

    private static StoredScenario ReadScenario(SqliteDataReader r) => new()
    {
        Id = Str(r, "id")!,
        Name = Str(r, "name")!,
        Description = Str(r, "description")!,
        DefinitionJson = Str(r, "definition")!,
        Version = (int)Long(r, "version")!,
        CreatedAt = DateOf(r, "created_at")!.Value,
        UpdatedAt = DateOf(r, "updated_at")!.Value,
    };

    private static ProcessRecord ReadProcess(SqliteDataReader r) => new()
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
        Variables = JsonOf(r, "variables") ?? new JsonObject(),
        LastError = JsonOf(r, "last_error"),
        StepCount = (int)Long(r, "step_count")!,
        StepBudget = (int)Long(r, "step_budget")!,
        CreatedAt = DateOf(r, "created_at")!.Value,
        UpdatedAt = DateOf(r, "updated_at")!.Value,
        FinishedAt = DateOf(r, "finished_at"),
    };

    private static TokenRecord ReadToken(SqliteDataReader r) => new()
    {
        Id = Str(r, "id")!,
        ProcessId = Str(r, "process_id")!,
        NodeId = Str(r, "node_id")!,
        State = EnumOf<TokenState>(r, "state"),
        WaitKind = EnumOf<WaitKind>(r, "wait_kind"),
        WakeAt = DateOf(r, "wake_at"),
        Attempt = (int)Long(r, "attempt")!,
        WaitingStepId = Long(r, "waiting_step_id"),
        CreatedAt = DateOf(r, "created_at")!.Value,
    };

    private static StepRecord ReadStep(SqliteDataReader r) => new()
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
        Input = JsonOf(r, "input"),
        Checkpoint = JsonOf(r, "checkpoint"),
        Output = JsonOf(r, "output"),
        Port = Str(r, "port"),
        Error = Str(r, "error"),
        Resolution = Str(r, "resolution"),
        PlannedAt = DateOf(r, "planned_at")!.Value,
        StartedAt = DateOf(r, "started_at"),
        FinishedAt = DateOf(r, "finished_at"),
    };

    private static JournalEntry ReadJournal(SqliteDataReader r) => new()
    {
        Id = Long(r, "id")!.Value,
        ProcessId = Str(r, "process_id")!,
        StepId = Long(r, "step_id"),
        NodeId = Str(r, "node_id"),
        At = DateOf(r, "at")!.Value,
        Level = EnumOf<JournalLevel>(r, "level"),
        Message = Str(r, "message")!,
    };
}
