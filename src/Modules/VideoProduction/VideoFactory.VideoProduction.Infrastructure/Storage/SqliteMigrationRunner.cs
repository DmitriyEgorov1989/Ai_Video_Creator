using Microsoft.Data.Sqlite;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>
/// Создаёт и обновляет локальную SQLite-схему модуля VideoProduction.
/// </summary>
public sealed class SqliteMigrationRunner
{
    private const int InitialSchemaVersion = 1;
    private const int HeartbeatSchemaVersion = 2;
    private const int EditPlanSchemaVersion = 3;
    private const int DeliverySchemaVersion = 4;
    private readonly string connectionString;

    /// <summary>
    /// Создаёт runner для указанной SQLite-базы.
    /// </summary>
    /// <param name="connectionString">Строка подключения к базе данных.</param>
    public SqliteMigrationRunner(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <summary>
    /// Инициализирует схему и безопасно повторяет уже применённые миграции.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Асинхронная операция инициализации схемы.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);

        await ConfigureConnectionAsync(connection, cancellationToken);
        await ExecuteAsync(connection, transaction: null, "PRAGMA foreign_keys = OFF;", cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """,
            cancellationToken);

        if (!await IsAppliedAsync(connection, transaction, InitialSchemaVersion, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, InitialSchemaSql, cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (1, $appliedAt);",
                cancellationToken,
                ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, HeartbeatSchemaVersion, cancellationToken))
        {
            await ExecuteAsync(
                connection,
                transaction,
                HeartbeatSchemaSql,
                cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (2, $appliedAt);",
                cancellationToken,
                ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, EditPlanSchemaVersion, cancellationToken))
        {
            await ExecuteAsync(
                connection,
                transaction,
                EditPlanSchemaSql,
                cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (3, $appliedAt);",
                cancellationToken,
                ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, DeliverySchemaVersion, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, DeliverySchemaSql, cancellationToken);
            await ExecuteAsync(
                connection,
                transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (4, $appliedAt);",
                cancellationToken,
                ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 5, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                ALTER TABLE jobs ADD COLUMN completed_at TEXT NULL;
                ALTER TABLE jobs ADD COLUMN cleanup_started_at TEXT NULL;
                ALTER TABLE jobs ADD COLUMN files_deleted_at TEXT NULL;
                UPDATE jobs SET completed_at = updated_at, lease_owner = NULL WHERE status = 3;
                CREATE INDEX ix_jobs_retention ON jobs (status, completed_at, files_deleted_at);
                """, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (5, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 6, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, PlanningAttemptSchemaSql, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (6, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 7, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, PlanningApprovalSchemaSql, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (7, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 8, cancellationToken))
        {
            await ExecuteAsync(connection, transaction,
                "ALTER TABLE jobs ADD COLUMN telegram_user_id INTEGER NULL;",
                cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (8, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 9, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, CancellationSchemaSql, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (9, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 10, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, RepeatDeliverySchemaSql, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (10, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 11, cancellationToken))
        {
            await EnsureColumnAsync(
                connection, transaction, "jobs", "active_variant_id", "TEXT NULL", cancellationToken);
            await ExecuteAsync(connection, transaction, OrderedJobSourcesSchemaSql, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (11, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 12, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, TelegramIntakeSchemaSql, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (12, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 13, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, CritiqueAttemptSchemaSql, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (13, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 14, cancellationToken))
        {
            await EnsureColumnAsync(
                connection, transaction, "jobs", "active_variant_id", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "variants", "parent_variant_id", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "variants", "change_request", "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(
                connection, transaction, "variants", "request_key", "TEXT NULL", cancellationToken);
            await ExecuteAsync(connection, transaction, AlternativeVariantSchemaSql, cancellationToken);
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (14, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }


        if (!await IsAppliedAsync(connection, transaction, 15, cancellationToken))
        {
            if (await TableExistsAsync(connection, transaction, "artifacts", cancellationToken))
            {
                await EnsureColumnAsync(
                    connection, transaction, "artifacts", "variant_id", "TEXT NULL", cancellationToken);
                await ExecuteAsync(connection, transaction,
                    "CREATE INDEX IF NOT EXISTS ix_artifacts_variant_type_created " +
                    "ON artifacts (job_id, variant_id, artifact_type, created_at);",
                    cancellationToken);
                await ExecuteAsync(connection, transaction, """
                    UPDATE artifacts
                    SET variant_id = (
                        SELECT jobs.active_variant_id FROM jobs WHERE jobs.job_id = artifacts.job_id)
                    WHERE variant_id IS NULL
                      AND artifact_type IN (
                          'render-output', 'objective-critique-report', 'qa-report',
                          'delivery-encode-output', 'final-mp4', 'manifest');
                    """, cancellationToken);
            }
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (15, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 16, cancellationToken))
        {
            if (await TableExistsAsync(connection, transaction, "delivery_receipts", cancellationToken))
            {
                await EnsureColumnAsync(
                    connection, transaction, "delivery_receipts", "uploaded_sha256", "TEXT NULL", cancellationToken);
                await EnsureColumnAsync(
                    connection, transaction, "delivery_receipts", "uploaded_size_bytes", "INTEGER NULL", cancellationToken);
            }
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (16, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        if (!await IsAppliedAsync(connection, transaction, 17, cancellationToken))
        {
            if (await TableExistsAsync(connection, transaction, "delivery_receipts", cancellationToken))
            {
                await EnsureColumnAsync(
                    connection, transaction, "delivery_receipts", "receipt_format_version", "INTEGER NULL", cancellationToken);
                await ExecuteAsync(connection, transaction, """
                    UPDATE delivery_receipts
                    SET receipt_format_version = CASE
                        WHEN uploaded_sha256 IS NOT NULL AND uploaded_size_bytes > 0 THEN 2
                        ELSE 1
                    END
                    WHERE receipt_format_version IS NULL;
                    """, cancellationToken);
            }
            if (await TableExistsAsync(connection, transaction, "planning_attempts", cancellationToken))
            {
                await EnsureColumnAsync(
                    connection, transaction, "planning_attempts", "attempt_key", "TEXT NULL", cancellationToken);
                await EnsureColumnAsync(
                    connection, transaction, "planning_attempts", "completed_at", "TEXT NULL", cancellationToken);
                await ExecuteAsync(connection, transaction, """
                    CREATE UNIQUE INDEX IF NOT EXISTS ux_planning_attempts_intent_key
                        ON planning_attempts (attempt_key) WHERE attempt_key IS NOT NULL;
                    CREATE INDEX IF NOT EXISTS ix_planning_attempts_job_status
                        ON planning_attempts (job_id, status);
                    """, cancellationToken);
            }
            if (await TableExistsAsync(connection, transaction, "planning_approvals", cancellationToken))
            {
                await EnsureColumnAsync(
                    connection, transaction, "planning_approvals", "repair_attempt", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
                await ExecuteAsync(connection, transaction, """
                    UPDATE planning_approvals
                    SET repair_attempt = CASE
                        WHEN version >= 1 THEN version - 1
                        ELSE -1
                    END;
                    """, cancellationToken);
                await ExecuteAsync(connection, transaction, """
                    CREATE INDEX IF NOT EXISTS ix_planning_approvals_cost_snapshot
                        ON planning_approvals (
                            job_id, variant_id, repair_attempt,
                            estimated_cost_usd, accumulated_actual_cost_usd, status);
                    """, cancellationToken);
            }
            await ExecuteAsync(connection, transaction,
                "INSERT INTO schema_migrations (version, applied_at) VALUES (17, $appliedAt);",
                cancellationToken, ("$appliedAt", DateTimeOffset.UtcNow.ToString("O")));
        }

        await transaction.CommitAsync(cancellationToken);
        await ExecuteAsync(connection, transaction: null, "PRAGMA foreign_keys = ON;", cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table);";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction: null,
            "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;",
            cancellationToken);
    }

    private static async Task<bool> IsAppliedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE version = $version);";
        command.Parameters.AddWithValue("$version", version);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value) == 1;
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"SELECT EXISTS (SELECT 1 FROM pragma_table_info('{table}') WHERE name = $column);";
        query.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            await ExecuteAsync(
                connection,
                transaction,
                $"ALTER TABLE {table} ADD COLUMN {column} {definition};",
                cancellationToken);
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string InitialSchemaSql = """
        CREATE TABLE jobs (
            job_id TEXT PRIMARY KEY,
            source_artifact_id TEXT NOT NULL UNIQUE,
            comment TEXT NOT NULL,
            requested_mode INTEGER NOT NULL,
            target_duration_seconds INTEGER NULL,
            captions_enabled INTEGER NOT NULL,
            music_enabled INTEGER NOT NULL,
            status INTEGER NOT NULL,
            stage INTEGER NOT NULL,
            effective_mode INTEGER NULL,
            pending_confirmation INTEGER NULL,
            current_approved_edit_plan_revision_id TEXT NULL,
            active_variant_id TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE variants (
            variant_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL,
            parent_variant_id TEXT NULL,
            change_request TEXT NULL,
            request_key TEXT NULL,
            current_approved_edit_plan_revision_id TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE
        );

        CREATE TABLE stage_attempts (
            attempt_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL,
            variant_id TEXT NULL,
            stage INTEGER NOT NULL,
            attempt_number INTEGER NOT NULL,
            status TEXT NOT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            started_at TEXT NOT NULL,
            completed_at TEXT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (variant_id) REFERENCES variants (variant_id) ON DELETE SET NULL,
            UNIQUE (job_id, stage, attempt_number)
        );

        CREATE TABLE artifacts (
            artifact_id TEXT PRIMARY KEY,
            job_id TEXT NULL,
            artifact_type TEXT NOT NULL,
            relative_path TEXT NOT NULL UNIQUE,
            sha256 TEXT NULL,
            size_bytes INTEGER NULL,
            metadata_json TEXT NULL,
            parent_artifact_id TEXT NULL,
            tool_version TEXT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (parent_artifact_id) REFERENCES artifacts (artifact_id)
        );

        CREATE TABLE events (
            event_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL,
            variant_id TEXT NULL,
            event_type TEXT NOT NULL,
            stage INTEGER NULL,
            payload_json TEXT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (variant_id) REFERENCES variants (variant_id) ON DELETE SET NULL
        );

        CREATE INDEX ix_stage_attempts_job_stage
            ON stage_attempts (job_id, stage);

        CREATE INDEX ix_artifacts_job
            ON artifacts (job_id);

        CREATE INDEX ix_events_job_created
            ON events (job_id, created_at);
        """;

    private const string HeartbeatSchemaSql = """
        ALTER TABLE jobs ADD COLUMN heartbeat_at TEXT NULL;
        ALTER TABLE jobs ADD COLUMN lease_owner TEXT NULL;

        CREATE INDEX ix_jobs_work_status_heartbeat
            ON jobs (status, heartbeat_at, created_at);
        """;

    private const string EditPlanSchemaSql = """
        CREATE TABLE edit_plan_revisions (
            revision_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL,
            variant_id TEXT NOT NULL,
            analysis_revision_id TEXT NOT NULL,
            plan_hash TEXT NOT NULL,
            plan_json TEXT NOT NULL,
            actual_cost_usd TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (variant_id) REFERENCES variants (variant_id) ON DELETE CASCADE,
            UNIQUE (job_id, variant_id, plan_hash)
        );

        CREATE INDEX ix_edit_plan_revisions_job_variant
            ON edit_plan_revisions (job_id, variant_id);
        """;

    private const string DeliverySchemaSql = """
        CREATE TABLE delivery_receipts (
            receipt_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL,
            variant_id TEXT NOT NULL,
            artifact_id TEXT NOT NULL,
            recipient_id INTEGER NOT NULL,
            remote_message_id TEXT NULL,
            status TEXT NOT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            uploaded_sha256 TEXT NULL,
            uploaded_size_bytes INTEGER NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (variant_id) REFERENCES variants (variant_id) ON DELETE CASCADE,
            FOREIGN KEY (artifact_id) REFERENCES artifacts (artifact_id) ON DELETE CASCADE
        );

        CREATE INDEX ix_delivery_receipts_job
            ON delivery_receipts (job_id, created_at);
        """;

    private const string RepeatDeliverySchemaSql = """
        CREATE TABLE repeat_delivery_requests (
            telegram_update_id INTEGER PRIMARY KEY,
            job_id TEXT NOT NULL,
            variant_id TEXT NULL,
            artifact_id TEXT NULL,
            telegram_user_id INTEGER NOT NULL,
            receipt_id TEXT NULL,
            remote_message_id TEXT NULL,
            status TEXT NOT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            created_at TEXT NOT NULL,
            completed_at TEXT NULL
        );

        CREATE INDEX ix_repeat_delivery_requests_job
            ON repeat_delivery_requests (job_id, created_at);
        """;

    private const string OrderedJobSourcesSchemaSql = """
        CREATE TABLE jobs_v11 (
            job_id TEXT PRIMARY KEY,
            source_artifact_id TEXT NOT NULL,
            comment TEXT NOT NULL,
            requested_mode INTEGER NOT NULL,
            target_duration_seconds INTEGER NULL,
            captions_enabled INTEGER NOT NULL,
            music_enabled INTEGER NOT NULL,
            status INTEGER NOT NULL,
            stage INTEGER NOT NULL,
            effective_mode INTEGER NULL,
            pending_confirmation INTEGER NULL,
            current_approved_edit_plan_revision_id TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            heartbeat_at TEXT NULL,
            lease_owner TEXT NULL,
            completed_at TEXT NULL,
            cleanup_started_at TEXT NULL,
            files_deleted_at TEXT NULL,
            telegram_user_id INTEGER NULL,
            edit_tempo INTEGER NOT NULL DEFAULT 1,
            requested_style INTEGER NOT NULL DEFAULT 0,
            active_variant_id TEXT NULL
        );

        INSERT INTO jobs_v11 (
            job_id, source_artifact_id, comment, requested_mode,
            target_duration_seconds, captions_enabled, music_enabled,
            status, stage, effective_mode, pending_confirmation,
            current_approved_edit_plan_revision_id, created_at, updated_at,
            heartbeat_at, lease_owner, completed_at, cleanup_started_at,
            files_deleted_at, telegram_user_id, edit_tempo, requested_style,
            active_variant_id)
        SELECT
            job_id, source_artifact_id, comment, requested_mode,
            target_duration_seconds, captions_enabled, music_enabled,
            status, stage, effective_mode, pending_confirmation,
            current_approved_edit_plan_revision_id, created_at, updated_at,
            heartbeat_at, lease_owner, completed_at, cleanup_started_at,
            files_deleted_at, telegram_user_id, 1, 0, active_variant_id
        FROM jobs;

        DROP TABLE jobs;
        ALTER TABLE jobs_v11 RENAME TO jobs;

        CREATE INDEX ix_jobs_work_status_heartbeat
            ON jobs (status, heartbeat_at, created_at);
        CREATE INDEX ix_jobs_retention
            ON jobs (status, completed_at, files_deleted_at);

        CREATE TABLE job_sources (
            job_id TEXT NOT NULL,
            source_artifact_id TEXT NOT NULL,
            source_order INTEGER NOT NULL CHECK (source_order >= 0),
            created_at TEXT NOT NULL,
            PRIMARY KEY (job_id, source_artifact_id),
            UNIQUE (job_id, source_order),
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (source_artifact_id) REFERENCES artifacts (artifact_id)
        );

        INSERT INTO job_sources (job_id, source_artifact_id, source_order, created_at)
        SELECT job_id, source_artifact_id, 0, created_at FROM jobs;
        """;

    private const string TelegramIntakeSchemaSql = """
        CREATE TABLE telegram_intake_sessions (
            session_id TEXT PRIMARY KEY,
            completion_identity TEXT NOT NULL UNIQUE,
            user_id INTEGER NOT NULL,
            chat_id INTEGER NOT NULL,
            status INTEGER NOT NULL CHECK (status IN (0, 1, 2)),
            instruction TEXT NULL,
            requested_style INTEGER NOT NULL DEFAULT 0,
            target_duration_seconds INTEGER NULL,
            job_id TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id)
        );

        CREATE UNIQUE INDEX ux_telegram_intake_one_open_user
            ON telegram_intake_sessions (user_id) WHERE status = 0;

        CREATE TABLE telegram_intake_items (
            session_id TEXT NOT NULL,
            chat_id INTEGER NOT NULL,
            message_id INTEGER NOT NULL,
            file_unique_id TEXT NULL,
            status INTEGER NOT NULL CHECK (status IN (0, 1, 2)),
            temporary_relative_path TEXT NOT NULL,
            reserved_bytes INTEGER NOT NULL CHECK (reserved_bytes > 0),
            artifact_id TEXT NULL,
            sha256 TEXT NULL,
            actual_size_bytes INTEGER NULL,
            duration_microseconds INTEGER NULL,
            error_code TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (chat_id, message_id),
            FOREIGN KEY (session_id) REFERENCES telegram_intake_sessions (session_id) ON DELETE CASCADE,
            FOREIGN KEY (artifact_id) REFERENCES artifacts (artifact_id)
        );

        CREATE INDEX ix_telegram_intake_items_session_message
            ON telegram_intake_items (session_id, message_id);
        CREATE UNIQUE INDEX ux_telegram_intake_items_file_unique
            ON telegram_intake_items (session_id, file_unique_id)
            WHERE file_unique_id IS NOT NULL AND status IN (0, 1);

        CREATE TABLE telegram_intake_completion_receipts (
            chat_id INTEGER NOT NULL,
            done_message_id INTEGER NOT NULL,
            session_id TEXT NOT NULL,
            job_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (chat_id, done_message_id),
            FOREIGN KEY (session_id) REFERENCES telegram_intake_sessions (session_id),
            FOREIGN KEY (job_id) REFERENCES jobs (job_id)
        );
        """;

    private const string PlanningAttemptSchemaSql = """
        CREATE TABLE planning_attempts (
            attempt_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL,
            variant_id TEXT NOT NULL,
            attempt_number INTEGER NOT NULL,
            repair_attempt INTEGER NOT NULL,
            estimated_cost_usd TEXT NOT NULL,
            actual_cost_usd TEXT NULL,
            status TEXT NOT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (variant_id) REFERENCES variants (variant_id) ON DELETE CASCADE
        );

        CREATE INDEX ix_planning_attempts_job_variant
            ON planning_attempts (job_id, variant_id, attempt_number);
        """;

    private const string PlanningApprovalSchemaSql = """
        CREATE TABLE planning_approvals (
            approval_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL,
            variant_id TEXT NOT NULL,
            telegram_user_id INTEGER NOT NULL,
            version INTEGER NOT NULL,
            estimated_cost_usd TEXT NOT NULL,
            accumulated_actual_cost_usd TEXT NOT NULL,
            limit_usd TEXT NOT NULL,
            status TEXT NOT NULL,
            created_at TEXT NOT NULL,
            decided_at TEXT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (variant_id) REFERENCES variants (variant_id) ON DELETE CASCADE,
            UNIQUE (job_id, variant_id, version)
        );

        CREATE UNIQUE INDEX ix_planning_approvals_current
            ON planning_approvals (job_id, variant_id)
            WHERE status = 'Pending';
        """;

    private const string CancellationSchemaSql = """
        CREATE TABLE job_cancellation_requests (
            job_id TEXT PRIMARY KEY,
            requested_by_user_id INTEGER NOT NULL,
            reason TEXT NOT NULL,
            requested_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE
        );

        CREATE INDEX ix_job_cancellation_requests_requested_at
            ON job_cancellation_requests (requested_at);
        """;

    private const string CritiqueAttemptSchemaSql = """
        CREATE TABLE critique_attempts (
            job_id TEXT NOT NULL,
            variant_id TEXT NOT NULL,
            source_revision_id TEXT NOT NULL,
            render_artifact_id TEXT NOT NULL,
            correction_revision_id TEXT NULL,
            remount_number INTEGER NOT NULL CHECK (remount_number >= 0),
            estimated_cost_usd TEXT NOT NULL,
            actual_cost_usd TEXT NULL,
            status TEXT NOT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            created_at TEXT NOT NULL,
            completed_at TEXT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (variant_id) REFERENCES variants (variant_id) ON DELETE CASCADE,
            FOREIGN KEY (source_revision_id) REFERENCES edit_plan_revisions (revision_id),
            FOREIGN KEY (render_artifact_id) REFERENCES artifacts (artifact_id),
            FOREIGN KEY (correction_revision_id) REFERENCES edit_plan_revisions (revision_id),
            UNIQUE (variant_id, source_revision_id, render_artifact_id)
        );

        CREATE INDEX ix_critique_attempts_job_variant
            ON critique_attempts (job_id, variant_id, remount_number);
        """;

    private const string AlternativeVariantSchemaSql = """
        CREATE TABLE variants_v14 (
            variant_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL,
            parent_variant_id TEXT NULL,
            change_request TEXT NULL,
            request_key TEXT NULL,
            current_approved_edit_plan_revision_id TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (parent_variant_id) REFERENCES variants_v14 (variant_id)
        );

        INSERT INTO variants_v14 (
            variant_id, job_id, parent_variant_id, change_request, request_key,
            current_approved_edit_plan_revision_id, created_at, updated_at)
        SELECT variant_id, job_id, parent_variant_id, change_request, request_key,
               current_approved_edit_plan_revision_id, created_at, updated_at
        FROM variants;

        DROP TABLE variants;
        ALTER TABLE variants_v14 RENAME TO variants;

        CREATE INDEX ix_variants_job ON variants (job_id);
        CREATE UNIQUE INDEX ux_variants_job_request_key
            ON variants (job_id, request_key) WHERE request_key IS NOT NULL;

        UPDATE jobs
        SET active_variant_id = (
            SELECT variant_id FROM variants
            WHERE variants.job_id = jobs.job_id
            ORDER BY created_at, variant_id
            LIMIT 1)
        WHERE active_variant_id IS NULL;
        """;
}
