using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteVariantMigrationTests
{
    [Fact]
    public async Task VersionThirteenVariantIsPreservedAndMadeActive()
    {
        string path = Path.Combine(Path.GetTempPath(), $"variant-v14-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={path};Pooling=False";
        string jobId = Guid.NewGuid().ToString("N");
        string variantId = Guid.NewGuid().ToString("N");
        try
        {
            await using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                INSERT INTO schema_migrations
                SELECT value, '2000' FROM json_each('[1,2,3,4,5,6,7,8,9,10,11,12,13]');
                CREATE TABLE jobs (
                    job_id TEXT PRIMARY KEY,
                    status INTEGER NOT NULL);
                CREATE TABLE variants (
                    variant_id TEXT PRIMARY KEY,
                    job_id TEXT NOT NULL UNIQUE,
                    current_approved_edit_plan_revision_id TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY (job_id) REFERENCES jobs (job_id) ON DELETE CASCADE);
                INSERT INTO jobs VALUES ($jobId, 3);
                INSERT INTO variants VALUES ($variantId, $jobId, 'revision-1', '2000', '2000');
                """;
            command.Parameters.AddWithValue("$jobId", jobId);
            command.Parameters.AddWithValue("$variantId", variantId);
            await command.ExecuteNonQueryAsync();

            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);

            command.CommandText = """
                SELECT j.active_variant_id, v.current_approved_edit_plan_revision_id,
                       v.parent_variant_id, v.change_request, v.request_key
                FROM jobs j INNER JOIN variants v ON v.variant_id = j.active_variant_id
                WHERE j.job_id = $jobId;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(variantId, reader.GetString(0));
            Assert.Equal("revision-1", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.True(reader.IsDBNull(3));
            Assert.True(reader.IsDBNull(4));
            await reader.DisposeAsync();

            command.CommandText = """
                SELECT COUNT(*) FROM pragma_index_list('variants') AS indexes
                INNER JOIN pragma_index_info(indexes.name) AS columns
                    ON columns.name = 'job_id'
                WHERE indexes.[unique] = 1
                  AND indexes.name <> 'ux_variants_job_request_key';
                """;
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
            Assert.Equal(15L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }
}
