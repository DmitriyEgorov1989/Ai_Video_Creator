using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.Cancellation;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Сохраняет запросы пользовательской отмены в SQLite.</summary>
public sealed class SqliteJobCancellationStore : IJobCancellationStore
{
    private readonly string connectionString;
    private readonly SqliteJobStateStore stateStore;

    /// <summary>Создаёт store отмены.</summary>
    /// <param name="connectionString">Строка подключения к SQLite.</param>
    public SqliteJobCancellationStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
        stateStore = new SqliteJobStateStore(connectionString);
    }

    /// <inheritdoc />
    public async Task<JobCreationRecord?> FindActiveAsync(
        long telegramUserId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id
            FROM jobs
            WHERE telegram_user_id = $telegramUserId
              AND status = $running
              AND stage IN ($analysis, $rendering)
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$telegramUserId", telegramUserId);
        command.Parameters.AddWithValue("$running", (int)JobStatus.Running);
        command.Parameters.AddWithValue("$analysis", (int)ProductionStage.Analysis);
        command.Parameters.AddWithValue("$rendering", (int)ProductionStage.Rendering);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && Guid.TryParse(text, out Guid jobGuid)
            ? await stateStore.GetAsync(new JobId(jobGuid), cancellationToken)
            : null;
    }

    /// <inheritdoc />
    public async Task<bool> RequestAsync(
        JobCancellationRequest request,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO job_cancellation_requests (
                job_id, requested_by_user_id, reason, requested_at)
            VALUES ($jobId, $userId, $reason, $requestedAt)
            ON CONFLICT (job_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$jobId", request.JobId.ToString());
        command.Parameters.AddWithValue("$userId", request.RequestedByUserId);
        command.Parameters.AddWithValue("$reason", request.Reason);
        command.Parameters.AddWithValue("$requestedAt", request.RequestedAt.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> IsRequestedAsync(
        JobId jobId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM job_cancellation_requests WHERE job_id = $jobId);";
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
