using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Planning;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Сохраняет аудит платных попыток планирования в SQLite.</summary>
public sealed class SqlitePlanningAttemptStore : IPlanningAttemptStore
{
    private readonly string connectionString;

    /// <summary>Создаёт store попыток.</summary>
    public SqlitePlanningAttemptStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task SaveAsync(PlanningAttemptRecord attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO planning_attempts (
                attempt_id, job_id, variant_id, attempt_number, repair_attempt,
                estimated_cost_usd, actual_cost_usd, status, error_code, error_message, created_at)
            VALUES (
                $attemptId, $jobId, $variantId, $attemptNumber, $repairAttempt,
                $estimatedCostUsd, $actualCostUsd, $status, $errorCode, $errorMessage, $createdAt);
            """;
        command.Parameters.AddWithValue("$attemptId", attempt.AttemptId.ToString());
        command.Parameters.AddWithValue("$jobId", attempt.JobId.ToString());
        command.Parameters.AddWithValue("$variantId", attempt.VariantId.ToString());
        command.Parameters.AddWithValue("$attemptNumber", attempt.AttemptNumber);
        command.Parameters.AddWithValue("$repairAttempt", attempt.RepairAttempt);
        command.Parameters.AddWithValue("$estimatedCostUsd", attempt.EstimatedCostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actualCostUsd", attempt.ActualCostUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", attempt.Status);
        command.Parameters.AddWithValue("$errorCode", attempt.ErrorCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", attempt.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", attempt.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
