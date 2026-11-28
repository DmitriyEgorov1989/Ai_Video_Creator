using Microsoft.Data.Sqlite;
using System.Globalization;
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
    public async Task<PlanningCostLedger> GetLedgerAsync(
        VideoFactory.VideoProduction.Domain.Identifiers.JobId jobId,
        VideoFactory.VideoProduction.Domain.Identifiers.VariantId variantId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT actual_cost_usd, NULL, status, repair_attempt, variant_id = $variantId
            FROM planning_attempts WHERE job_id = $jobId
            UNION ALL
            SELECT actual_cost_usd, variant_id, 'legacy', -1, 0 FROM edit_plan_revisions
            WHERE job_id = $jobId
              AND created_at < (SELECT applied_at FROM schema_migrations WHERE version = 6);
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        decimal total = 0m;
        int nextRepairAttempt = 0;
        bool unknown = false;
        Dictionary<string, decimal> legacyByVariant = new(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            string status = reader.GetString(2);
            if (status is not ("started" or "invalid" or "succeeded" or "existing" or "failed" or "legacy"))
                return new(PlanningCostLedgerState.Invalid, null, nextRepairAttempt);
            if (reader.IsDBNull(0))
            {
                unknown = true;
                continue;
            }
            if (!decimal.TryParse(reader.GetString(0), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal cost)
                || cost < 0m)
                return new(PlanningCostLedgerState.Invalid, null, nextRepairAttempt);
            if (reader.IsDBNull(1))
            {
                total += cost;
                if (reader.GetInt64(4) == 1)
                    nextRepairAttempt = Math.Max(nextRepairAttempt, reader.GetInt32(3) + 1);
            }
            else
            {
                string legacyVariantId = reader.GetString(1);
                if (!legacyByVariant.TryGetValue(legacyVariantId, out decimal current) || cost > current)
                    legacyByVariant[legacyVariantId] = cost;
            }
        }
        return unknown
            ? new(PlanningCostLedgerState.Unknown, null, nextRepairAttempt)
            : new(PlanningCostLedgerState.Known, total + legacyByVariant.Values.Sum(), nextRepairAttempt);
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
                estimated_cost_usd, actual_cost_usd, status, error_code, error_message,
                created_at, attempt_key, completed_at)
            VALUES (
                $attemptId, $jobId, $variantId, $attemptNumber, $repairAttempt,
                $estimatedCostUsd, $actualCostUsd, $status, $errorCode, $errorMessage,
                $createdAt, $attemptKey, $completedAt)
            ON CONFLICT(attempt_id) DO UPDATE SET
                actual_cost_usd = excluded.actual_cost_usd,
                status = excluded.status,
                error_code = excluded.error_code,
                error_message = excluded.error_message,
                completed_at = excluded.completed_at;
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
        command.Parameters.AddWithValue("$attemptKey", $"{attempt.JobId}:{attempt.VariantId}:{attempt.RepairAttempt}");
        command.Parameters.AddWithValue("$completedAt", attempt.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
