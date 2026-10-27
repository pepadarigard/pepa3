using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Pepa.Engine.Persistence;
using static Pepa.Storage.Sqlite.SqliteMapping;

namespace Pepa.Storage.Sqlite;

/// <summary>
/// Хранилище в одном файле SQLite. Журнал WAL, synchronous=FULL: подтверждённая транзакция переживает
/// сбой питания. Все записи выполняются строго по одной.
/// </summary>
public sealed partial class SqliteStore : IPepaStore
{
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

    /// <summary>
    /// База — восстановленная резервная копия (её положили на место базы): время копии. Узнаётся при
    /// <see cref="InitializeAsync(CancellationToken)"/>; после того как процессы приведены в порядок, отметку снимает
    /// <see cref="ClearRestoreMarkerAsync"/>.
    /// </summary>
    public DateTimeOffset? RestoredFromBackupAt { get; private set; }

    public async Task ClearRestoreMarkerAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await ExecuteAsync(connection, null, "DROP TABLE IF EXISTS pepa_backup;", cancellationToken);
            RestoredFromBackupAt = null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Удалить из истории завершённые до <paramref name="finishedBefore"/> процессы (с их шагами, журналом и ветвями).
    /// Не трогаются процессы, у которых есть незавершённый «родитель» или «ребёнок» (вызванный сценарий) или занятый ресурс.
    /// События остаются: по ним Pepa узнаёт повторно пришедшее событие. Удаляется пачками, чтобы не держать запись долго.
    /// </summary>
    /// <returns>Сколько процессов удалено.</returns>
    public async Task<int> DeleteFinishedProcessesAsync(DateTimeOffset finishedBefore, int batch = 500, CancellationToken cancellationToken = default)
    {
        const string finished = "('Completed', 'Failed', 'Cancelled')";
        var total = 0;
        while (true)
        {
            int deleted;
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await using var connection = await OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = $"""
                    DELETE FROM processes WHERE id IN (
                        SELECT p.id FROM processes p
                        WHERE p.status IN {finished} AND p.finished_at IS NOT NULL AND p.finished_at < $before
                          AND NOT EXISTS (SELECT 1 FROM processes c WHERE c.parent_process_id = p.id AND c.status NOT IN {finished})
                          AND NOT EXISTS (SELECT 1 FROM processes q WHERE q.id = p.parent_process_id AND q.status NOT IN {finished})
                          AND NOT EXISTS (SELECT 1 FROM resources r WHERE r.holder_process_id = p.id)
                        LIMIT $batch);
                    """;
                command.Parameters.AddWithValue("$before", Date(finishedBefore));
                command.Parameters.AddWithValue("$batch", batch);
                deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            total += deleted;
            if (deleted < batch)
            {
                return total;
            }
        }
    }

