using Microsoft.Data.Sqlite;
using Pepa.Engine.Persistence;
using static Pepa.Storage.Sqlite.SqliteStore;

namespace Pepa.Storage.Sqlite;

/// <summary>Одна транзакция записи. Держит общий замок записи до освобождения.</summary>
internal sealed class SqliteWrite(SqliteConnection connection, SqliteTransaction transaction, SemaphoreSlim writeLock)
    : IStoreWrite
{
    private bool _committed;
    private bool _disposed;

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

    public async Task<bool> DeleteScenarioAsync(string id) =>
        await RunAsync("DELETE FROM scenarios WHERE id = $id;", c => c.Parameters.AddWithValue("$id", id)) > 0;

    public Task InsertProcessAsync(ProcessRecord process) =>
        RunAsync("""
                 INSERT INTO processes (id, scenario_id, scenario_name, scenario_version, definition, status, attention,
                     pause_reason, status_message, attention_step_id, variables, last_error, step_count, step_budget,
                     created_at, updated_at, finished_at)
                 VALUES ($id, $scenario_id, $scenario_name, $scenario_version, $definition, $status, $attention,
                     $pause_reason, $status_message, $attention_step_id, $variables, $last_error, $step_count, $step_budget,
                     $created_at, $updated_at, $finished_at);
                 """, c => BindProcess(c, process));

    public async Task UpdateProcessAsync(ProcessRecord process)
    {
        var changed = await RunAsync("""
                                     UPDATE processes SET status = $status, attention = $attention, pause_reason = $pause_reason,
                                         status_message = $status_message, attention_step_id = $attention_step_id,
                                         variables = $variables, last_error = $last_error, step_count = $step_count,
                                         step_budget = $step_budget, updated_at = $updated_at, finished_at = $finished_at
                                     WHERE id = $id;
                                     """, c => BindProcess(c, process));
        if (changed == 0)
        {
            throw new InvalidOperationException($"Процесс {process.Id} не найден в хранилище.");
        }
    }

    public Task UpsertTokenAsync(TokenRecord token) =>
        RunAsync("""
                 INSERT INTO tokens (id, process_id, node_id, state, wait_kind, wake_at, attempt, waiting_step_id, created_at)
                 VALUES ($id, $process_id, $node_id, $state, $wait_kind, $wake_at, $attempt, $waiting_step_id, $created_at)
                 ON CONFLICT (id) DO UPDATE SET node_id = excluded.node_id, state = excluded.state,
                     wait_kind = excluded.wait_kind, wake_at = excluded.wake_at, attempt = excluded.attempt,
                     waiting_step_id = excluded.waiting_step_id;
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
        c.Parameters.AddWithValue("$variables", p.Variables.ToJsonString());
        c.Parameters.AddWithValue("$last_error", JsonOrNull(p.LastError));
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
}
