using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteRepeatDeliveryRequestStoreTests
{
    [Fact]
    public async Task SameTelegramCommandIsStartedOnlyOnceAndReturnsReceiptAfterCompletion()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"repeat-delivery-{Guid.NewGuid():N}.db");
        string connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        try
        {
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            SqliteRepeatDeliveryRequestStore store = new(connectionString);
            RepeatDeliveryCommand command = new(JobId.New(), 123456, 9001);
            VariantId variantId = VariantId.New();
            ArtifactId artifactId = ArtifactId.New();
            await SeedDeliveryIdentityAsync(connectionString, command.JobId, variantId, artifactId);

            Assert.IsType<RepeatDeliveryRequestStart.Started>(await store.TryStartAsync(command, CancellationToken.None));
            Assert.IsType<RepeatDeliveryRequestStart.Pending>(await store.TryStartAsync(command, CancellationToken.None));

            DeliveryReceipt receipt = new(
                Guid.NewGuid(), command.JobId, variantId, artifactId, command.TelegramUserId,
                "message-42", "succeeded", null, null, new string('a', 64), 123);
            await store.CompleteAsync(command.TelegramUpdateId, receipt, CancellationToken.None);

            await using (SqliteConnection connection = new(connectionString))
            {
                await connection.OpenAsync();
                await using SqliteCommand count = connection.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM delivery_receipts WHERE receipt_id = $receiptId;";
                count.Parameters.AddWithValue("$receiptId", receipt.ReceiptId.ToString());
                Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
            }

            SqliteRepeatDeliveryRequestStore recoveredStore = new(connectionString);
            RepeatDeliveryRequestStart repeated = await recoveredStore.TryStartAsync(command, CancellationToken.None);
            RepeatDeliveryRequestStart.AlreadyProcessed processed = Assert.IsType<RepeatDeliveryRequestStart.AlreadyProcessed>(repeated);
            Assert.Equal(receipt.ReceiptId, processed.Receipt.ReceiptId);
            Assert.Equal("message-42", processed.Receipt.RemoteMessageId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
            File.Delete(databasePath);
        }
    }

    private static async Task SeedDeliveryIdentityAsync(
        string connectionString,
        JobId jobId,
        VariantId variantId,
        ArtifactId artifactId)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (
                job_id, source_artifact_id, comment, requested_mode, captions_enabled, music_enabled,
                status, stage, created_at, updated_at, active_variant_id)
            VALUES ($jobId, $sourceId, 'test', 0, 0, 0, 3, 8, $now, $now, $variantId);
            INSERT INTO variants (variant_id, job_id, created_at, updated_at)
            VALUES ($variantId, $jobId, $now, $now);
            INSERT INTO artifacts (
                artifact_id, job_id, variant_id, artifact_type, relative_path, sha256,
                size_bytes, created_at)
            VALUES ($artifactId, $jobId, $variantId, 'final-mp4', $path, $hash, 123, $now);
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$sourceId", ArtifactId.New().ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        command.Parameters.AddWithValue("$path", $"final/{artifactId}.mp4");
        command.Parameters.AddWithValue("$hash", new string('a', 64));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