    /// <summary>Папка резервных копий базы — рядом с базой.</summary>
    public string BackupDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!, "backups");

    /// <summary>Имя файла копии: вид (daily, manual, migration) и время.</summary>
    public static string BackupFileName(string kind, DateTime localTime) =>
        $"pepa-{kind}-{localTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db";

    /// <summary>
    /// Согласованная копия базы одним файлом (онлайн-копирование SQLite: запись в базу во время копирования не мешает).
    /// Копия проверяется (quick_check) и появляется под своим именем только целиком.
    /// </summary>
    public async Task BackupAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        if (File.Exists(targetPath))
        {
            throw new IOException($"Копия «{Path.GetFileName(targetPath)}» уже есть.");
        }

        var temp = targetPath + ".tmp";
        try
        {
            File.Delete(temp);
            await using (var source = await OpenAsync(cancellationToken))
            await using (var target = new SqliteConnection(new SqliteConnectionStringBuilder
                         {
                             DataSource = temp, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
                         }.ToString()))
            {
                await target.OpenAsync(cancellationToken);
                source.BackupDatabase(target);
                // Отметка «это копия»: если базу заменят ею, Pepa узнает об этом при запуске (см. RestoredFromBackupAt).
                // База сама может быть восстановленной копией (с отметкой) — отметка тогда просто обновляется.
                await ExecuteAsync(target, null,
                    "CREATE TABLE IF NOT EXISTS pepa_backup (created_at TEXT NOT NULL); DELETE FROM pepa_backup; " +
                    $"INSERT INTO pepa_backup (created_at) VALUES ('{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}');",
                    cancellationToken);
                // Копия — одним файлом, без журнала WAL рядом.
                await ExecuteAsync(target, null, "PRAGMA journal_mode = DELETE;", cancellationToken);
                var check = Convert.ToString(await ScalarAsync(target, null, "PRAGMA quick_check;", cancellationToken), CultureInfo.InvariantCulture);
                if (check != "ok")
                {
                    throw new InvalidDataException($"Копия базы не прошла проверку: {check}");
                }
            }

            File.Move(temp, targetPath);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => InitializeAsync(Migrations.Length, cancellationToken);

    /// <param name="targetVersion">До какой версии схемы обновить (меньше последней — только для проверок обновления).</param>
    internal async Task InitializeAsync(int targetVersion, CancellationToken cancellationToken)
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

            if (version > 0 && version < targetVersion)
            {
                // Перед обновлением схемы — копия: если обновление пойдёт не так, данные не потеряются.
                await BackupAsync(Path.Combine(BackupDirectory, BackupFileName($"migration{version}", DateTime.Now)), cancellationToken);
            }

            RestoredFromBackupAt = null;
            if (Convert.ToInt64(await ScalarAsync(connection, null, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'pepa_backup';",
                    cancellationToken), CultureInfo.InvariantCulture) > 0)
            {
                var at = Convert.ToString(await ScalarAsync(connection, null, "SELECT created_at FROM pepa_backup LIMIT 1;", cancellationToken),
                    CultureInfo.InvariantCulture);
                // Время копии не прочиталось — всё равно восстановленная база: отметка важнее точного времени.
                RestoredFromBackupAt = DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
            }

            for (var i = version; i < targetVersion; i++)
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

    // ───────────────────────────── Сценарии ─────────────────────────────

    public Task<IReadOnlyList<StoredScenario>> ListScenariosAsync(CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM scenarios ORDER BY updated_at DESC;", null, ReadScenario, cancellationToken);

    public async Task<StoredScenario?> GetScenarioAsync(string id, CancellationToken cancellationToken = default) =>
        (await QueryAsync("SELECT * FROM scenarios WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id),
            ReadScenario, cancellationToken)).FirstOrDefault();

    // ───────────────────────────── Процессы ─────────────────────────────

    public async Task<ProcessRecord?> GetProcessAsync(string id, CancellationToken cancellationToken = default) =>
        (await QueryAsync("SELECT * FROM processes WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id),
            ReadProcess, cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<ProcessRecord>> ListProcessesAsync(ProcessQuery query,
        CancellationToken cancellationToken = default)
    {
        var sql = new StringBuilder("SELECT * FROM processes WHERE 1 = 1");
        if (query.Statuses is { Count: > 0 } statuses)
        {
            sql.Append(" AND status IN (").Append(string.Join(", ", statuses.Select((_, i) => $"$s{i}"))).Append(')');
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

    public Task<IReadOnlyList<ProcessRecord>> GetChildProcessesAsync(string parentProcessId,
        CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM processes WHERE parent_process_id = $p ORDER BY created_at;",
            c => c.Parameters.AddWithValue("$p", parentProcessId), ReadProcess, cancellationToken);

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

    public async Task<StepRecord?> GetLastCheckpointStepAsync(string processId, string tokenId, string nodeId,
        CancellationToken cancellationToken = default) =>
        // Посещение блока начинается шагом с попыткой 1: снимки прежних посещений (цикл) не подходят, а шаг без снимка
        // (подготовка не удалась) не обрывает цепочку — берётся последний снимок этого посещения.
        (await QueryAsync("""
                          SELECT * FROM steps
                          WHERE process_id = $p AND token_id = $t AND node_id = $n AND checkpoint IS NOT NULL
                            AND id >= (SELECT COALESCE(MAX(id), 0) FROM steps
                                       WHERE process_id = $p AND token_id = $t AND node_id = $n AND attempt = 1)
                          ORDER BY id DESC LIMIT 1;
                          """,
            c =>
            {
                c.Parameters.AddWithValue("$p", processId);
                c.Parameters.AddWithValue("$t", tokenId);
                c.Parameters.AddWithValue("$n", nodeId);
            }, ReadStep, cancellationToken)).FirstOrDefault();

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
                      OR (p.status = 'Running' AND (t.state = 'Ready' OR (t.state = 'Waiting' AND t.wake_at <= $now)))
                      OR (p.status IN ('Waiting', 'Running') AND t.state = 'Waiting' AND t.wait_kind = 'Event'
                          AND EXISTS (SELECT 1 FROM subscriptions s
                                      WHERE s.step_id = t.waiting_step_id AND s.state = 'Fulfilled'))
                      OR (p.status IN ('Waiting', 'Running') AND t.state = 'Waiting' AND t.wait_kind = 'Resource'
                          AND EXISTS (SELECT 1 FROM resources r
                                      WHERE r.key = t.wait_ref AND (r.holder_process_id = p.id OR r.holder_process_id IS NULL)));
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

    // ───────────────────────────── События ─────────────────────────────

    public async Task<EventRecord?> GetEventAsync(long id, CancellationToken cancellationToken = default) =>
        (await QueryAsync("SELECT * FROM events WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id),
            ReadEvent, cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<EventRecord>> ListEventsAsync(EventQuery query, CancellationToken cancellationToken = default)
    {
        var sql = new StringBuilder("SELECT * FROM events WHERE 1 = 1");
        if (query.States is { Count: > 0 } states)
        {
            sql.Append(" AND state IN (").Append(string.Join(", ", states.Select((_, i) => $"$s{i}"))).Append(')');
        }

        if (query.ProcessId is not null)
        {
            sql.Append(" AND process_id = $process");
        }

        // Для «необработанных» — в порядке поступления, иначе — сначала новые.
        sql.Append(query.States is { Count: 1 } single && single.Contains(EventState.Received)
            ? " ORDER BY id LIMIT $limit;"
            : " ORDER BY id DESC LIMIT $limit;");
        return QueryAsync(sql.ToString(), command =>
        {
            var i = 0;
            foreach (var state in query.States ?? [])
            {
                command.Parameters.AddWithValue($"$s{i++}", state.ToString());
            }

            if (query.ProcessId is not null)
            {
                command.Parameters.AddWithValue("$process", query.ProcessId);
            }

            command.Parameters.AddWithValue("$limit", query.Limit);
        }, ReadEvent, cancellationToken);
    }

    public async Task<SubscriptionRecord?> GetSubscriptionAsync(long id, CancellationToken cancellationToken = default) =>
        (await QueryAsync("SELECT * FROM subscriptions WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id),
            ReadSubscription, cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<SubscriptionRecord>> GetActiveSubscriptionsAsync(string source,
        CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM subscriptions WHERE state = 'Active' AND source = $s ORDER BY id;",
            c => c.Parameters.AddWithValue("$s", source), ReadSubscription, cancellationToken);

    public Task<IReadOnlyList<SubscriptionRecord>> GetSubscriptionsAsync(string processId,
        CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM subscriptions WHERE process_id = $p ORDER BY id;",
            c => c.Parameters.AddWithValue("$p", processId), ReadSubscription, cancellationToken);

    // ───────────────────────────── Ресурсы ─────────────────────────────

    public Task<IReadOnlyList<ResourceRecord>> ListResourcesAsync(CancellationToken cancellationToken = default) =>
        QueryAsync("SELECT * FROM resources ORDER BY key;", null, ReadResource, cancellationToken);

    public Task<IReadOnlyList<ResourceWaiterRecord>> ListResourceWaitersAsync(CancellationToken cancellationToken = default) =>
        QueryAsync($"{WaiterSelect} ORDER BY w.resource_key, w.priority DESC, w.id;", null, ReadWaiter, cancellationToken);

    // ───────────────────────────── Триггеры ─────────────────────────────

    public Task<IReadOnlyList<TriggerRecord>> ListTriggersAsync(string? scenarioId = null,
        CancellationToken cancellationToken = default) =>
        scenarioId is null
            ? QueryAsync("SELECT * FROM triggers ORDER BY created_at;", null, ReadTrigger, cancellationToken)
            : QueryAsync("SELECT * FROM triggers WHERE scenario_id = $s ORDER BY created_at;",
                c => c.Parameters.AddWithValue("$s", scenarioId), ReadTrigger, cancellationToken);

    public async Task<TriggerRecord?> GetTriggerAsync(string id, CancellationToken cancellationToken = default) =>
        (await QueryAsync("SELECT * FROM triggers WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id),
            ReadTrigger, cancellationToken)).FirstOrDefault();

    // ───────────────────────────── Настройки ─────────────────────────────

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
        return await SqliteMapping.QueryAsync(connection, null, sql, bind, map, cancellationToken);
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
}
