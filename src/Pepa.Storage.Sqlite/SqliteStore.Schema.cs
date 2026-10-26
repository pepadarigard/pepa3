namespace Pepa.Storage.Sqlite;

public sealed partial class SqliteStore
{
    /// <summary>
    /// Миграции схемы. Номер миграции = значение PRAGMA user_version после её применения.
    /// Существующие миграции не меняются — только добавляются новые.
    /// </summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE scenarios (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            definition  TEXT NOT NULL,
            version     INTEGER NOT NULL,
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL
        );

        CREATE TABLE processes (
            id                TEXT PRIMARY KEY,
            scenario_id       TEXT NOT NULL,
            scenario_name     TEXT NOT NULL,
            scenario_version  INTEGER NOT NULL,
            definition        TEXT NOT NULL,
            status            TEXT NOT NULL,
            attention         TEXT NOT NULL DEFAULT 'None',
            pause_reason      TEXT NOT NULL DEFAULT 'None',
            status_message    TEXT NULL,
            attention_step_id INTEGER NULL,
            variables         TEXT NOT NULL,
            last_error        TEXT NULL,
            step_count        INTEGER NOT NULL DEFAULT 0,
            step_budget       INTEGER NOT NULL,
            created_at        TEXT NOT NULL,
            updated_at        TEXT NOT NULL,
            finished_at       TEXT NULL
        );
        CREATE INDEX ix_processes_status ON processes (status);
        CREATE INDEX ix_processes_scenario ON processes (scenario_id, created_at);
        CREATE INDEX ix_processes_created ON processes (created_at);

        CREATE TABLE tokens (
            id              TEXT PRIMARY KEY,
            process_id      TEXT NOT NULL REFERENCES processes (id) ON DELETE CASCADE,
            node_id         TEXT NOT NULL,
            state           TEXT NOT NULL,
            wait_kind       TEXT NOT NULL DEFAULT 'None',
            wake_at         TEXT NULL,
            attempt         INTEGER NOT NULL DEFAULT 1,
            waiting_step_id INTEGER NULL,
            created_at      TEXT NOT NULL
        );
        CREATE INDEX ix_tokens_process ON tokens (process_id);
        CREATE INDEX ix_tokens_wake ON tokens (state, wake_at);

        CREATE TABLE steps (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            process_id  TEXT NOT NULL REFERENCES processes (id) ON DELETE CASCADE,
            token_id    TEXT NOT NULL,
            node_id     TEXT NOT NULL,
            node_type   TEXT NOT NULL,
            node_label  TEXT NOT NULL,
            attempt     INTEGER NOT NULL,
            effect      TEXT NOT NULL,
            state       TEXT NOT NULL,
            input       TEXT NULL,
            checkpoint  TEXT NULL,
            output      TEXT NULL,
            port        TEXT NULL,
            error       TEXT NULL,
            resolution  TEXT NULL,
            planned_at  TEXT NOT NULL,
            started_at  TEXT NULL,
            finished_at TEXT NULL
        );
        CREATE INDEX ix_steps_process ON steps (process_id, id);
        CREATE INDEX ix_steps_open ON steps (process_id, state);

        CREATE TABLE journal (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            process_id TEXT NOT NULL REFERENCES processes (id) ON DELETE CASCADE,
            step_id    INTEGER NULL,
            node_id    TEXT NULL,
            at         TEXT NOT NULL,
            level      TEXT NOT NULL,
            message    TEXT NOT NULL
        );
        CREATE INDEX ix_journal_process ON journal (process_id, id);

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
    ];
}
