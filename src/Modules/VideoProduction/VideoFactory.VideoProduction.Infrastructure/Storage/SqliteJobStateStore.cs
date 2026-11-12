using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>
/// Восстанавливает Job и выполняет его переходы через условные SQLite-обновления.
/// </summary>
public sealed class SqliteJobStateStore : IJobStateStore
{
    private readonly string connectionString;

    /// <summary>
    /// Создаёт SQLite store состояния Job.
    /// </summary>
    /// <param name="connectionString">Строка подключения к базе данных.</param>
    public SqliteJobStateStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<JobCreationRecord?> GetAsync(
        JobId jobId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadByJobIdAsync(connection, transaction: null, jobId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JobStateTransitionResult> TryTransitionAsync(
        JobId jobId,
        JobStatus expectedStatus,
        ProductionStage expectedStage,
        JobProgress nextProgress,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE jobs
            SET status = $nextStatus,
                stage = $nextStage,
                effective_mode = $nextEffectiveMode,
                pending_confirmation = $nextPendingConfirmation,
                updated_at = $updatedAt
            WHERE job_id = $jobId
              AND status = $expectedStatus
              AND stage = $expectedStage;
            """;
        update.Parameters.AddWithValue("$nextStatus", (int)nextProgress.Status);
        update.Parameters.AddWithValue("$nextStage", (int)nextProgress.Stage);
        update.Parameters.AddWithValue(
            "$nextEffectiveMode",
            nextProgress.EffectiveMode is null
                ? (object)DBNull.Value
                : (int)nextProgress.EffectiveMode.Value);
        update.Parameters.AddWithValue(
            "$nextPendingConfirmation",
            nextProgress.PendingConfirmation is null
                ? (object)DBNull.Value
                : (int)nextProgress.PendingConfirmation.Value);
        update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$jobId", jobId.ToString());
        update.Parameters.AddWithValue("$expectedStatus", (int)expectedStatus);
        update.Parameters.AddWithValue("$expectedStage", (int)expectedStage);

        int affectedRows = await update.ExecuteNonQueryAsync(cancellationToken);
        JobCreationRecord? current = await ReadByJobIdAsync(
            connection,
            transaction,
            jobId,
            cancellationToken);

        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new JobStateTransitionResult.NotFound();
        }

        await transaction.CommitAsync(cancellationToken);
        return affectedRows == 1
            ? new JobStateTransitionResult.Applied(current with { Progress = nextProgress })
            : new JobStateTransitionResult.Conflict(current);
    }

    private static async Task<JobCreationRecord?> ReadByJobIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        JobId jobId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT j.job_id, j.source_artifact_id, j.comment, j.requested_mode,
                   j.target_duration_seconds, j.captions_enabled, j.music_enabled,
                   j.status, j.stage, j.effective_mode, j.pending_confirmation,
                   j.current_approved_edit_plan_revision_id, v.variant_id
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

        ProductionSettings settings = new(
            GetNullableInt32(reader, 4),
            reader.GetInt64(5) != 0,
            reader.GetInt64(6) != 0);
        JobProgress progress = JobProgress.Restore(
            (JobStatus)reader.GetInt64(7),
            (ProductionStage)reader.GetInt64(8),
            (ProductionMode)reader.GetInt64(3),
            GetNullableEnum<ProductionMode>(reader, 9),
            GetNullableEnum<ConfirmationKind>(reader, 10));

        return new JobCreationRecord(
            new JobId(Guid.Parse(reader.GetString(0))),
            new VariantId(Guid.Parse(reader.GetString(12))),
            new JobRequest(
                new ArtifactId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                (ProductionMode)reader.GetInt64(3),
                settings),
            progress,
            GetNullableRevisionId(reader, 11));
    }

    private static int? GetNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : checked((int)reader.GetInt64(ordinal));

    private static TEnum? GetNullableEnum<TEnum>(SqliteDataReader reader, int ordinal)
        where TEnum : struct, Enum =>
        reader.IsDBNull(ordinal) ? null : (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt64(ordinal));

    private static RevisionId? GetNullableRevisionId(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new RevisionId(Guid.Parse(reader.GetString(ordinal)));
}
