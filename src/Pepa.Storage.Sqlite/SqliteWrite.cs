using Microsoft.Data.Sqlite;
using Pepa.Engine.Persistence;
using static Pepa.Storage.Sqlite.SqliteMapping;

namespace Pepa.Storage.Sqlite;

/// <summary>Одна транзакция записи. Держит общий замок записи до освобождения.</summary>
internal sealed class SqliteWrite(SqliteConnection connection, SqliteTransaction transaction, SemaphoreSlim writeLock)
    : IStoreWrite
{
    private bool _committed;
    private bool _disposed;

    // ───────────────────────────── Сценарии ─────────────────────────────

    public async Task SaveScenarioAsync(StoredScenario scenario, int? expectedVersion)
    {
        if (expectedVersion is null)
        {
            await RunAsync("""
                           INSERT INTO scenarios (id, name, description, definition, version, created_at, updated_at)
                           VALUES ($id, $name, $description, $definition, $version, $created, $updated);
                           """, c => BindScenario(c, scenario));
            return;
        }

        var changed = await RunAsync("""
                                     UPDATE scenarios SET name = $name, description = $description, definition = $definition,
                                            version = $version, updated_at = $updated
                                     WHERE id = $id AND version = $expected;
                                     """, c =>
        {
            BindScenario(c, scenario);
            c.Parameters.AddWithValue("$expected", expectedVersion.Value);
        });
        if (changed == 0)
        {
            throw new ConcurrencyException("Сценарий уже изменён в другом месте.");
        }
    }

    public async Task<bool> DeleteScenarioAsync(string id)
    {
        await RunAsync("DELETE FROM triggers WHERE scenario_id = $id;", c => c.Parameters.AddWithValue("$id", id));
        return await RunAsync("DELETE FROM scenarios WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id)) > 0;
    }

    // ───────────────────────────── Процессы ─────────────────────────────

    public Task InsertProcessAsync(ProcessRecord process) =>
        RunAsync("""
                 INSERT INTO processes (id, scenario_id, scenario_name, scenario_version, definition, status, attention,
                     pause_reason, status_message, attention_step_id, attention_token_id, variables, last_error, memory, debug, parent_process_id,
                     origin_key, depth, step_count, step_budget, created_at, updated_at, finished_at, secrets)
                 VALUES ($id, $scenario_id, $scenario_name, $scenario_version, $definition, $status, $attention,
                     $pause_reason, $status_message, $attention_step_id, $attention_token_id, $variables, $last_error, $memory, $debug, $parent_process_id,
                     $origin_key, $depth, $step_count, $step_budget, $created_at, $updated_at, $finished_at, $secrets);
                 """, c => BindProcess(c, process));

    public async Task UpdateProcessAsync(ProcessRecord process)
    {
        var changed = await RunAsync("""
                                     UPDATE processes SET status = $status, attention = $attention, pause_reason = $pause_reason,
                                         status_message = $status_message, attention_step_id = $attention_step_id,
                                         attention_token_id = $attention_token_id,
                                         variables = $variables, last_error = $last_error, memory = $memory, debug = $debug,
                                         step_count = $step_count, step_budget = $step_budget, updated_at = $updated_at,
                                         finished_at = $finished_at, secrets = $secrets
                                     WHERE id = $id;
                                     """, c => BindProcess(c, process));
        if (changed == 0)
        {
            throw new InvalidOperationException($"Процесс {process.Id} не найден в хранилище.");
        }
    }

    public async Task<ProcessRecord?> GetProcessByOriginAsync(string originKey) =>
        (await Query("SELECT * FROM processes WHERE origin_key = $k;", c => c.Parameters.AddWithValue("$k", originKey),
            ReadProcess)).FirstOrDefault();

    public async Task<ProcessRecord?> GetProcessAsync(string id) =>
        (await Query("SELECT * FROM processes WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id), ReadProcess))
        .FirstOrDefault();

    public Task<IReadOnlyList<TokenRecord>> GetTokensAsync(string processId) =>
        Query("SELECT * FROM tokens WHERE process_id = $p ORDER BY created_at;", c => c.Parameters.AddWithValue("$p", processId), ReadToken);

    public Task CancelOpenStepsAsync(string processId, long exceptStepId, string resolution, DateTimeOffset at) =>
        RunAsync("""
                 UPDATE steps SET state = 'Cancelled', finished_at = $at, resolution = $resolution
                 WHERE process_id = $p AND state IN ('Planned', 'Waiting') AND id <> $except;
                 """, c =>
        {
            c.Parameters.AddWithValue("$p", processId);
            c.Parameters.AddWithValue("$except", exceptStepId);
            c.Parameters.AddWithValue("$resolution", resolution);
            c.Parameters.AddWithValue("$at", Date(at));
        });

    public Task UpsertTokenAsync(TokenRecord token) =>
        RunAsync("""
                 INSERT INTO tokens (id, process_id, node_id, state, wait_kind, wake_at, attempt, waiting_step_id, wait_ref,
                     from_node_id, fork, created_at)
                 VALUES ($id, $process_id, $node_id, $state, $wait_kind, $wake_at, $attempt, $waiting_step_id, $wait_ref,
                     $from_node_id, $fork, $created_at)
                 ON CONFLICT (id) DO UPDATE SET node_id = excluded.node_id, state = excluded.state,
                     wait_kind = excluded.wait_kind, wake_at = excluded.wake_at, attempt = excluded.attempt,
                     waiting_step_id = excluded.waiting_step_id, wait_ref = excluded.wait_ref,
                     from_node_id = excluded.from_node_id, fork = excluded.fork;
                 """, c =>
        {
            c.Parameters.AddWithValue("$id", token.Id);
            c.Parameters.AddWithValue("$process_id", token.ProcessId);
            c.Parameters.AddWithValue("$node_id", token.NodeId);
            c.Parameters.AddWithValue("$state", token.State.ToString());
            c.Parameters.AddWithValue("$wait_kind", token.WaitKind.ToString());
            c.Parameters.AddWithValue("$wake_at", DateOrNull(token.WakeAt));
            c.Parameters.AddWithValue("$attempt", token.Attempt);
            c.Parameters.AddWithValue("$waiting_step_id", OrNull(token.WaitingStepId));
            c.Parameters.AddWithValue("$wait_ref", OrNull(token.WaitRef));
            c.Parameters.AddWithValue("$from_node_id", OrNull(token.FromNodeId));
            c.Parameters.AddWithValue("$fork", OrNull(token.Fork));
            c.Parameters.AddWithValue("$created_at", Date(token.CreatedAt));
        });

    public async Task<long> InsertStepAsync(StepRecord step)
    {
        await using var command = Command("""
                                          INSERT INTO steps (process_id, token_id, node_id, node_type, node_label, attempt, effect,
                                              state, input, checkpoint, output, port, error, resolution, planned_at, started_at,
                                              finished_at)
                                          VALUES ($process_id, $token_id, $node_id, $node_type, $node_label, $attempt, $effect,
                                              $state, $input, $checkpoint, $output, $port, $error, $resolution, $planned_at,
                                              $started_at, $finished_at)
                                          RETURNING id;
                                          """);
        BindStep(command, step);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task UpdateStepAsync(StepRecord step)
    {
        var changed = await RunAsync("""
                                     UPDATE steps SET state = $state, input = $input, checkpoint = $checkpoint, output = $output,
                                         port = $port, error = $error, resolution = $resolution, started_at = $started_at,
                                         finished_at = $finished_at
                                     WHERE id = $id;
                                     """, c =>
        {
            BindStep(c, step);
            c.Parameters.AddWithValue("$id", step.Id);
        });
        if (changed == 0)
        {
            throw new InvalidOperationException($"Шаг {step.Id} не найден в хранилище.");
        }
    }

    public Task AddJournalAsync(JournalEntry entry) =>
        RunAsync("""
                 INSERT INTO journal (process_id, step_id, node_id, at, level, message)
                 VALUES ($process_id, $step_id, $node_id, $at, $level, $message);
                 """, c =>
        {
            c.Parameters.AddWithValue("$process_id", entry.ProcessId);
            c.Parameters.AddWithValue("$step_id", OrNull(entry.StepId));
            c.Parameters.AddWithValue("$node_id", OrNull(entry.NodeId));
            c.Parameters.AddWithValue("$at", Date(entry.At));
            c.Parameters.AddWithValue("$level", entry.Level.ToString());
            c.Parameters.AddWithValue("$message", entry.Message);
        });

    public Task SetSettingAsync(string key, string value) =>
        RunAsync("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            c =>
            {
                c.Parameters.AddWithValue("$k", key);
                c.Parameters.AddWithValue("$v", value);
            });

    // ───────────────────────────── События ─────────────────────────────

    public async Task<long?> TryInsertEventAsync(EventRecord record)
    {
        if (record.ExternalId is not null && await FindEventByExternalIdAsync(record.Source, record.ExternalId) is not null)
        {
            return null;
        }

        await using var command = Command("""
                                          INSERT INTO events (source, type, external_id, correlation_key, payload, received_at, occurred_at,
                                              state, process_id, subscription_id, candidates, started_processes, note, handled_at, secrets)
                                          VALUES ($source, $type, $external_id, $correlation_key, $payload, $received_at, $occurred_at,
                                              $state, $process_id, $subscription_id, $candidates, $started_processes, $note, $handled_at, $secrets)
                                          RETURNING id;
                                          """);
        BindEvent(command, record);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<EventRecord?> FindEventByExternalIdAsync(string source, string externalId) =>
        (await Query("SELECT * FROM events WHERE source = $s AND external_id = $e;", c =>
        {
            c.Parameters.AddWithValue("$s", source);
            c.Parameters.AddWithValue("$e", externalId);
        }, ReadEvent)).FirstOrDefault();

    public async Task<bool> TryUpdateEventAsync(EventRecord record, EventState expected) =>
        await RunAsync("""
                       UPDATE events SET state = $state, process_id = $process_id, subscription_id = $subscription_id,
                           candidates = $candidates, started_processes = $started_processes, note = $note, handled_at = $handled_at
                       WHERE id = $id AND state = $expected;
                       """, c =>
        {
            BindEvent(c, record);
            c.Parameters.AddWithValue("$id", record.Id);
            c.Parameters.AddWithValue("$expected", expected.ToString());
        }) > 0;

    public async Task<EventRecord?> GetEventAsync(long id) =>
        (await Query("SELECT * FROM events WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id), ReadEvent)).FirstOrDefault();

    public Task<IReadOnlyList<EventRecord>> GetEventsForSubscriptionAsync(long subscriptionId) =>
        Query("SELECT * FROM events WHERE subscription_id = $s ORDER BY id;",
            c => c.Parameters.AddWithValue("$s", subscriptionId), ReadEvent);

    public async Task<long> InsertSubscriptionAsync(SubscriptionRecord record)
    {
        await using var command = Command("""
                                          INSERT INTO subscriptions (process_id, token_id, step_id, node_id, source, event_type,
                                              correlation_key, match_expression, priority, lookback_seconds, save_to, purpose, state,
                                              event_id, created_at, expires_at)
                                          VALUES ($process_id, $token_id, $step_id, $node_id, $source, $event_type,
                                              $correlation_key, $match_expression, $priority, $lookback_seconds, $save_to, $purpose,
                                              $state, $event_id, $created_at, $expires_at)
                                          RETURNING id;
                                          """);
        command.Parameters.AddWithValue("$process_id", record.ProcessId);
        command.Parameters.AddWithValue("$token_id", record.TokenId);
        command.Parameters.AddWithValue("$step_id", record.StepId);
        command.Parameters.AddWithValue("$node_id", record.NodeId);
        command.Parameters.AddWithValue("$source", record.Source);
        command.Parameters.AddWithValue("$event_type", OrNull(record.EventType));
        command.Parameters.AddWithValue("$correlation_key", OrNull(record.CorrelationKey));
        command.Parameters.AddWithValue("$match_expression", OrNull(record.MatchExpression));
        command.Parameters.AddWithValue("$priority", record.Priority);
        command.Parameters.AddWithValue("$lookback_seconds", record.LookbackSeconds);
        command.Parameters.AddWithValue("$save_to", OrNull(record.SaveTo));
        command.Parameters.AddWithValue("$purpose", record.Purpose.ToString());
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$event_id", OrNull(record.EventId));
        command.Parameters.AddWithValue("$created_at", Date(record.CreatedAt));
        command.Parameters.AddWithValue("$expires_at", DateOrNull(record.ExpiresAt));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<bool> TrySetSubscriptionStateAsync(long id, SubscriptionState expected, SubscriptionState state,
        long? eventId) =>
        await RunAsync("""
                       UPDATE subscriptions SET state = $state, event_id = COALESCE($event_id, event_id)
                       WHERE id = $id AND state = $expected;
                       """, c =>
        {
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$expected", expected.ToString());
            c.Parameters.AddWithValue("$state", state.ToString());
            c.Parameters.AddWithValue("$event_id", OrNull(eventId));
        }) > 0;

    public async Task<SubscriptionRecord?> GetSubscriptionAsync(long id) =>
        (await Query("SELECT * FROM subscriptions WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id),
            ReadSubscription)).FirstOrDefault();

    public Task<IReadOnlyList<SubscriptionRecord>> GetSubscriptionsAsync(string processId) =>
        Query("SELECT * FROM subscriptions WHERE process_id = $p ORDER BY id;",
            c => c.Parameters.AddWithValue("$p", processId), ReadSubscription);

    // ───────────────────────────── Ресурсы ─────────────────────────────

    public async Task<ResourceRecord?> GetResourceAsync(string key) =>
        (await Query("SELECT * FROM resources WHERE key = $k;", c => c.Parameters.AddWithValue("$k", key), ReadResource))
        .FirstOrDefault();

    public Task UpsertResourceAsync(ResourceRecord resource) =>
        RunAsync("""
                 INSERT INTO resources (key, title, holder_process_id, acquired_at, updated_at)
                 VALUES ($key, $title, $holder, $acquired, $updated)
                 ON CONFLICT (key) DO UPDATE SET title = excluded.title, holder_process_id = excluded.holder_process_id,
                     acquired_at = excluded.acquired_at, updated_at = excluded.updated_at;
                 """, c =>
        {
            c.Parameters.AddWithValue("$key", resource.Key);
            c.Parameters.AddWithValue("$title", resource.Title);
            c.Parameters.AddWithValue("$holder", OrNull(resource.HolderProcessId));
            c.Parameters.AddWithValue("$acquired", DateOrNull(resource.AcquiredAt));
            c.Parameters.AddWithValue("$updated", Date(resource.UpdatedAt));
        });

    public Task<IReadOnlyList<ResourceRecord>> GetResourcesHeldByAsync(string processId) =>
        Query("SELECT * FROM resources WHERE holder_process_id = $p ORDER BY key;",
            c => c.Parameters.AddWithValue("$p", processId), ReadResource);

    public Task<IReadOnlyList<ResourceRecord>> GetAllResourcesAsync() =>
        Query("SELECT * FROM resources ORDER BY key;", null, ReadResource);

    public Task<IReadOnlyList<ResourceWaiterRecord>> GetWaitersAsync(string resourceKey) =>
        Query($"{WaiterSelect} WHERE w.resource_key = $k ORDER BY w.priority DESC, w.id;",
            c => c.Parameters.AddWithValue("$k", resourceKey), ReadWaiter);

    public Task<IReadOnlyList<ResourceWaiterRecord>> GetAllWaitersAsync() =>
        Query($"{WaiterSelect} ORDER BY w.resource_key, w.priority DESC, w.id;", null, ReadWaiter);

    public Task AddWaiterAsync(ResourceWaiterRecord waiter) =>
        RunAsync("""
                 INSERT INTO resource_waiters (resource_key, process_id, token_id, priority, requested_at)
                 VALUES ($key, $process, $token, $priority, $requested)
                 ON CONFLICT (resource_key, token_id) DO NOTHING;
                 """, c =>
        {
            c.Parameters.AddWithValue("$key", waiter.ResourceKey);
            c.Parameters.AddWithValue("$process", waiter.ProcessId);
            c.Parameters.AddWithValue("$token", waiter.TokenId);
            c.Parameters.AddWithValue("$priority", waiter.Priority);
            c.Parameters.AddWithValue("$requested", Date(waiter.RequestedAt));
        });

    public Task RemoveWaiterAsync(string resourceKey, string processId) =>
        RunAsync("DELETE FROM resource_waiters WHERE resource_key = $k AND process_id = $p;", c =>
        {
            c.Parameters.AddWithValue("$k", resourceKey);
            c.Parameters.AddWithValue("$p", processId);
        });

    public Task RemoveTokenWaiterAsync(string resourceKey, string tokenId) =>
        RunAsync("DELETE FROM resource_waiters WHERE resource_key = $k AND token_id = $t;", c =>
        {
            c.Parameters.AddWithValue("$k", resourceKey);
            c.Parameters.AddWithValue("$t", tokenId);
        });

    public Task RemoveWaitersOfProcessAsync(string processId) =>
        RunAsync("DELETE FROM resource_waiters WHERE process_id = $p;", c => c.Parameters.AddWithValue("$p", processId));

    // ───────────────────────────── Триггеры ─────────────────────────────

    public Task SaveTriggerAsync(TriggerRecord trigger) =>
        RunAsync("""
                 INSERT INTO triggers (id, scenario_id, kind, enabled, name, config, inputs, overlap, next_run_at, last_run_at,
                     last_process_id, missed_runs, last_missed_at, skipped_runs, last_error, created_at, updated_at)
                 VALUES ($id, $scenario_id, $kind, $enabled, $name, $config, $inputs, $overlap, $next_run_at, $last_run_at,
                     $last_process_id, $missed_runs, $last_missed_at, $skipped_runs, $last_error, $created_at, $updated_at)
                 ON CONFLICT (id) DO UPDATE SET kind = excluded.kind, enabled = excluded.enabled, name = excluded.name,
                     config = excluded.config, inputs = excluded.inputs, overlap = excluded.overlap,
                     next_run_at = excluded.next_run_at, last_run_at = excluded.last_run_at,
                     last_process_id = excluded.last_process_id, missed_runs = excluded.missed_runs,
                     last_missed_at = excluded.last_missed_at, skipped_runs = excluded.skipped_runs,
                     last_error = excluded.last_error, updated_at = excluded.updated_at;
                 """, c =>
        {
            c.Parameters.AddWithValue("$id", trigger.Id);
            c.Parameters.AddWithValue("$scenario_id", trigger.ScenarioId);
            c.Parameters.AddWithValue("$kind", trigger.Kind.ToString());
            c.Parameters.AddWithValue("$enabled", trigger.Enabled ? 1 : 0);
            c.Parameters.AddWithValue("$name", trigger.Name);
            c.Parameters.AddWithValue("$config", trigger.Config.ToJsonString());
            c.Parameters.AddWithValue("$inputs", JsonOrNull(trigger.Inputs));
            c.Parameters.AddWithValue("$overlap", trigger.Overlap);
            c.Parameters.AddWithValue("$next_run_at", DateOrNull(trigger.NextRunAt));
            c.Parameters.AddWithValue("$last_run_at", DateOrNull(trigger.LastRunAt));
            c.Parameters.AddWithValue("$last_process_id", OrNull(trigger.LastProcessId));
            c.Parameters.AddWithValue("$missed_runs", trigger.MissedRuns);
            c.Parameters.AddWithValue("$last_missed_at", DateOrNull(trigger.LastMissedAt));
            c.Parameters.AddWithValue("$skipped_runs", trigger.SkippedRuns);
            c.Parameters.AddWithValue("$last_error", OrNull(trigger.LastError));
            c.Parameters.AddWithValue("$created_at", Date(trigger.CreatedAt));
            c.Parameters.AddWithValue("$updated_at", Date(trigger.UpdatedAt));
        });

    public async Task<TriggerRecord?> GetTriggerAsync(string id) =>
        (await Query("SELECT * FROM triggers WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id), ReadTrigger)).FirstOrDefault();

    public async Task<bool> DeleteTriggerAsync(string id) =>
        await RunAsync("DELETE FROM triggers WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id)) > 0;

    // ───────────────────────────── Транзакция ─────────────────────────────

    public async Task CommitAsync()
    {
        await transaction.CommitAsync();
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_committed)
            {
                try
                {
                    await transaction.RollbackAsync();
                }
                catch (Exception)
                {
                    // Соединение уже разорвано — SQLite откатит незавершённую транзакцию сам.
                }
            }

            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
        finally
        {
            writeLock.Release();
        }
    }

    private SqliteCommand Command(string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private async Task<int> RunAsync(string sql, Action<SqliteCommand> bind)
    {
        await using var command = Command(sql);
        bind(command);
        return await command.ExecuteNonQueryAsync();
    }

    private Task<IReadOnlyList<T>> Query<T>(string sql, Action<SqliteCommand>? bind, Func<SqliteDataReader, T> map) =>
        SqliteMapping.QueryAsync(connection, transaction, sql, bind, map);

    private static void BindScenario(SqliteCommand c, StoredScenario s)
    {
        c.Parameters.AddWithValue("$id", s.Id);
        c.Parameters.AddWithValue("$name", s.Name);
        c.Parameters.AddWithValue("$description", s.Description);
        c.Parameters.AddWithValue("$definition", s.DefinitionJson);
        c.Parameters.AddWithValue("$version", s.Version);
        c.Parameters.AddWithValue("$created", Date(s.CreatedAt));
        c.Parameters.AddWithValue("$updated", Date(s.UpdatedAt));
    }

    private static void BindProcess(SqliteCommand c, ProcessRecord p)
    {
        c.Parameters.AddWithValue("$id", p.Id);
        c.Parameters.AddWithValue("$scenario_id", p.ScenarioId);
        c.Parameters.AddWithValue("$scenario_name", p.ScenarioName);
        c.Parameters.AddWithValue("$scenario_version", p.ScenarioVersion);
        c.Parameters.AddWithValue("$definition", p.DefinitionJson);
        c.Parameters.AddWithValue("$status", p.Status.ToString());
        c.Parameters.AddWithValue("$attention", p.Attention.ToString());
        c.Parameters.AddWithValue("$pause_reason", p.PauseReason.ToString());
        c.Parameters.AddWithValue("$status_message", OrNull(p.StatusMessage));
        c.Parameters.AddWithValue("$attention_step_id", OrNull(p.AttentionStepId));
        c.Parameters.AddWithValue("$attention_token_id", OrNull(p.AttentionTokenId));
        c.Parameters.AddWithValue("$variables", p.Variables.ToJsonString());
        c.Parameters.AddWithValue("$last_error", JsonOrNull(p.LastError));
        c.Parameters.AddWithValue("$memory", JsonOrNull(p.Memory));
        c.Parameters.AddWithValue("$debug", p.Debug is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(p.Debug));
        c.Parameters.AddWithValue("$secrets", Secrets(p.Secrets));
        c.Parameters.AddWithValue("$parent_process_id", OrNull(p.ParentProcessId));
        c.Parameters.AddWithValue("$origin_key", OrNull(p.OriginKey));
        c.Parameters.AddWithValue("$depth", p.Depth);
        c.Parameters.AddWithValue("$step_count", p.StepCount);
        c.Parameters.AddWithValue("$step_budget", p.StepBudget);
        c.Parameters.AddWithValue("$created_at", Date(p.CreatedAt));
        c.Parameters.AddWithValue("$updated_at", Date(p.UpdatedAt));
        c.Parameters.AddWithValue("$finished_at", DateOrNull(p.FinishedAt));
    }

    private static void BindStep(SqliteCommand c, StepRecord s)
    {
        c.Parameters.AddWithValue("$process_id", s.ProcessId);
        c.Parameters.AddWithValue("$token_id", s.TokenId);
        c.Parameters.AddWithValue("$node_id", s.NodeId);
        c.Parameters.AddWithValue("$node_type", s.NodeType);
        c.Parameters.AddWithValue("$node_label", s.NodeLabel);
        c.Parameters.AddWithValue("$attempt", s.Attempt);
        c.Parameters.AddWithValue("$effect", s.Effect.ToString());
        c.Parameters.AddWithValue("$state", s.State.ToString());
        c.Parameters.AddWithValue("$input", JsonOrNull(s.Input));
        c.Parameters.AddWithValue("$checkpoint", JsonOrNull(s.Checkpoint));
        c.Parameters.AddWithValue("$output", JsonOrNull(s.Output));
        c.Parameters.AddWithValue("$port", OrNull(s.Port));
        c.Parameters.AddWithValue("$error", OrNull(s.Error));
        c.Parameters.AddWithValue("$resolution", OrNull(s.Resolution));
        c.Parameters.AddWithValue("$planned_at", Date(s.PlannedAt));
        c.Parameters.AddWithValue("$started_at", DateOrNull(s.StartedAt));
        c.Parameters.AddWithValue("$finished_at", DateOrNull(s.FinishedAt));
    }

    private static void BindEvent(SqliteCommand c, EventRecord e)
    {
        c.Parameters.AddWithValue("$source", e.Source);
        c.Parameters.AddWithValue("$type", e.Type);
        c.Parameters.AddWithValue("$external_id", OrNull(e.ExternalId));
        c.Parameters.AddWithValue("$correlation_key", OrNull(e.CorrelationKey));
        c.Parameters.AddWithValue("$payload", JsonOrNull(e.Payload));
        c.Parameters.AddWithValue("$received_at", Date(e.ReceivedAt));
        c.Parameters.AddWithValue("$occurred_at", DateOrNull(e.OccurredAt));
        c.Parameters.AddWithValue("$state", e.State.ToString());
        c.Parameters.AddWithValue("$process_id", OrNull(e.ProcessId));
        c.Parameters.AddWithValue("$subscription_id", OrNull(e.SubscriptionId));
        c.Parameters.AddWithValue("$candidates", JsonOrNull(e.Candidates));
        c.Parameters.AddWithValue("$started_processes", JsonOrNull(e.StartedProcesses));
        c.Parameters.AddWithValue("$note", OrNull(e.Note));
        c.Parameters.AddWithValue("$handled_at", DateOrNull(e.HandledAt));
        c.Parameters.AddWithValue("$secrets", Secrets(e.Secrets));
    }

    private static object Secrets(IReadOnlyList<string>? values) =>
        values is null || values.Count == 0 ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(values);
}
