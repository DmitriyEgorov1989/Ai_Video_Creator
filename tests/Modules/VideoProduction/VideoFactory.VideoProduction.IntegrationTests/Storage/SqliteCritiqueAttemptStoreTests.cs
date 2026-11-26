using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteCritiqueAttemptStoreTests
{
    [Fact]
    public async Task SavingSameAttemptTwiceCreatesOneRestorableRecord()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        (JobId jobId, VariantId variantId, RevisionId sourceRevisionId, ArtifactId renderArtifactId) =
            await SeedAttemptReferencesAsync(database.ConnectionString);
        SqliteCritiqueAttemptStore store = new(database.ConnectionString);
        CritiqueAttemptRecord attempt = new(
            jobId,
            variantId,
            sourceRevisionId,
            renderArtifactId,
            null,
            0,
            0.04m,
            null,
            "Started",
            null,
            null,
            new DateTimeOffset(2026, 11, 26, 10, 0, 0, TimeSpan.Zero),
            null);

        await store.SaveAsync(attempt, CancellationToken.None);
        await store.SaveAsync(attempt, CancellationToken.None);

        CritiqueAttemptRecord? restored = await store.GetAsync(
            variantId,
            sourceRevisionId,
            renderArtifactId,
            CancellationToken.None);
        Assert.Equal(attempt, restored);
        CritiqueAttemptRecord listed = Assert.Single(
            await store.ListAsync(jobId, variantId, CancellationToken.None));
        Assert.Equal(attempt, listed);

        await using SqliteConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM critique_attempts;";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task CompletingAttemptPersistsActualCost()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        (JobId jobId, VariantId variantId, RevisionId sourceRevisionId, ArtifactId renderArtifactId) =
            await SeedAttemptReferencesAsync(database.ConnectionString);
        SqliteCritiqueAttemptStore store = new(database.ConnectionString);
        DateTimeOffset createdAt = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

        await store.SaveAsync(
            new CritiqueAttemptRecord(
                jobId, variantId, sourceRevisionId, renderArtifactId, null, 1,
                0.04m, null, "Started", null, null, createdAt, null),
            CancellationToken.None);
        await store.SaveAsync(
            new CritiqueAttemptRecord(
                jobId, variantId, sourceRevisionId, renderArtifactId, null, 1,
                0.04m, 0.031m, "Completed", null, null, createdAt, createdAt.AddMinutes(1)),
            CancellationToken.None);

        CritiqueAttemptRecord? restored = await store.GetAsync(
            variantId,
            sourceRevisionId,
            renderArtifactId,
            CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal(0.031m, restored.ActualCostUsd);
        Assert.Equal("Completed", restored.Status);
        Assert.Equal(createdAt.AddMinutes(1), restored.CompletedAt);
    }

    private static async Task<(JobId JobId, VariantId VariantId, RevisionId SourceRevisionId, ArtifactId RenderArtifactId)>
        SeedAttemptReferencesAsync(string connectionString)
    {
        JobId jobId = JobId.New();
        VariantId variantId = VariantId.New();
        RevisionId sourceRevisionId = RevisionId.New();
        ArtifactId sourceArtifactId = ArtifactId.New();
        ArtifactId renderArtifactId = ArtifactId.New();

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO artifacts (artifact_id, artifact_type, relative_path, created_at)
            VALUES ($sourceArtifactId, 'OriginalVideo', 'source/original.mp4', $createdAt);
            INSERT INTO jobs (
                job_id, source_artifact_id, comment, requested_mode, captions_enabled,
                music_enabled, status, stage, created_at, updated_at)
            VALUES ($jobId, $sourceArtifactId, 'test', 0, 0, 0, 0, 0, $createdAt, $createdAt);
            INSERT INTO variants (variant_id, job_id, created_at, updated_at)
            VALUES ($variantId, $jobId, $createdAt, $createdAt);
            INSERT INTO edit_plan_revisions (
                revision_id, job_id, variant_id, analysis_revision_id, plan_hash,
                plan_json, actual_cost_usd, created_at)
            VALUES ($sourceRevisionId, $jobId, $variantId, $analysisRevisionId,
                'source-plan', '{}', '0', $createdAt);
            INSERT INTO artifacts (
                artifact_id, job_id, artifact_type, relative_path, created_at)
            VALUES ($renderArtifactId, $jobId, 'RenderedVideo', 'renders/output.mp4', $createdAt);
            """;
        command.Parameters.AddWithValue("$sourceArtifactId", sourceArtifactId.ToString());
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$sourceRevisionId", sourceRevisionId.ToString());
        command.Parameters.AddWithValue("$analysisRevisionId", RevisionId.New().ToString());
        command.Parameters.AddWithValue("$renderArtifactId", renderArtifactId.ToString());
        command.Parameters.AddWithValue("$createdAt", "2026-11-26T10:00:00.0000000+00:00");
        await command.ExecuteNonQueryAsync();

        return (jobId, variantId, sourceRevisionId, renderArtifactId);
    }
}
