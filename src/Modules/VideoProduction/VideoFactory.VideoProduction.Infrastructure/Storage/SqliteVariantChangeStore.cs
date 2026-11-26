using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Variants;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Атомарно создаёт альтернативные варианты в SQLite.</summary>
public sealed class SqliteVariantChangeStore : IVariantChangeStore
{
    private readonly string connectionString;

    /// <summary>Создаёт хранилище запросов альтернативного варианта.</summary>
    /// <param name="connectionString">Непустая строка подключения SQLite.</param>
    public SqliteVariantChangeStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<RequestVariantChangeResult> RequestAsync(
        RequestVariantChangeCommand command,
        VariantId newVariantId,
        CancellationToken cancellationToken)
    {
        RequestVariantChangeResult.Invalid? invalid = Validate(command);
        if (invalid is not null)
        {
            return invalid;
        }

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        JobState? job = await ReadJobAsync(connection, transaction, command, cancellationToken);
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new RequestVariantChangeResult.Stale();
        }

        if (job.TelegramUserId != command.TelegramUserId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new RequestVariantChangeResult.Forbidden();
        }

        if (!job.SourceBelongsToJob)
        {
            await transaction.CommitAsync(cancellationToken);
            return new RequestVariantChangeResult.Stale();
        }

        VariantId? existing = await FindExistingAsync(
            connection, transaction, command.JobId, command.RequestKey, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new RequestVariantChangeResult.Succeeded(existing.Value, WasCreated: false);
        }

        if (job.Status != JobStatus.Completed || job.ActiveVariantId != command.SourceVariantId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new RequestVariantChangeResult.Stale();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await InsertVariantAsync(connection, transaction, command, newVariantId, now, cancellationToken);
        int updated = await ActivateAsync(connection, transaction, command, newVariantId, now, cancellationToken);
        if (updated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new RequestVariantChangeResult.Stale();
        }

        await transaction.CommitAsync(cancellationToken);
        return new RequestVariantChangeResult.Succeeded(newVariantId, WasCreated: true);
    }

    private static RequestVariantChangeResult.Invalid? Validate(RequestVariantChangeCommand command)
    {
        if (command.TelegramUserId <= 0)
        {
            return new(RequestVariantChangeErrorCode.InvalidTelegramUserId);
        }

        if (string.IsNullOrWhiteSpace(command.ChangeRequest))
        {
            return new(RequestVariantChangeErrorCode.EmptyChangeRequest);
        }

        return string.IsNullOrWhiteSpace(command.RequestKey)
            ? new(RequestVariantChangeErrorCode.EmptyRequestKey)
            : null;
    }

    private static async Task<JobState?> ReadJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RequestVariantChangeCommand command,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT j.telegram_user_id, j.status, j.active_variant_id,
                   EXISTS (SELECT 1 FROM variants v
                           WHERE v.variant_id = $sourceVariantId AND v.job_id = j.job_id)
            FROM jobs j WHERE j.job_id = $jobId;
            """;
        query.Parameters.AddWithValue("$jobId", command.JobId.ToString());
        query.Parameters.AddWithValue("$sourceVariantId", command.SourceVariantId.ToString());
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new JobState(
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            (JobStatus)reader.GetInt64(1),
            reader.IsDBNull(2) ? null : new VariantId(Guid.Parse(reader.GetString(2))),
            reader.GetInt64(3) == 1);
    }

    private static async Task<VariantId?> FindExistingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobId jobId,
        string requestKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT variant_id FROM variants WHERE job_id = $jobId AND request_key = $requestKey;";
        query.Parameters.AddWithValue("$jobId", jobId.ToString());
        query.Parameters.AddWithValue("$requestKey", requestKey);
        object? value = await query.ExecuteScalarAsync(cancellationToken);
        return value is string id ? new VariantId(Guid.Parse(id)) : null;
    }

    private static async Task InsertVariantAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RequestVariantChangeCommand command,
        VariantId newVariantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO variants (
                variant_id, job_id, parent_variant_id, change_request, request_key,
                current_approved_edit_plan_revision_id, created_at, updated_at)
            VALUES ($variantId, $jobId, $parentVariantId, $changeRequest, $requestKey,
                    NULL, $now, $now);
            """;
        insert.Parameters.AddWithValue("$variantId", newVariantId.ToString());
        insert.Parameters.AddWithValue("$jobId", command.JobId.ToString());
        insert.Parameters.AddWithValue("$parentVariantId", command.SourceVariantId.ToString());
        insert.Parameters.AddWithValue("$changeRequest", command.ChangeRequest);
        insert.Parameters.AddWithValue("$requestKey", command.RequestKey);
        insert.Parameters.AddWithValue("$now", now.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ActivateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RequestVariantChangeCommand command,
        VariantId newVariantId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE jobs
            SET active_variant_id = $newVariantId,
                current_approved_edit_plan_revision_id = NULL,
                status = $running,
                stage = $planning,
                pending_confirmation = NULL,
                completed_at = NULL,
                cleanup_started_at = NULL,
                files_deleted_at = NULL,
                heartbeat_at = NULL,
                lease_owner = NULL,
                updated_at = $now
            WHERE job_id = $jobId
              AND active_variant_id = $sourceVariantId
              AND status = $completed;
            """;
        update.Parameters.AddWithValue("$newVariantId", newVariantId.ToString());
        update.Parameters.AddWithValue("$running", (int)JobStatus.Running);
        update.Parameters.AddWithValue("$planning", (int)ProductionStage.Planning);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$jobId", command.JobId.ToString());
        update.Parameters.AddWithValue("$sourceVariantId", command.SourceVariantId.ToString());
        update.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        return await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record JobState(
        long? TelegramUserId,
        JobStatus Status,
        VariantId? ActiveVariantId,
        bool SourceBelongsToJob);
}
