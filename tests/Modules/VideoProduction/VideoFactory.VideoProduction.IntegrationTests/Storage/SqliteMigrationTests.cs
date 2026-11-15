using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteMigrationTests
{
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

                await using SqliteCommand journalCommand = connection.CreateCommand();
                journalCommand.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
                long migrationCount = (long)(await journalCommand.ExecuteScalarAsync())!;
                Assert.Equal(4, migrationCount);

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
