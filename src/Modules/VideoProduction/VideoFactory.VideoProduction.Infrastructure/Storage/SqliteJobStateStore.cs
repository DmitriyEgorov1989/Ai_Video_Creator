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
                updated_at = $updatedAt,
                completed_at = CASE WHEN $nextStatus = $completedStatus THEN COALESCE(completed_at, $updatedAt) ELSE completed_at END,
                lease_owner = CASE WHEN $nextStatus = $completedStatus THEN NULL ELSE lease_owner END
            WHERE job_id = $jobId
              AND status = $expectedStatus
              AND stage = $expectedStage
              AND cleanup_started_at IS NULL;
            """;
        update.Parameters.AddWithValue("$nextStatus", (int)nextProgress.Status);
        update.Parameters.AddWithValue("$completedStatus", (int)JobStatus.Completed);
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
        CancellationToken cancellationToken) =>
        await SqliteJobWriter.ReadByJobIdAsync(connection, transaction, jobId, cancellationToken);
}
