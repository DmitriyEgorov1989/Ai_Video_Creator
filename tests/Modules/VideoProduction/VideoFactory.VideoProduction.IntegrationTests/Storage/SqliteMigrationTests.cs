using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteMigrationTests
{
    [Fact]
    public async Task VersionTenJobIsBackfilledIntoOrderedSources()
    {
        string path = Path.Combine(Path.GetTempPath(), $"multi-source-upgrade-{Guid.NewGuid():N}.db");
        string cs = $"Data Source={path};Pooling=False";
        string sourceId = Guid.NewGuid().ToString("N");
        string jobId = Guid.NewGuid().ToString("N");
        try
        {
            await using SqliteConnection connection = new(cs);
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_migrations
                SELECT value, '2000' FROM json_each('[1,2,3,4,5,6,7,8,9,10]');
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
                    updated_at TEXT NOT NULL,
                    heartbeat_at TEXT NULL,
                    lease_owner TEXT NULL,
                    completed_at TEXT NULL,
                    cleanup_started_at TEXT NULL,
                    files_deleted_at TEXT NULL,
                    telegram_user_id INTEGER NULL);
                CREATE TABLE variants (
                    variant_id TEXT PRIMARY KEY,
                    job_id TEXT NOT NULL UNIQUE,
                    current_approved_edit_plan_revision_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
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
                    created_at TEXT NOT NULL);
                INSERT INTO artifacts VALUES ($sourceId, $jobId, 'OriginalVideo', 'source.bin', 'abc', 3, '{}', NULL, 'test', '2000');
                INSERT INTO jobs VALUES ($jobId, $sourceId, 'instruction', 0, NULL, 1, 0, 0, 0, NULL, NULL, NULL, '2000', '2000', NULL, NULL, NULL, NULL, NULL, 123);
                """;
            command.Parameters.AddWithValue("$sourceId", sourceId);
            command.Parameters.AddWithValue("$jobId", jobId);
            await command.ExecuteNonQueryAsync();

            await new SqliteMigrationRunner(cs).InitializeAsync(CancellationToken.None);

            command.CommandText = "SELECT source_artifact_id, source_order FROM job_sources WHERE job_id = $jobId;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(sourceId, reader.GetString(0));
            Assert.Equal(0, reader.GetInt32(1));
            await reader.DisposeAsync();
            command.CommandText = """
                SELECT COUNT(*)
                FROM pragma_index_list('jobs') AS indexes
                INNER JOIN pragma_index_info(indexes.name) AS columns
                    ON columns.name = 'source_artifact_id'
                WHERE indexes.[unique] = 1;
                """;
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task UpgradeFromVersionFourPreservesHistoryAndReleasesCompletedLease()
    {
        string path = Path.Combine(Path.GetTempPath(), $"retention-upgrade-{Guid.NewGuid():N}.db");
        string cs = $"Data Source={path};Pooling=False";
        try
        {
            await using SqliteConnection connection = new(cs);
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_migrations VALUES (1, '2000'), (2, '2000'), (3, '2000'), (4, '2000');
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
                    updated_at TEXT NOT NULL,
                    heartbeat_at TEXT NULL,
                    lease_owner TEXT NULL);
                CREATE TABLE variants (
                    variant_id TEXT PRIMARY KEY,
                    job_id TEXT NOT NULL UNIQUE,
                    current_approved_edit_plan_revision_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
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
                    created_at TEXT NOT NULL);
                INSERT INTO artifacts VALUES ('source-completed', 'completed', 'OriginalVideo', 'completed.bin', NULL, NULL, NULL, NULL, NULL, '2000');
                INSERT INTO artifacts VALUES ('source-active', 'active', 'OriginalVideo', 'active.bin', NULL, NULL, NULL, NULL, NULL, '2000');
                INSERT INTO jobs VALUES ('completed', 'source-completed', 'instruction', 0, NULL, 1, 0, 3, 8, 0, NULL, NULL, '2000', '2000-01-01T00:00:00Z', NULL, 'old-worker');
                INSERT INTO jobs VALUES ('active', 'source-active', 'instruction', 0, NULL, 1, 0, 1, 0, 0, NULL, NULL, '2000', '2000-01-01T00:00:00Z', NULL, 'active-worker');
                """;
            await command.ExecuteNonQueryAsync();
            await new SqliteMigrationRunner(cs).InitializeAsync(CancellationToken.None);
            await new SqliteMigrationRunner(cs).InitializeAsync(CancellationToken.None);
            command.CommandText = "SELECT completed_at, lease_owner FROM jobs WHERE job_id = 'completed';";
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal("2000-01-01T00:00:00Z", reader.GetString(0));
                Assert.True(reader.IsDBNull(1));
            }
            command.CommandText = "SELECT completed_at, lease_owner FROM jobs WHERE job_id = 'active';";
            await using SqliteDataReader active = await command.ExecuteReaderAsync();
            Assert.True(await active.ReadAsync());
            Assert.True(active.IsDBNull(0));
            Assert.Equal("active-worker", active.GetString(1));
        }
        finally
        {
            File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task InitializeCreatesRequiredSchemaAndCanRunAgain()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"video-factory-{Guid.NewGuid():N}.db");

        try
        {
            string connectionString = $"Data Source={databasePath};Cache=Shared;Pooling=False";
            SqliteMigrationRunner runner = new(connectionString);

            await runner.InitializeAsync(CancellationToken.None);
            await runner.InitializeAsync(CancellationToken.None);

            await using (SqliteConnection connection = new(connectionString))
            {
                await connection.OpenAsync();
                HashSet<string> tables = await ReadTablesAsync(connection);

                Assert.Contains("schema_migrations", tables);
                Assert.Contains("jobs", tables);
                Assert.Contains("variants", tables);
                Assert.Contains("stage_attempts", tables);
                Assert.Contains("artifacts", tables);
                Assert.Contains("events", tables);
                Assert.Contains("edit_plan_revisions", tables);
                Assert.Contains("delivery_receipts", tables);
                Assert.Contains("planning_attempts", tables);
                 Assert.Contains("planning_approvals", tables);
                 Assert.Contains("job_cancellation_requests", tables);
                 Assert.Contains("repeat_delivery_requests", tables);
                 Assert.Contains("job_sources", tables);
                 Assert.Contains("telegram_intake_sessions", tables);
                 Assert.Contains("telegram_intake_items", tables);
                 Assert.Contains("telegram_intake_completion_receipts", tables);
                 Assert.Contains("critique_attempts", tables);

                await using SqliteCommand journalCommand = connection.CreateCommand();
                journalCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
                long migrationCount = (long)(await journalCommand.ExecuteScalarAsync())!;
                 Assert.Equal(15, migrationCount);

                 await using SqliteCommand critiqueIndexCommand = connection.CreateCommand();
                 critiqueIndexCommand.CommandText = """
                     SELECT COUNT(*)
                     FROM pragma_index_list('critique_attempts') AS indexes
                     WHERE indexes.[unique] = 1
                       AND (
                           SELECT group_concat(name, ',')
                           FROM (
                               SELECT columns.name
                               FROM pragma_index_info(indexes.name) AS columns
                               ORDER BY columns.seqno)) = 'variant_id,source_revision_id,render_artifact_id';
                     """;
                 Assert.Equal(1L, (long)(await critiqueIndexCommand.ExecuteScalarAsync())!);

                await using SqliteCommand retentionCommand = connection.CreateCommand();
                retentionCommand.CommandText = "SELECT completed_at, cleanup_started_at, files_deleted_at FROM jobs LIMIT 0;";
                await using SqliteDataReader retentionReader = await retentionCommand.ExecuteReaderAsync();

                await using SqliteCommand journalModeCommand = connection.CreateCommand();
                journalModeCommand.CommandText = "PRAGMA journal_mode;";
                string journalMode = (string)(await journalModeCommand.ExecuteScalarAsync())!;
                Assert.Equal("wal", journalMode, ignoreCase: true);
            }
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-wal");
            File.Delete($"{databasePath}-shm");
        }
    }

    private static async Task<HashSet<string>> ReadTablesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        HashSet<string> tables = [];

        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
