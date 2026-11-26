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
    public async Task AppendAsync(DeliveryReceipt receipt, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO delivery_receipts (
                receipt_id, job_id, variant_id, artifact_id, recipient_id,
                remote_message_id, status, error_code, error_message, created_at)
            VALUES ($receiptId, $jobId, $variantId, $artifactId, $recipientId,
                    $remoteMessageId, $status, $errorCode, $errorMessage, $createdAt);
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
                SELECT 1 FROM delivery_receipts
                WHERE job_id = $jobId AND variant_id = $variantId
                  AND artifact_id = $artifactId AND recipient_id = $recipientId
                  AND status = 'succeeded');
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        command.Parameters.AddWithValue("$recipientId", recipientId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }
}
