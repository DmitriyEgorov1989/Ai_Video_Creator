using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Сохраняет delivery receipts в SQLite.</summary>
public sealed class SqliteDeliveryReceiptStore : IDeliveryReceiptStore
{
    private readonly string connectionString;

    /// <summary>Создаёт store delivery receipts.</summary>
    public SqliteDeliveryReceiptStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<DeliveryIntentStart> TryStartAsync(
        DeliveryIntent intent,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using SqliteCommand succeeded = connection.CreateCommand();
        succeeded.Transaction = transaction;
        succeeded.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM delivery_receipts
                WHERE job_id = $jobId AND variant_id = $variantId
                  AND artifact_id = $artifactId AND recipient_id = $recipientId
                  AND status = 'succeeded'
                  AND (receipt_format_version = 1 OR (
                      receipt_format_version = 2
                      AND length(uploaded_sha256) = 64
                      AND uploaded_sha256 NOT GLOB '*[^0-9A-Fa-f]*'
                      AND lower(uploaded_sha256) = lower($expectedSha256)
                      AND uploaded_size_bytes = $expectedSizeBytes)));
            """;
        AddIdentityParameters(succeeded, intent);
        succeeded.Parameters.AddWithValue("$expectedSha256", intent.ExpectedSha256);
        succeeded.Parameters.AddWithValue("$expectedSizeBytes", intent.ExpectedSizeBytes);
        if (Convert.ToInt64(await succeeded.ExecuteScalarAsync(cancellationToken)) == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return new DeliveryIntentStart.AlreadySucceeded();
        }

        await using SqliteCommand pending = connection.CreateCommand();
        pending.Transaction = transaction;
        pending.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM delivery_receipts
                WHERE job_id = $jobId AND variant_id = $variantId
                  AND artifact_id = $artifactId AND recipient_id = $recipientId
                  AND status = 'sending');
            """;
        AddIdentityParameters(pending, intent);
        if (Convert.ToInt64(await pending.ExecuteScalarAsync(cancellationToken)) == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return new DeliveryIntentStart.Indeterminate();
        }

        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO delivery_receipts (
                receipt_id, job_id, variant_id, artifact_id, recipient_id,
                status, receipt_format_version, created_at)
            VALUES ($receiptId, $jobId, $variantId, $artifactId, $recipientId,
                    'sending', 2, $createdAt);
            """;
        AddIdentityParameters(insert, intent);
        insert.Parameters.AddWithValue("$receiptId", intent.ReceiptId.ToString());
        insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DeliveryIntentStart.Started();
    }

    /// <inheritdoc />
    public async Task CompleteAsync(DeliveryReceipt receipt, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE delivery_receipts
            SET remote_message_id = $remoteMessageId, status = $status,
                error_code = $errorCode, error_message = $errorMessage,
                uploaded_sha256 = $uploadedSha256, uploaded_size_bytes = $uploadedSizeBytes
            WHERE receipt_id = $receiptId AND status = 'sending';
            """;
        command.Parameters.AddWithValue("$receiptId", receipt.ReceiptId.ToString());
        command.Parameters.AddWithValue("$remoteMessageId", (object?)receipt.RemoteMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", receipt.Status);
        command.Parameters.AddWithValue("$errorCode", (object?)receipt.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)receipt.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$uploadedSha256", (object?)receipt.UploadedSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$uploadedSizeBytes", (object?)receipt.UploadedSizeBytes ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AppendAsync(DeliveryReceipt receipt, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_receipts (
                receipt_id, job_id, variant_id, artifact_id, recipient_id,
                remote_message_id, status, error_code, error_message,
                uploaded_sha256, uploaded_size_bytes, receipt_format_version, created_at)
            VALUES ($receiptId, $jobId, $variantId, $artifactId, $recipientId,
                    $remoteMessageId, $status, $errorCode, $errorMessage,
                    $uploadedSha256, $uploadedSizeBytes, 2, $createdAt);
            """;
        command.Parameters.AddWithValue("$receiptId", receipt.ReceiptId.ToString());
        command.Parameters.AddWithValue("$jobId", receipt.JobId.ToString());
        command.Parameters.AddWithValue("$variantId", receipt.VariantId.ToString());
        command.Parameters.AddWithValue("$artifactId", receipt.ArtifactId.ToString());
        command.Parameters.AddWithValue("$recipientId", receipt.RecipientId);
        command.Parameters.AddWithValue("$remoteMessageId", (object?)receipt.RemoteMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", receipt.Status);
        command.Parameters.AddWithValue("$errorCode", (object?)receipt.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", (object?)receipt.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$uploadedSha256", (object?)receipt.UploadedSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$uploadedSizeBytes", (object?)receipt.UploadedSizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HasSucceededAsync(
        JobId jobId,
        VariantId variantId,
        ArtifactId artifactId,
        long recipientId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM delivery_receipts r
                INNER JOIN artifacts a ON a.artifact_id = r.artifact_id
                WHERE r.job_id = $jobId AND r.variant_id = $variantId
                  AND r.artifact_id = $artifactId AND r.recipient_id = $recipientId
                  AND r.status = 'succeeded'
                  AND (r.receipt_format_version = 1 OR (
                      r.receipt_format_version = 2
                      AND length(r.uploaded_sha256) = 64
                      AND r.uploaded_sha256 NOT GLOB '*[^0-9A-Fa-f]*'
                      AND lower(r.uploaded_sha256) = lower(a.sha256)
                      AND r.uploaded_size_bytes = a.size_bytes)));
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        command.Parameters.AddWithValue("$recipientId", recipientId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static void AddIdentityParameters(SqliteCommand command, DeliveryIntent intent)
    {
        command.Parameters.AddWithValue("$jobId", intent.JobId.ToString());
        command.Parameters.AddWithValue("$variantId", intent.VariantId.ToString());
        command.Parameters.AddWithValue("$artifactId", intent.ArtifactId.ToString());
        command.Parameters.AddWithValue("$recipientId", intent.RecipientId);
    }
}
