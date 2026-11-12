using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>
/// Сохраняет попытки этапов и исходную диагностику в SQLite.
/// </summary>
public sealed class SqliteStageAttemptStore : IStageAttemptStore
{
    private readonly string connectionString;

    /// <summary>
    /// Создаёт SQLite store попыток этапов.
    /// </summary>
    /// <param name="connectionString">Строка подключения к базе данных.</param>
    public SqliteStageAttemptStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task AppendAsync(
        StageAttemptRecord attempt,
        CancellationToken cancellationToken)
    {
        if (attempt.AttemptId == Guid.Empty)
        {
            throw new ArgumentException("Attempt ID cannot be empty.", nameof(attempt));
        }

        if (attempt.JobId.IsEmpty)
        {
            throw new ArgumentException("Job ID cannot be empty.", nameof(attempt));
        }

        if (attempt.AttemptNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt number must be positive.");
        }

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO stage_attempts (
                attempt_id, job_id, variant_id, stage, attempt_number, status,
                error_code, error_message, started_at, completed_at)
            VALUES (
                $attemptId, $jobId, $variantId, $stage, $attemptNumber, $status,
                $errorCode, $errorMessage, $startedAt, $completedAt);
            """;
        command.Parameters.AddWithValue("$attemptId", attempt.AttemptId.ToString("N"));
        command.Parameters.AddWithValue("$jobId", attempt.JobId.ToString());
        command.Parameters.AddWithValue(
            "$variantId",
            attempt.VariantId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$stage", (int)attempt.Stage);
        command.Parameters.AddWithValue("$attemptNumber", attempt.AttemptNumber);
        command.Parameters.AddWithValue("$status", attempt.Status.ToString());
        command.Parameters.AddWithValue("$errorCode", (object?)attempt.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)attempt.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$startedAt", attempt.StartedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$completedAt",
            attempt.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StageAttemptRecord?> GetLatestAsync(
        JobId jobId,
        ProductionStage stage,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT attempt_id, variant_id, attempt_number, status,
                   error_code, error_message, started_at, completed_at
            FROM stage_attempts
            WHERE job_id = $jobId AND stage = $stage
            ORDER BY attempt_number DESC, started_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$stage", (int)stage);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StageAttemptRecord(
            Guid.Parse(reader.GetString(0)),
            jobId,
            reader.IsDBNull(1) ? null : new VariantId(Guid.Parse(reader.GetString(1))),
            stage,
            reader.GetInt32(2),
            Enum.Parse<StageAttemptStatus>(reader.GetString(3), ignoreCase: false),
            GetNullableString(reader, 4),
            GetNullableString(reader, 5),
            DateTimeOffset.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)));
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
