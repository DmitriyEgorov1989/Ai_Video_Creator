using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Сохраняет подтверждения стоимости планирования в SQLite.</summary>
public sealed class SqlitePlanningApprovalStore : IPlanningApprovalStore
{
    private readonly string connectionString;

    /// <summary>Создаёт store подтверждений.</summary>
    public SqlitePlanningApprovalStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<PlanningApproval?> FindAsync(
        JobId jobId,
        VariantId variantId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT approval_id, telegram_user_id, version, estimated_cost_usd,
                   accumulated_actual_cost_usd, limit_usd, status, created_at, decided_at
            FROM planning_approvals
            WHERE job_id = $jobId AND variant_id = $variantId
            ORDER BY version DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader, jobId, variantId) : null;
    }

    /// <inheritdoc />
    public async Task<PlanningApproval> SavePendingAsync(
        JobId jobId,
        VariantId variantId,
        long telegramUserId,
        decimal estimatedCostUsd,
        decimal accumulatedActualCostUsd,
        decimal limitUsd,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        PlanningApproval? current = await FindInTransactionAsync(connection, transaction, jobId, variantId, cancellationToken);
        if (current is { Status: PlanningApprovalStatus.Pending })
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }

        int version = (current?.Version ?? 0) + 1;
        PlanningApproval approval = new(
            Guid.NewGuid(), jobId, variantId, telegramUserId, version,
            estimatedCostUsd, accumulatedActualCostUsd, limitUsd,
            PlanningApprovalStatus.Pending, DateTimeOffset.UtcNow, null);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO planning_approvals
              (approval_id, job_id, variant_id, telegram_user_id, version,
               estimated_cost_usd, accumulated_actual_cost_usd, limit_usd,
               status, created_at, decided_at)
            VALUES ($approvalId, $jobId, $variantId, $userId, $version,
                    $estimated, $accumulated, $limit, 'Pending', $createdAt, NULL);
            """;
        AddParameters(command, approval);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return approval;
    }

    /// <inheritdoc />
    public async Task<PlanningApprovalResult> ResolveAsync(
        JobId jobId,
        VariantId variantId,
        long telegramUserId,
        int version,
        PlanningApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        PlanningApproval? current = await FindInTransactionAsync(connection, transaction, jobId, variantId, cancellationToken);
        if (current is null || current.Version != version)
        {
            return new PlanningApprovalResult.Stale();
        }

        if (current.TelegramUserId != telegramUserId)
        {
            return new PlanningApprovalResult.Forbidden();
        }

        if (current.Status != PlanningApprovalStatus.Pending)
        {
            return new PlanningApprovalResult.AlreadyResolved(current);
        }

        DateTimeOffset decidedAt = DateTimeOffset.UtcNow;
        string status = decision == PlanningApprovalDecision.Approve ? "Approved" : "Rejected";
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE planning_approvals
            SET status = $status, decided_at = $decidedAt
            WHERE job_id = $jobId AND variant_id = $variantId
              AND version = $version AND status = 'Pending';
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$decidedAt", decidedAt.ToString("O"));
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$version", version);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            return new PlanningApprovalResult.Stale();
        }

        PlanningApproval resolved = current with
        {
            Status = decision == PlanningApprovalDecision.Approve
                ? PlanningApprovalStatus.Approved
                : PlanningApprovalStatus.Rejected,
            DecidedAt = decidedAt
        };
        await transaction.CommitAsync(cancellationToken);
        return new PlanningApprovalResult.Applied(resolved);
    }

    private static void AddParameters(SqliteCommand command, PlanningApproval approval)
    {
        command.Parameters.AddWithValue("$approvalId", approval.ApprovalId.ToString());
        command.Parameters.AddWithValue("$jobId", approval.JobId.ToString());
        command.Parameters.AddWithValue("$variantId", approval.VariantId.ToString());
        command.Parameters.AddWithValue("$userId", approval.TelegramUserId);
        command.Parameters.AddWithValue("$version", approval.Version);
        command.Parameters.AddWithValue("$estimated", approval.EstimatedCostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$accumulated", approval.AccumulatedActualCostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", approval.LimitUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$createdAt", approval.CreatedAt.ToString("O"));
    }

    private static async Task<PlanningApproval?> FindInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        VariantId variantId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT approval_id, telegram_user_id, version, estimated_cost_usd,
                   accumulated_actual_cost_usd, limit_usd, status, created_at, decided_at
            FROM planning_approvals
            WHERE job_id = $jobId AND variant_id = $variantId
            ORDER BY version DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader, jobId, variantId) : null;
    }

    private static PlanningApproval Read(SqliteDataReader reader, JobId jobId, VariantId variantId) =>
        new(
            Guid.Parse(reader.GetString(0)),
            jobId,
            variantId,
            reader.GetInt64(1),
            reader.GetInt32(2),
            decimal.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
            Enum.Parse<PlanningApprovalStatus>(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture));
}
