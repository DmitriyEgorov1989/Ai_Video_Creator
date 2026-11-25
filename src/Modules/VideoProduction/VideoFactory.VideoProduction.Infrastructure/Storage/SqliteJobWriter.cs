using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

internal static class SqliteJobWriter
{
    public static async Task<JobCreationStoreResult> CreateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobCreationRecord record,
        CancellationToken cancellationToken)
    {
        foreach (ArtifactId sourceId in record.Request.SourceAssetIds)
        {
            if (!await ArtifactExistsAsync(connection, transaction, sourceId, cancellationToken))
            {
                return new JobCreationStoreResult.Rejected(
                    new CreateJobError(CreateJobErrorCode.SourceArtifactNotFound));
            }
        }

        JobCreationRecord? existing = await FindExistingAsync(
            connection,
            transaction,
            record.Request.SourceAssetIds,
            cancellationToken);
        if (existing is not null)
        {
            return new JobCreationStoreResult.Existing(existing);
        }

        foreach (ArtifactId sourceId in record.Request.SourceAssetIds)
        {
            if (!await ArtifactIsUnownedAsync(connection, transaction, sourceId, cancellationToken))
            {
                return new JobCreationStoreResult.Rejected(
                    new CreateJobError(CreateJobErrorCode.SourceArtifactNotFound));
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await InsertJobAsync(connection, transaction, record, now, cancellationToken);
        await InsertVariantAsync(connection, transaction, record, now, cancellationToken);
        await InsertSourcesAsync(connection, transaction, record, now, cancellationToken);
        return new JobCreationStoreResult.Created(record);
    }

    private static async Task<bool> ArtifactExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactId artifactId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM artifacts a WHERE a.artifact_id = $artifactId
              AND NOT EXISTS (SELECT 1 FROM jobs j WHERE j.cleanup_started_at IS NOT NULL
                AND (j.job_id = a.job_id
                  OR replace(a.relative_path, '\', '/') LIKE 'data/jobs/' || j.job_id || '/%'
                  OR replace(a.relative_path, '\', '/') LIKE 'jobs/' || j.job_id || '/%')));
            """;
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<bool> ArtifactIsUnownedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactId artifactId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM artifacts WHERE artifact_id = $artifactId AND job_id IS NULL);";
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task<JobCreationRecord?> FindExistingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ArtifactId> sourceIds,
        CancellationToken cancellationToken)
    {
        List<JobId> candidates = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT job_id FROM job_sources
                WHERE source_order = 0 AND source_artifact_id = $sourceArtifactId;
                """;
            command.Parameters.AddWithValue("$sourceArtifactId", sourceIds[0].ToString());
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add(new JobId(Guid.Parse(reader.GetString(0))));
            }
        }

        foreach (JobId candidate in candidates)
        {
            IReadOnlyList<ArtifactId> storedSources = await ReadSourcesAsync(
                connection,
                transaction,
                candidate,
                cancellationToken);
            if (storedSources.SequenceEqual(sourceIds))
            {
                return await ReadByJobIdAsync(connection, transaction, candidate, cancellationToken);
            }
        }

        return null;
    }

    private static async Task InsertJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobCreationRecord record,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO jobs (
                job_id, source_artifact_id, comment, requested_mode,
                target_duration_seconds, captions_enabled, music_enabled,
                status, stage, effective_mode, pending_confirmation,
                current_approved_edit_plan_revision_id, telegram_user_id,
                edit_tempo, requested_style, created_at, updated_at)
            VALUES (
                $jobId, $sourceArtifactId, $comment, $requestedMode,
                $targetDurationSeconds, $captionsEnabled, $musicEnabled,
                $status, $stage, $effectiveMode, $pendingConfirmation,
                $currentApprovedRevisionId, $telegramUserId,
                $editTempo, $requestedStyle, $createdAt, $updatedAt);
            """;
        ProductionSettings settings = record.Request.Settings;
        JobProgress progress = record.Progress;
        command.Parameters.AddWithValue("$jobId", record.JobId.ToString());
        command.Parameters.AddWithValue("$sourceArtifactId", record.Request.PrimarySourceAssetId.ToString());
        command.Parameters.AddWithValue("$comment", record.Request.Comment);
        command.Parameters.AddWithValue("$requestedMode", (int)record.Request.RequestedMode);
        command.Parameters.AddWithValue("$targetDurationSeconds", settings.TargetDurationSeconds ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$captionsEnabled", settings.CaptionsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$musicEnabled", settings.MusicEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$status", (int)progress.Status);
        command.Parameters.AddWithValue("$stage", (int)progress.Stage);
        command.Parameters.AddWithValue("$effectiveMode", progress.EffectiveMode is null ? (object)DBNull.Value : (int)progress.EffectiveMode.Value);
        command.Parameters.AddWithValue("$pendingConfirmation", progress.PendingConfirmation is null ? (object)DBNull.Value : (int)progress.PendingConfirmation.Value);
        command.Parameters.AddWithValue("$currentApprovedRevisionId", record.CurrentApprovedEditPlanRevisionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$telegramUserId", record.TelegramUserId is long userId ? userId : (object)DBNull.Value);
        command.Parameters.AddWithValue("$editTempo", (int)settings.Tempo);
        command.Parameters.AddWithValue("$requestedStyle", (int)settings.RequestedStyle);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertVariantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobCreationRecord record,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO variants (
                variant_id, job_id, current_approved_edit_plan_revision_id, created_at, updated_at)
            VALUES ($variantId, $jobId, $currentApprovedRevisionId, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$variantId", record.VariantId.ToString());
        command.Parameters.AddWithValue("$jobId", record.JobId.ToString());
        command.Parameters.AddWithValue("$currentApprovedRevisionId", record.CurrentApprovedEditPlanRevisionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobCreationRecord record,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < record.Request.SourceAssetIds.Length; index++)
        {
            ArtifactId sourceId = record.Request.SourceAssetIds[index];
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO job_sources (job_id, source_artifact_id, source_order, created_at)
                VALUES ($jobId, $sourceArtifactId, $sourceOrder, $createdAt);
                UPDATE artifacts SET job_id = $jobId WHERE artifact_id = $sourceArtifactId;
                """;
            command.Parameters.AddWithValue("$jobId", record.JobId.ToString());
            command.Parameters.AddWithValue("$sourceArtifactId", sourceId.ToString());
            command.Parameters.AddWithValue("$sourceOrder", index);
            command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    internal static async Task<IReadOnlyList<ArtifactId>> ReadSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        JobId jobId,
        CancellationToken cancellationToken)
    {
        List<ArtifactId> sources = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_artifact_id FROM job_sources
            WHERE job_id = $jobId ORDER BY source_order;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(new ArtifactId(Guid.Parse(reader.GetString(0))));
        }

        return sources;
    }

    internal static async Task<JobCreationRecord?> ReadByJobIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        JobId jobId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT j.job_id, j.comment, j.requested_mode, j.target_duration_seconds,
                   j.captions_enabled, j.music_enabled, j.status, j.stage,
                   j.effective_mode, j.pending_confirmation,
                   j.current_approved_edit_plan_revision_id, j.telegram_user_id,
                   v.variant_id, j.edit_tempo, j.requested_style
            FROM jobs AS j
            INNER JOIN variants AS v ON v.job_id = j.job_id
            WHERE j.job_id = $jobId;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string comment = reader.GetString(1);
        ProductionMode requestedMode = (ProductionMode)reader.GetInt64(2);
        ProductionSettings settings = new(
            reader.IsDBNull(3) ? null : checked((int)reader.GetInt64(3)),
            reader.GetInt64(4) != 0,
            reader.GetInt64(5) != 0,
            (EditTempo)reader.GetInt64(13),
            (RequestedReelsStyle)reader.GetInt64(14));
        JobProgress progress = JobProgress.Restore(
            (JobStatus)reader.GetInt64(6),
            (ProductionStage)reader.GetInt64(7),
            requestedMode,
            reader.IsDBNull(8) ? null : (ProductionMode)reader.GetInt64(8),
            reader.IsDBNull(9) ? null : (ConfirmationKind)reader.GetInt64(9));
        RevisionId? revisionId = reader.IsDBNull(10)
            ? null
            : new RevisionId(Guid.Parse(reader.GetString(10)));
        long? telegramUserId = reader.IsDBNull(11) ? null : reader.GetInt64(11);
        VariantId variantId = new(Guid.Parse(reader.GetString(12)));
        await reader.DisposeAsync();

        IReadOnlyList<ArtifactId> sources = await ReadSourcesAsync(
            connection,
            transaction,
            jobId,
            cancellationToken);
        return new JobCreationRecord(
            jobId,
            variantId,
            new JobRequest(sources, comment, requestedMode, settings),
            progress,
            revisionId)
        {
            TelegramUserId = telegramUserId
        };
    }
}
