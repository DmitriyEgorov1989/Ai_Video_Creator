using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Queue;

/// <summary>
/// Захватывает Job в SQLite и восстанавливает Job с устаревшим heartbeat.
/// </summary>
public sealed class SqliteJobWorkQueue : IJobWorkQueue
{
    private readonly string connectionString;
    private readonly SqliteJobStateStore stateStore;

    /// <summary>
    /// Создаёт SQLite queue.
    /// </summary>
    public SqliteJobWorkQueue(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
        stateStore = new SqliteJobStateStore(connectionString);
    }

    /// <inheritdoc />
    public async Task<JobCreationRecord?> TryClaimNextAsync(
        string workerId,
        TimeSpan leaseTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("Worker ID cannot be empty.", nameof(workerId));
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset staleBefore = now - leaseTimeout;

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using SqliteCommand select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText =
            """
            SELECT job_id
            FROM jobs
            WHERE status = $queuedStatus
             OR (status = $runningStatus AND
                   (lease_owner = $workerId OR heartbeat_at IS NULL OR heartbeat_at < $staleBefore))
            ORDER BY created_at;
            """;
        select.Parameters.AddWithValue("$queuedStatus", (int)JobStatus.Queued);
        select.Parameters.AddWithValue("$runningStatus", (int)JobStatus.Running);
        select.Parameters.AddWithValue("$workerId", workerId);
        select.Parameters.AddWithValue("$staleBefore", staleBefore.ToString("O"));

        string? jobId = null;
        await using (SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                jobId = reader.GetString(0);
            }
        }

        if (jobId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE jobs
            SET status = CASE WHEN status = $queuedStatus
                              THEN $runningStatus ELSE status END,
                lease_owner = $workerId,
                heartbeat_at = $heartbeatAt,
                updated_at = $updatedAt
            WHERE job_id = $jobId
              AND (
                  status = $queuedStatus
                   OR (status = $runningStatus AND
                       (lease_owner = $workerId OR heartbeat_at IS NULL OR heartbeat_at < $staleBefore))
              );
            """;
        update.Parameters.AddWithValue("$queuedStatus", (int)JobStatus.Queued);
        update.Parameters.AddWithValue("$runningStatus", (int)JobStatus.Running);
        update.Parameters.AddWithValue("$workerId", workerId);
        update.Parameters.AddWithValue("$heartbeatAt", now.ToString("O"));
        update.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        update.Parameters.AddWithValue("$jobId", jobId);
        update.Parameters.AddWithValue("$staleBefore", staleBefore.ToString("O"));

        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return await stateStore.GetAsync(
            new JobId(Guid.Parse(jobId)),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TryHeartbeatAsync(
        JobId jobId,
        string workerId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE jobs
            SET heartbeat_at = $heartbeatAt,
                updated_at = $updatedAt
            WHERE job_id = $jobId
              AND status = $runningStatus
              AND lease_owner = $workerId;
            """;
        string now = DateTimeOffset.UtcNow.ToString("O");
        command.Parameters.AddWithValue("$heartbeatAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$runningStatus", (int)JobStatus.Running);
        command.Parameters.AddWithValue("$workerId", workerId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
