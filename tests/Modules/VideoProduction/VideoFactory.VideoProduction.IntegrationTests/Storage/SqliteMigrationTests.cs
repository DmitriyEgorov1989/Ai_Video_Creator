using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteMigrationTests
{
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
                CREATE TABLE jobs (job_id TEXT PRIMARY KEY, status INTEGER NOT NULL, updated_at TEXT NOT NULL, lease_owner TEXT NULL);
                INSERT INTO jobs VALUES ('completed', 3, '2000-01-01T00:00:00Z', 'old-worker');
                INSERT INTO jobs VALUES ('active', 1, '2000-01-01T00:00:00Z', 'active-worker');
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

                await using SqliteCommand journalCommand = connection.CreateCommand();
                journalCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
                long migrationCount = (long)(await journalCommand.ExecuteScalarAsync())!;
                 Assert.Equal(9, migrationCount);

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
