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
    public async Task<RepeatDeliveryRequestStart?> FindAsync(
        long telegramUpdateId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText = """
            SELECT r.status, r.remote_message_id, r.error_code, r.error_message,
                   r.job_id, r.variant_id, r.artifact_id, r.telegram_user_id, r.receipt_id
            FROM repeat_delivery_requests r
            WHERE r.telegram_update_id = $updateId;
            """;
        select.Parameters.AddWithValue("$updateId", telegramUpdateId);
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return ReadStart(reader);
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

        RepeatDeliveryRequestStart result = ReadStart(reader);
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(long telegramUpdateId, DeliveryReceipt receipt, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand insertReceipt = connection.CreateCommand();
        insertReceipt.Transaction = transaction;
        insertReceipt.CommandText = """
            INSERT INTO delivery_receipts (
                receipt_id, job_id, variant_id, artifact_id, recipient_id,
                remote_message_id, status, error_code, error_message,
                uploaded_sha256, uploaded_size_bytes, receipt_format_version, created_at)
            SELECT $receiptId, $jobId, $variantId, $artifactId, $userId,
                   $remoteMessageId, $status, $errorCode, $errorMessage,
                   $uploadedSha256, $uploadedSizeBytes, 2, $createdAt
            WHERE EXISTS (
                SELECT 1 FROM repeat_delivery_requests
                WHERE telegram_update_id = $updateId
                  AND (status = 'pending' OR receipt_id = $receiptId))
            ON CONFLICT (receipt_id) DO NOTHING;
            """;
        AddCompletionParameters(insertReceipt, receipt);
        insertReceipt.Parameters.AddWithValue("$uploadedSha256", (object?)receipt.UploadedSha256 ?? DBNull.Value);
        insertReceipt.Parameters.AddWithValue("$uploadedSizeBytes", (object?)receipt.UploadedSizeBytes ?? DBNull.Value);
        insertReceipt.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        insertReceipt.Parameters.AddWithValue("$updateId", telegramUpdateId);
        await insertReceipt.ExecuteNonQueryAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE repeat_delivery_requests
            SET status = $status, receipt_id = $receiptId, remote_message_id = $remoteMessageId,
                error_code = $errorCode, error_message = $errorMessage, completed_at = $completedAt,
                job_id = $jobId, variant_id = $variantId, artifact_id = $artifactId, telegram_user_id = $userId
            WHERE telegram_update_id = $updateId
              AND (status = 'pending' OR receipt_id = $receiptId);
            """;
        AddCompletionParameters(command, receipt);
        command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$updateId", telegramUpdateId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static RepeatDeliveryRequestStart ReadStart(SqliteDataReader reader)
    {
        string status = reader.GetString(0);
        if (status == "pending") return new RepeatDeliveryRequestStart.Pending();

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
        return new RepeatDeliveryRequestStart.AlreadyProcessed(receipt);
    }

    private static void AddCompletionParameters(SqliteCommand command, DeliveryReceipt receipt)
    {
        command.Parameters.AddWithValue("$status", receipt.Status);
        command.Parameters.AddWithValue("$receiptId", receipt.ReceiptId.ToString());
        command.Parameters.AddWithValue("$remoteMessageId", (object?)receipt.RemoteMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorCode", (object?)receipt.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)receipt.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$jobId", receipt.JobId.ToString());
        command.Parameters.AddWithValue("$variantId", receipt.VariantId.ToString());
        command.Parameters.AddWithValue("$artifactId", receipt.ArtifactId.ToString());
        command.Parameters.AddWithValue("$userId", receipt.RecipientId);
    }
}
