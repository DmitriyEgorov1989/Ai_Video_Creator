using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Variants;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteVariantChangeStoreTests
{
    [Fact]
    public async Task CompletedJobCreatesAlternativeAndReadsItAsActive()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        const long ownerId = 123;
        ArtifactId artifactId = ArtifactId.New();
        SqliteArtifactStore artifacts = new(database.ConnectionString);
        await artifacts.RegisterAsync(
            new ArtifactRecord(artifactId, "OriginalVideo", "intake/source.mp4", "abc", 3, "{}", null, "test"),
            CancellationToken.None);
        CreateJobResult.Succeeded created = Assert.IsType<CreateJobResult.Succeeded>(
            await new CreateJobUseCase(new SqliteJobCreationStore(database.ConnectionString)).ExecuteAsync(
                new CreateJobCommand(
                    artifactId,
                    "Исходный комментарий",
                    new ProductionSettings(null, captionsEnabled: true, musicEnabled: false),
                    ownerId),
                CancellationToken.None));
        RevisionId approvedRevisionId = RevisionId.New();
        await CompleteAsync(database.ConnectionString, created.JobId, created.VariantId, approvedRevisionId);

        RequestVariantChangeUseCase useCase = new(new SqliteVariantChangeStore(database.ConnectionString));
        RequestVariantChangeResult.Succeeded result = Assert.IsType<RequestVariantChangeResult.Succeeded>(
            await useCase.ExecuteAsync(
                new RequestVariantChangeCommand(
                    created.JobId,
                    created.VariantId,
                    ownerId,
                    "Сделай начало динамичнее",
                    "request-1"),
                CancellationToken.None));

        Assert.True(result.WasCreated);
        Assert.NotEqual(created.VariantId, result.VariantId);
        JobCreationRecord active = Assert.IsType<JobCreationRecord>(
            await new SqliteJobStateStore(database.ConnectionString)
                .GetAsync(created.JobId, CancellationToken.None));
        Assert.Equal(result.VariantId, active.VariantId);
        Assert.Equal("Сделай начало динамичнее", active.Request.Comment);
        Assert.Equal(JobStatus.Running, active.Progress.Status);
        Assert.Equal(ProductionStage.Planning, active.Progress.Stage);
        Assert.Null(active.CurrentApprovedEditPlanRevisionId);

        await using SqliteConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT variant_id, parent_variant_id, change_request,
                   current_approved_edit_plan_revision_id
            FROM variants WHERE job_id = $jobId ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$jobId", created.JobId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(created.VariantId.ToString(), reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(approvedRevisionId.ToString(), reader.GetString(3));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(result.VariantId.ToString(), reader.GetString(0));
        Assert.Equal(created.VariantId.ToString(), reader.GetString(1));
        Assert.Equal("Сделай начало динамичнее", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task RepeatedRequestKeyReturnsSameAlternativeWithoutThirdVariant()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        (JobId jobId, VariantId sourceVariantId) = await CreateCompletedJobAsync(database.ConnectionString, 123);
        RequestVariantChangeUseCase useCase = new(new SqliteVariantChangeStore(database.ConnectionString));
        RequestVariantChangeCommand command = new(
            jobId,
            sourceVariantId,
            123,
            "Сделай короче",
            "same-request");

        RequestVariantChangeResult.Succeeded first = Assert.IsType<RequestVariantChangeResult.Succeeded>(
            await useCase.ExecuteAsync(command, CancellationToken.None));
        RequestVariantChangeResult.Succeeded repeated = Assert.IsType<RequestVariantChangeResult.Succeeded>(
            await useCase.ExecuteAsync(command, CancellationToken.None));

        Assert.True(first.WasCreated);
        Assert.False(repeated.WasCreated);
        Assert.Equal(first.VariantId, repeated.VariantId);
        await using SqliteConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM variants WHERE job_id = $jobId;";
        count.Parameters.AddWithValue("$jobId", jobId.ToString());
        Assert.Equal(2L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task NonOwnerAndStaleRequestsDoNotCreateVariant()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        (JobId jobId, VariantId sourceVariantId) = await CreateCompletedJobAsync(database.ConnectionString, 123);
        RequestVariantChangeUseCase useCase = new(new SqliteVariantChangeStore(database.ConnectionString));

        RequestVariantChangeResult forbidden = await useCase.ExecuteAsync(
            new RequestVariantChangeCommand(jobId, sourceVariantId, 456, "Измени", "forbidden"),
            CancellationToken.None);
        RequestVariantChangeResult wrongSource = await useCase.ExecuteAsync(
            new RequestVariantChangeCommand(jobId, VariantId.New(), 123, "Измени", "wrong-source"),
            CancellationToken.None);
        await SetRunningAsync(database.ConnectionString, jobId);
        RequestVariantChangeResult wrongState = await useCase.ExecuteAsync(
            new RequestVariantChangeCommand(jobId, sourceVariantId, 123, "Измени", "wrong-state"),
            CancellationToken.None);

        Assert.IsType<RequestVariantChangeResult.Forbidden>(forbidden);
        Assert.IsType<RequestVariantChangeResult.Stale>(wrongSource);
        Assert.IsType<RequestVariantChangeResult.Stale>(wrongState);
        await using SqliteConnection connection = new(database.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM variants WHERE job_id = $jobId;";
        count.Parameters.AddWithValue("$jobId", jobId.ToString());
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task EmptyChangeRequestAndRequestKeyAreRejected()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        (JobId jobId, VariantId sourceVariantId) = await CreateCompletedJobAsync(database.ConnectionString, 123);
        RequestVariantChangeUseCase useCase = new(new SqliteVariantChangeStore(database.ConnectionString));

        RequestVariantChangeResult.Invalid emptyChange = Assert.IsType<RequestVariantChangeResult.Invalid>(
            await useCase.ExecuteAsync(
                new RequestVariantChangeCommand(jobId, sourceVariantId, 123, " ", "key"),
                CancellationToken.None));
        RequestVariantChangeResult.Invalid emptyKey = Assert.IsType<RequestVariantChangeResult.Invalid>(
            await useCase.ExecuteAsync(
                new RequestVariantChangeCommand(jobId, sourceVariantId, 123, "Измени", " "),
                CancellationToken.None));

        Assert.Equal(RequestVariantChangeErrorCode.EmptyChangeRequest, emptyChange.ErrorCode);
        Assert.Equal(RequestVariantChangeErrorCode.EmptyRequestKey, emptyKey.ErrorCode);
    }

    private static async Task<(JobId JobId, VariantId VariantId)> CreateCompletedJobAsync(
        string connectionString,
        long ownerId)
    {
        ArtifactId artifactId = ArtifactId.New();
        await new SqliteArtifactStore(connectionString).RegisterAsync(
            new ArtifactRecord(
                artifactId,
                "OriginalVideo",
                $"intake/{artifactId}.mp4",
                "abc",
                3,
                "{}",
                null,
                "test"),
            CancellationToken.None);
        CreateJobResult.Succeeded created = Assert.IsType<CreateJobResult.Succeeded>(
            await new CreateJobUseCase(new SqliteJobCreationStore(connectionString)).ExecuteAsync(
                new CreateJobCommand(
                    artifactId,
                    "Исходный комментарий",
                    new ProductionSettings(null, captionsEnabled: true, musicEnabled: false),
                    ownerId),
                CancellationToken.None));
        await CompleteAsync(connectionString, created.JobId, created.VariantId, RevisionId.New());
        return (created.JobId, created.VariantId);
    }

    private static async Task CompleteAsync(
        string connectionString,
        JobId jobId,
        VariantId variantId,
        RevisionId revisionId)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO edit_plan_revisions (
                revision_id, job_id, variant_id, analysis_revision_id, plan_hash,
                plan_json, actual_cost_usd, created_at)
            VALUES ($revisionId, $jobId, $variantId, 'analysis-1', 'hash-1', '{}', '0', $now);
            UPDATE variants
            SET current_approved_edit_plan_revision_id = $revisionId, updated_at = $now
            WHERE variant_id = $variantId;
            UPDATE jobs
            SET status = $completed, stage = $delivery,
                current_approved_edit_plan_revision_id = $revisionId,
                completed_at = $now, cleanup_started_at = $now,
                files_deleted_at = $now, heartbeat_at = $now, lease_owner = 'worker',
                updated_at = $now
            WHERE job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$revisionId", revisionId.ToString());
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        command.Parameters.AddWithValue("$delivery", (int)ProductionStage.Delivery);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetRunningAsync(string connectionString, JobId jobId)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE jobs SET status = $running WHERE job_id = $jobId;";
        command.Parameters.AddWithValue("$running", (int)JobStatus.Running);
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        await command.ExecuteNonQueryAsync();
    }
}
