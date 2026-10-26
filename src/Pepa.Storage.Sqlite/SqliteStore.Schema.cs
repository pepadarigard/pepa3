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
        // 2: события, подписки, ресурсы, триггеры, вложенные процессы, память блоков.
        """
        ALTER TABLE processes ADD COLUMN parent_process_id TEXT NULL;
        ALTER TABLE processes ADD COLUMN origin_key TEXT NULL;
        ALTER TABLE processes ADD COLUMN depth INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE processes ADD COLUMN memory TEXT NULL;
        CREATE UNIQUE INDEX ux_processes_origin ON processes (origin_key) WHERE origin_key IS NOT NULL;
        CREATE INDEX ix_processes_parent ON processes (parent_process_id) WHERE parent_process_id IS NOT NULL;

        ALTER TABLE tokens ADD COLUMN wait_ref TEXT NULL;
        ALTER TABLE tokens ADD COLUMN from_node_id TEXT NULL;

        CREATE TABLE events (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            source            TEXT NOT NULL,
            type              TEXT NOT NULL,
            external_id       TEXT NULL,
            correlation_key   TEXT NULL,
            payload           TEXT NULL,
            received_at       TEXT NOT NULL,
            occurred_at       TEXT NULL,
            state             TEXT NOT NULL,
            process_id        TEXT NULL,
            subscription_id   INTEGER NULL,
            candidates        TEXT NULL,
            started_processes TEXT NULL,
            note              TEXT NULL,
            handled_at        TEXT NULL
        );
        CREATE UNIQUE INDEX ux_events_external ON events (source, external_id) WHERE external_id IS NOT NULL;
        CREATE INDEX ix_events_state ON events (state, id);
        CREATE INDEX ix_events_subscription ON events (subscription_id) WHERE subscription_id IS NOT NULL;

        CREATE TABLE subscriptions (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            process_id       TEXT NOT NULL REFERENCES processes (id) ON DELETE CASCADE,
            token_id         TEXT NOT NULL,
            step_id          INTEGER NOT NULL,
            node_id          TEXT NOT NULL,
            source           TEXT NOT NULL,
            event_type       TEXT NULL,
            correlation_key  TEXT NULL,
            match_expression TEXT NULL,
            priority         INTEGER NOT NULL DEFAULT 0,
            lookback_seconds INTEGER NOT NULL DEFAULT 0,
            save_to          TEXT NULL,
            purpose          TEXT NOT NULL,
            state            TEXT NOT NULL,
            event_id         INTEGER NULL,
            created_at       TEXT NOT NULL,
            expires_at       TEXT NULL
        );
        CREATE INDEX ix_subscriptions_active ON subscriptions (state, source);
        CREATE INDEX ix_subscriptions_process ON subscriptions (process_id);
        CREATE INDEX ix_subscriptions_step ON subscriptions (step_id);

        CREATE TABLE resources (
            key               TEXT PRIMARY KEY,
            title             TEXT NOT NULL,
            holder_process_id TEXT NULL,
            acquired_at       TEXT NULL,
            updated_at        TEXT NOT NULL
        );
        CREATE TABLE resource_waiters (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            resource_key TEXT NOT NULL,
            process_id   TEXT NOT NULL REFERENCES processes (id) ON DELETE CASCADE,
            token_id     TEXT NOT NULL,
            priority     INTEGER NOT NULL DEFAULT 0,
            requested_at TEXT NOT NULL,
            UNIQUE (resource_key, process_id)
        );

        CREATE TABLE triggers (
            id              TEXT PRIMARY KEY,
            scenario_id     TEXT NOT NULL,
            kind            TEXT NOT NULL,
            enabled         INTEGER NOT NULL,
            name            TEXT NOT NULL DEFAULT '',
            config          TEXT NOT NULL,
            inputs          TEXT NULL,
            overlap         TEXT NOT NULL DEFAULT 'skip',
            next_run_at     TEXT NULL,
            last_run_at     TEXT NULL,
            last_process_id TEXT NULL,
            missed_runs     INTEGER NOT NULL DEFAULT 0,
            last_missed_at  TEXT NULL,
            skipped_runs    INTEGER NOT NULL DEFAULT 0,
            last_error      TEXT NULL,
            created_at      TEXT NOT NULL,
            updated_at      TEXT NOT NULL
        );
        CREATE INDEX ix_triggers_scenario ON triggers (scenario_id);
        """,
        // 3: отладка процесса (пошаговый запуск, точки останова).
        """
        ALTER TABLE processes ADD COLUMN debug TEXT NULL;
        """,
        // 4: параллельные ветви процесса.
        """
        ALTER TABLE tokens ADD COLUMN fork TEXT NULL;
        ALTER TABLE processes ADD COLUMN attention_token_id TEXT NULL;
        """,
        // 5: место в очереди ресурса — у ветви (маркера), а не у процесса целиком.
        """
        CREATE TABLE resource_waiters_v5 (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            resource_key TEXT NOT NULL,
            process_id   TEXT NOT NULL REFERENCES processes (id) ON DELETE CASCADE,
            token_id     TEXT NOT NULL,
            priority     INTEGER NOT NULL DEFAULT 0,
            requested_at TEXT NOT NULL,
            UNIQUE (resource_key, token_id)
        );
        INSERT INTO resource_waiters_v5 (id, resource_key, process_id, token_id, priority, requested_at)
            SELECT id, resource_key, process_id, token_id, priority, requested_at FROM resource_waiters;
        DROP TABLE resource_waiters;
        ALTER TABLE resource_waiters_v5 RENAME TO resource_waiters;
        """,
    ];
}
