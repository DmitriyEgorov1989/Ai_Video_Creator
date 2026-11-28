using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqlitePlanningAttemptStoreTests
{
    [Fact]
    public async Task StartedAttemptMakesActualCostUnknown()
    {
        string path = Path.Combine(Path.GetTempPath(), $"planning-unknown-cost-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={path};Pooling=False";
        try
        {
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            JobId jobId = JobId.New();
            VariantId variantId = VariantId.New();
            await InsertJobAsync(connectionString, jobId, variantId);
            SqlitePlanningAttemptStore store = new(connectionString);
            await store.SaveAsync(
                new PlanningAttemptRecord(
                    Guid.NewGuid(), jobId, variantId, 1, 0, 0.04m, null,
                    "started", null, null, DateTimeOffset.UtcNow),
                CancellationToken.None);

            PlanningCostLedger ledger = await store.GetLedgerAsync(jobId, variantId, CancellationToken.None);

            Assert.Equal(PlanningCostLedgerState.Unknown, ledger.State);
            Assert.Null(ledger.ActualCostUsd);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task ActualCostLedgerSumsRetriesAndRejectsCorruption()
    {
        string path = Path.Combine(Path.GetTempPath(), $"planning-cost-ledger-{Guid.NewGuid():N}.db");
        string connectionString = $"Data Source={path};Pooling=False";
        try
        {
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            SqlitePlanningAttemptStore store = new(connectionString);
            JobId jobId = JobId.New();
            VariantId variantId = VariantId.New();
            VariantId secondVariantId = VariantId.New();
            await using (SqliteConnection setup = new(connectionString))
            {
                await setup.OpenAsync();
                await using SqliteCommand setupCommand = setup.CreateCommand();
                setupCommand.CommandText = """
                    INSERT INTO artifacts (artifact_id, artifact_type, relative_path, created_at)
                    VALUES ('source', 'OriginalVideo', 'source.bin', '2000');
                    INSERT INTO jobs (job_id, source_artifact_id, comment, requested_mode, captions_enabled,
                        music_enabled, status, stage, active_variant_id, created_at, updated_at)
                    VALUES ($jobId, 'source', 'instruction', 0, 0, 0, 1, 2, $variantId, '2000', '2000');
                    INSERT INTO variants (variant_id, job_id, created_at, updated_at)
                    VALUES ($variantId, $jobId, '2000', '2000'),
                           ($secondVariantId, $jobId, '2000', '2000');
                    INSERT INTO edit_plan_revisions (
                        revision_id, job_id, variant_id, analysis_revision_id, plan_hash,
                        plan_json, actual_cost_usd, created_at)
                    VALUES ('legacy-revision', $jobId, $variantId, 'legacy-analysis',
                        'legacy-hash', '{}', '0.02', '1900-01-01T00:00:00.0000000+00:00');
                    """;
                setupCommand.Parameters.AddWithValue("$jobId", jobId.ToString());
                setupCommand.Parameters.AddWithValue("$variantId", variantId.ToString());
                setupCommand.Parameters.AddWithValue("$secondVariantId", secondVariantId.ToString());
                await setupCommand.ExecuteNonQueryAsync();
            }
            await store.SaveAsync(Attempt(jobId, variantId, 1, 0.04m), CancellationToken.None);
            await store.SaveAsync(Attempt(jobId, variantId, 2, 0.06m), CancellationToken.None);
            await store.SaveAsync(Attempt(jobId, secondVariantId, 1, 0.01m), CancellationToken.None);

            PlanningCostLedger known = await store.GetLedgerAsync(jobId, variantId, CancellationToken.None);
            Assert.Equal(PlanningCostLedgerState.Known, known.State);
            Assert.Equal(0.13m, known.ActualCostUsd);

            await using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE planning_attempts SET actual_cost_usd = 'invalid' WHERE attempt_number = 2;";
            await command.ExecuteNonQueryAsync();
            PlanningCostLedger invalid = await store.GetLedgerAsync(jobId, variantId, CancellationToken.None);
            Assert.Equal(PlanningCostLedgerState.Invalid, invalid.State);
            Assert.Null(invalid.ActualCostUsd);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm");
        }
    }

    private static PlanningAttemptRecord Attempt(JobId jobId, VariantId variantId, int number, decimal cost) =>
        new(Guid.NewGuid(), jobId, variantId, number, number - 1, cost, cost, "failed", "provider.failed", "failure", DateTimeOffset.UtcNow);

    private static async Task InsertJobAsync(string connectionString, JobId jobId, VariantId variantId)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifacts (artifact_id, artifact_type, relative_path, created_at)
            VALUES ('source', 'OriginalVideo', 'source.bin', '2000');
            INSERT INTO jobs (job_id, source_artifact_id, comment, requested_mode, captions_enabled,
                music_enabled, status, stage, active_variant_id, created_at, updated_at)
            VALUES ($jobId, 'source', 'instruction', 0, 0, 0, 1, 2, $variantId, '2000', '2000');
            INSERT INTO variants (variant_id, job_id, created_at, updated_at)
            VALUES ($variantId, $jobId, '2000', '2000');
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        await command.ExecuteNonQueryAsync();
    }
}
