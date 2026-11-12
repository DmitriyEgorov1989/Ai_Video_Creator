using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>
/// Сохраняет задания производства в SQLite.
/// </summary>
public sealed class SqliteJobCreationStore : IJobCreationStore
{
    private readonly string connectionString;

    /// <summary>
    /// Создаёт SQLite store для указанной базы данных.
    /// </summary>
    /// <param name="connectionString">Строка подключения к базе данных.</param>
    public SqliteJobCreationStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<JobCreationStoreResult> CreateAsync(
        JobCreationRecord record,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (!await ArtifactExistsAsync(
                connection,
                transaction,
                record.Request.SourceAssetId.ToString(),
                cancellationToken))
        {
            return new JobCreationStoreResult.Rejected(
                new CreateJobError(CreateJobErrorCode.SourceArtifactNotFound));
        }

        await using SqliteCommand insertJob = connection.CreateCommand();
        insertJob.Transaction = transaction;
        insertJob.CommandText =
            """
            INSERT INTO jobs (
                job_id, source_artifact_id, comment, requested_mode,
                target_duration_seconds, captions_enabled, music_enabled,
                status, stage, effective_mode, pending_confirmation,
                current_approved_edit_plan_revision_id, created_at, updated_at)
            VALUES (
                $jobId, $sourceArtifactId, $comment, $requestedMode,
                $targetDurationSeconds, $captionsEnabled, $musicEnabled,
                $status, $stage, $effectiveMode, $pendingConfirmation,
                $currentApprovedRevisionId, $createdAt, $updatedAt)
            ON CONFLICT (source_artifact_id) DO NOTHING;
            """;
        AddJobParameters(insertJob, record);
        int inserted = await insertJob.ExecuteNonQueryAsync(cancellationToken);

        if (inserted == 0)
        {
            JobCreationRecord existing = await ReadByArtifactAsync(
                connection,
                transaction,
                record.Request.SourceAssetId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new JobCreationStoreResult.Existing(existing);
        }

        await using SqliteCommand insertVariant = connection.CreateCommand();
        insertVariant.Transaction = transaction;
        insertVariant.CommandText =
            """
            INSERT INTO variants (
                variant_id, job_id, current_approved_edit_plan_revision_id, created_at, updated_at)
            VALUES ($variantId, $jobId, $currentApprovedRevisionId, $createdAt, $updatedAt);
            """;
        insertVariant.Parameters.AddWithValue("$variantId", record.VariantId.ToString());
        insertVariant.Parameters.AddWithValue("$jobId", record.JobId.ToString());
        insertVariant.Parameters.AddWithValue(
            "$currentApprovedRevisionId",
            record.CurrentApprovedEditPlanRevisionId?.ToString() ?? (object)DBNull.Value);
        insertVariant.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        insertVariant.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await insertVariant.ExecuteNonQueryAsync(cancellationToken);

        await using SqliteCommand attachArtifact = connection.CreateCommand();
        attachArtifact.Transaction = transaction;
        attachArtifact.CommandText =
            "UPDATE artifacts SET job_id = $jobId WHERE artifact_id = $artifactId;";
        attachArtifact.Parameters.AddWithValue("$jobId", record.JobId.ToString());
        attachArtifact.Parameters.AddWithValue("$artifactId", record.Request.SourceAssetId.ToString());
        await attachArtifact.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new JobCreationStoreResult.Created(record);
    }

    private static async Task<bool> ArtifactExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string artifactId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM artifacts WHERE artifact_id = $artifactId);";
        command.Parameters.AddWithValue("$artifactId", artifactId);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value) == 1;
    }

    private static void AddJobParameters(SqliteCommand command, JobCreationRecord record)
    {
        ProductionSettings settings = record.Request.Settings;
        JobProgress progress = record.Progress;
        command.Parameters.AddWithValue("$jobId", record.JobId.ToString());
        command.Parameters.AddWithValue("$sourceArtifactId", record.Request.SourceAssetId.ToString());
        command.Parameters.AddWithValue("$comment", record.Request.Comment);
        command.Parameters.AddWithValue("$requestedMode", (int)record.Request.RequestedMode);
        command.Parameters.AddWithValue(
            "$targetDurationSeconds",
            settings.TargetDurationSeconds ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$captionsEnabled", settings.CaptionsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$musicEnabled", settings.MusicEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$status", (int)progress.Status);
        command.Parameters.AddWithValue("$stage", (int)progress.Stage);
        command.Parameters.AddWithValue(
            "$effectiveMode",
            progress.EffectiveMode is null ? (object)DBNull.Value : (int)progress.EffectiveMode.Value);
        command.Parameters.AddWithValue(
            "$pendingConfirmation",
            progress.PendingConfirmation is null
                ? (object)DBNull.Value
                : (int)progress.PendingConfirmation.Value);
        command.Parameters.AddWithValue(
            "$currentApprovedRevisionId",
            record.CurrentApprovedEditPlanRevisionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
    }

    private static async Task<JobCreationRecord> ReadByArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactId artifactId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT j.job_id, j.comment, j.requested_mode, j.target_duration_seconds,
                   j.captions_enabled, j.music_enabled, j.status, j.stage,
                   j.effective_mode, j.pending_confirmation,
                   j.current_approved_edit_plan_revision_id, v.variant_id
            FROM jobs AS j
            INNER JOIN variants AS v ON v.job_id = j.job_id
            WHERE j.source_artifact_id = $artifactId;
            """;
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("SQLite Job was not found after a conflict.");
        }

        ProductionSettings settings = new(
            GetNullableInt32(reader, 3),
            reader.GetInt64(4) != 0,
            reader.GetInt64(5) != 0);
        JobProgress progress = JobProgress.Restore(
            (JobStatus)reader.GetInt64(6),
            (ProductionStage)reader.GetInt64(7),
            (ProductionMode)reader.GetInt64(2),
            GetNullableEnum<ProductionMode>(reader, 8),
            GetNullableEnum<ConfirmationKind>(reader, 9));

        return new JobCreationRecord(
            new JobId(Guid.Parse(reader.GetString(0))),
            new VariantId(Guid.Parse(reader.GetString(11))),
            new JobRequest(artifactId, reader.GetString(1), ProductionMode.Fast, settings),
            progress,
            GetNullableRevisionId(reader, 10));
    }

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : checked((int)reader.GetInt64(ordinal));

    private static TEnum? GetNullableEnum<TEnum>(SqliteDataReader reader, int ordinal)
        where TEnum : struct, Enum =>
        reader.IsDBNull(ordinal) ? null : (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt64(ordinal));

    private static RevisionId? GetNullableRevisionId(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new RevisionId(Guid.Parse(reader.GetString(ordinal)));
}
