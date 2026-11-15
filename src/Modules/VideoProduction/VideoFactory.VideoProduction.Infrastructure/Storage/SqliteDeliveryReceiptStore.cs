using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Delivery;

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
}
