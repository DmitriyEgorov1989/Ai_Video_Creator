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

        await transaction.CommitAsync(cancellationToken);
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
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE variants (
            variant_id TEXT PRIMARY KEY,
            job_id TEXT NOT NULL UNIQUE,
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
            created_at TEXT NOT NULL,
            FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE,
            FOREIGN KEY (variant_id) REFERENCES variants (variant_id) ON DELETE CASCADE,
            FOREIGN KEY (artifact_id) REFERENCES artifacts (artifact_id) ON DELETE CASCADE
        );

        CREATE INDEX ix_delivery_receipts_job
            ON delivery_receipts (job_id, created_at);
        """;
}
