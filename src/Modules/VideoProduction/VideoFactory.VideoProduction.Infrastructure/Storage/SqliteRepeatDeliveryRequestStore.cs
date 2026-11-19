using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Delivery;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Хранит идемпотентность повторных Telegram-команд.</summary>
public sealed class SqliteRepeatDeliveryRequestStore : IRepeatDeliveryRequestStore
{
    private readonly string connectionString;

    /// <summary>Создаёт хранилище команд повторной доставки.</summary>
    public SqliteRepeatDeliveryRequestStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<RepeatDeliveryRequestStart> TryStartAsync(RepeatDeliveryCommand command, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO repeat_delivery_requests (telegram_update_id, job_id, telegram_user_id, status, created_at)
            VALUES ($updateId, $jobId, $userId, 'pending', $createdAt)
            ON CONFLICT (telegram_update_id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$updateId", command.TelegramUpdateId);
        insert.Parameters.AddWithValue("$jobId", command.JobId.ToString());
        insert.Parameters.AddWithValue("$userId", command.TelegramUserId);
        insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return new RepeatDeliveryRequestStart.Started();
        }

        await using SqliteCommand select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT r.status, r.remote_message_id, r.error_code, r.error_message,
                   r.job_id, r.variant_id, r.artifact_id, r.telegram_user_id, r.receipt_id
            FROM repeat_delivery_requests r
            WHERE r.telegram_update_id = $updateId;
            """;
        select.Parameters.AddWithValue("$updateId", command.TelegramUpdateId);
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await reader.DisposeAsync();
            await transaction.RollbackAsync(cancellationToken);
            return new RepeatDeliveryRequestStart.Pending();
        }

        string status = reader.GetString(0);
        if (status == "pending")
        {
            await reader.DisposeAsync();
            await transaction.CommitAsync(cancellationToken);
            return new RepeatDeliveryRequestStart.Pending();
        }

        DeliveryReceipt receipt = new(
            Guid.TryParse(reader.GetString(8), out Guid receiptId) ? receiptId : Guid.Empty,
            new(new Guid(reader.GetString(4))),
            new(new Guid(reader.GetString(5))),
            new(new Guid(reader.GetString(6))),
            reader.GetInt64(7),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            status,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return new RepeatDeliveryRequestStart.AlreadyProcessed(receipt);
    }

    /// <inheritdoc />
    public async Task CompleteAsync(long telegramUpdateId, DeliveryReceipt receipt, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE repeat_delivery_requests
            SET status = $status, receipt_id = $receiptId, remote_message_id = $remoteMessageId,
                error_code = $errorCode, error_message = $errorMessage, completed_at = $completedAt,
                job_id = $jobId, variant_id = $variantId, artifact_id = $artifactId, telegram_user_id = $userId
            WHERE telegram_update_id = $updateId;
            """;
        command.Parameters.AddWithValue("$status", receipt.Status);
        command.Parameters.AddWithValue("$receiptId", receipt.ReceiptId.ToString());
        command.Parameters.AddWithValue("$remoteMessageId", (object?)receipt.RemoteMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)receipt.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)receipt.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$jobId", receipt.JobId.ToString());
        command.Parameters.AddWithValue("$variantId", receipt.VariantId.ToString());
        command.Parameters.AddWithValue("$artifactId", receipt.ArtifactId.ToString());
        command.Parameters.AddWithValue("$userId", receipt.RecipientId);
        command.Parameters.AddWithValue("$updateId", telegramUpdateId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
