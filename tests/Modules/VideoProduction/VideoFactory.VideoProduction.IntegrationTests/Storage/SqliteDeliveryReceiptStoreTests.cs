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
            ArtifactId malformedArtifactId = ArtifactId.New();
            await SeedDeliveryIdentityAsync(
                connectionString, jobId, variantId,
                [(artifactId, new string('a', 64), 123L), (malformedArtifactId, new string('c', 64), 10L)]);

            await InsertAsync(connectionString, new DeliveryReceipt(
                Guid.NewGuid(), jobId, variantId, artifactId, 42, null,
                "failed", "telegram.failed", "Delivery failed."), CancellationToken.None);

            await InsertAsync(connectionString, new DeliveryReceipt(
                Guid.NewGuid(), jobId, variantId, artifactId, 42, "legacy-message",
                "succeeded", null, null), CancellationToken.None, receiptFormatVersion: 1);

            Assert.True(await store.HasSucceededAsync(jobId, variantId, artifactId, 42, CancellationToken.None));

            await InsertAsync(connectionString, new DeliveryReceipt(
                Guid.NewGuid(), jobId, variantId, malformedArtifactId, 42, "malformed-message",
                "succeeded", null, null), CancellationToken.None, receiptFormatVersion: 2);

            Assert.False(await store.HasSucceededAsync(jobId, variantId, malformedArtifactId, 42, CancellationToken.None));

            const string uploadedSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            await store.AppendAsync(new DeliveryReceipt(
                Guid.NewGuid(), jobId, variantId, artifactId, 42, "message-1",
                "succeeded", null, null, uploadedSha256, 123), CancellationToken.None);

            Assert.True(await store.HasSucceededAsync(jobId, variantId, artifactId, 42, CancellationToken.None));
            Assert.False(await store.HasSucceededAsync(jobId, variantId, artifactId, 43, CancellationToken.None));
            Assert.False(await store.HasSucceededAsync(jobId, variantId, ArtifactId.New(), 42, CancellationToken.None));

            await using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT uploaded_sha256, uploaded_size_bytes FROM delivery_receipts WHERE uploaded_sha256 IS NOT NULL;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(uploadedSha256, reader.GetString(0));
            Assert.Equal(123, reader.GetInt64(1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RecoveryDistinguishesNewIndeterminateAndSucceededIntent()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"delivery-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string connectionString = $"Data Source={Path.Combine(directory, "receipts.sqlite")}";
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            JobId jobId = JobId.New();
            VariantId variantId = VariantId.New();
            ArtifactId artifactId = ArtifactId.New();
            string hash = new('a', 64);
            await SeedDeliveryIdentityAsync(connectionString, jobId, variantId, [(artifactId, hash, 123L)]);
            DeliveryIntent intent = new(Guid.NewGuid(), jobId, variantId, artifactId, 42, hash, 123);

            Assert.IsType<DeliveryIntentStart.Started>(
                await new SqliteDeliveryReceiptStore(connectionString).TryStartAsync(intent, CancellationToken.None));
            Assert.IsType<DeliveryIntentStart.Indeterminate>(
                await new SqliteDeliveryReceiptStore(connectionString).TryStartAsync(
                    intent with { ReceiptId = Guid.NewGuid() }, CancellationToken.None));

            await new SqliteDeliveryReceiptStore(connectionString).CompleteAsync(
                new DeliveryReceipt(
                    intent.ReceiptId, jobId, variantId, artifactId, 42, "message-1",
                    "succeeded", null, null, hash, 123),
                CancellationToken.None);

            Assert.IsType<DeliveryIntentStart.AlreadySucceeded>(
                await new SqliteDeliveryReceiptStore(connectionString).TryStartAsync(
                    intent with { ReceiptId = Guid.NewGuid() }, CancellationToken.None));
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
        CancellationToken cancellationToken,
        int receiptFormatVersion = 2)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            INSERT INTO delivery_receipts (
                receipt_id, job_id, variant_id, artifact_id, recipient_id,
                remote_message_id, status, error_code, error_message, receipt_format_version, created_at)
            VALUES ($receiptId, $jobId, $variantId, $artifactId, $recipientId,
                    $remoteMessageId, $status, $errorCode, $errorMessage, $receiptFormatVersion, $createdAt);
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
        command.Parameters.AddWithValue("$receiptFormatVersion", receiptFormatVersion);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SeedDeliveryIdentityAsync(
        string connectionString,
        JobId jobId,
        VariantId variantId,
        IReadOnlyList<(ArtifactId Id, string Hash, long Size)> artifacts)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO jobs (
                    job_id, source_artifact_id, comment, requested_mode, captions_enabled, music_enabled,
                    status, stage, created_at, updated_at, active_variant_id)
                VALUES ($jobId, $sourceId, 'test', 0, 0, 0, 3, 8, $now, $now, $variantId);
                INSERT INTO variants (variant_id, job_id, created_at, updated_at)
                VALUES ($variantId, $jobId, $now, $now);
                """;
            command.Parameters.AddWithValue("$jobId", jobId.ToString());
            command.Parameters.AddWithValue("$sourceId", ArtifactId.New().ToString());
            command.Parameters.AddWithValue("$variantId", variantId.ToString());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        foreach ((ArtifactId id, string hash, long size) in artifacts)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO artifacts (
                    artifact_id, job_id, variant_id, artifact_type, relative_path, sha256,
                    size_bytes, created_at)
                VALUES ($artifactId, $jobId, $variantId, 'final-mp4', $path, $hash, $size, $now);
                """;
            command.Parameters.AddWithValue("$artifactId", id.ToString());
            command.Parameters.AddWithValue("$jobId", jobId.ToString());
            command.Parameters.AddWithValue("$variantId", variantId.ToString());
            command.Parameters.AddWithValue("$path", $"final/{id}.mp4");
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
