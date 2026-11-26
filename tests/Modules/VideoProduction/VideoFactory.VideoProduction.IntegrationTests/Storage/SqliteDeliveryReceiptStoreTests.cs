using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteDeliveryReceiptStoreTests
{
    [Fact]
    public async Task HasSucceededRequiresMatchingSuccessfulReceipt()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"delivery-receipts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string connectionString = $"Data Source={Path.Combine(directory, "receipts.sqlite")}";
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            SqliteDeliveryReceiptStore store = new(connectionString);
            JobId jobId = JobId.New();
            VariantId variantId = VariantId.New();
            ArtifactId artifactId = ArtifactId.New();

            await InsertAsync(connectionString, new DeliveryReceipt(
                Guid.NewGuid(), jobId, variantId, artifactId, 42, null,
                "failed", "telegram.failed", "Delivery failed."), CancellationToken.None);

            Assert.False(await store.HasSucceededAsync(jobId, variantId, artifactId, 42, CancellationToken.None));

            await InsertAsync(connectionString, new DeliveryReceipt(
                Guid.NewGuid(), jobId, variantId, artifactId, 42, "message-1",
                "succeeded", null, null), CancellationToken.None);

            Assert.True(await store.HasSucceededAsync(jobId, variantId, artifactId, 42, CancellationToken.None));
            Assert.False(await store.HasSucceededAsync(jobId, variantId, artifactId, 43, CancellationToken.None));
            Assert.False(await store.HasSucceededAsync(jobId, variantId, ArtifactId.New(), 42, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task InsertAsync(
        string connectionString,
        DeliveryReceipt receipt,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
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
