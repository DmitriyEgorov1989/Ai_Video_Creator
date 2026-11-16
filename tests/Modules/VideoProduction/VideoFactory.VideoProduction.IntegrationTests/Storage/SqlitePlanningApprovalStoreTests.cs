using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqlitePlanningApprovalStoreTests
{
    [Fact]
    public async Task ApprovalIsPersistentAndOnlyOwnerCanResolveCurrentVersion()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"approval-{Guid.NewGuid():N}.sqlite");
        string connectionString = $"Data Source={databasePath};Pooling=False";
        JobId jobId = JobId.New();
        VariantId variantId = VariantId.New();
        try
        {
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            await using (SqliteConnection connection = new(connectionString))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO jobs (job_id, source_artifact_id, comment, requested_mode,
                      captions_enabled, music_enabled, status, stage, created_at, updated_at)
                    VALUES ($job, $artifact, 'comment', 0, 1, 0, 1, 3, $now, $now);
                    INSERT INTO variants (variant_id, job_id, created_at, updated_at)
                    VALUES ($variant, $job, $now, $now);
                    """;
                command.Parameters.AddWithValue("$job", jobId.ToString());
                command.Parameters.AddWithValue("$variant", variantId.ToString());
                command.Parameters.AddWithValue("$artifact", ArtifactId.New().ToString());
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }

            SqlitePlanningApprovalStore store = new(connectionString);
            PlanningApproval pending = await store.SavePendingAsync(
                jobId, variantId, 123, 0.08m, 0m, 0.05m, CancellationToken.None);
            Assert.Equal(PlanningApprovalStatus.Pending,
                (await store.FindAsync(jobId, variantId, CancellationToken.None))!.Status);

            Assert.IsType<PlanningApprovalResult.Forbidden>(await store.ResolveAsync(
                jobId, variantId, 456, pending.Version, PlanningApprovalDecision.Approve, CancellationToken.None));

            PlanningApprovalResult.Applied applied = Assert.IsType<PlanningApprovalResult.Applied>(
                await store.ResolveAsync(
                    jobId, variantId, 123, pending.Version, PlanningApprovalDecision.Approve, CancellationToken.None));
            Assert.Equal(PlanningApprovalStatus.Approved, applied.Approval.Status);

            Assert.IsType<PlanningApprovalResult.AlreadyResolved>(await store.ResolveAsync(
                jobId, variantId, 123, pending.Version, PlanningApprovalDecision.Approve, CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}
