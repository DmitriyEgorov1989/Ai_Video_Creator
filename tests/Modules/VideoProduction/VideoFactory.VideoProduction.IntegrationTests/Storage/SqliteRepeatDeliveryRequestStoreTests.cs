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

            Assert.IsType<RepeatDeliveryRequestStart.Started>(await store.TryStartAsync(command, CancellationToken.None));
            Assert.IsType<RepeatDeliveryRequestStart.Pending>(await store.TryStartAsync(command, CancellationToken.None));

            DeliveryReceipt receipt = new(
                Guid.NewGuid(), command.JobId, VariantId.New(), ArtifactId.New(), command.TelegramUserId,
                "message-42", "succeeded", null, null);
            await store.CompleteAsync(command.TelegramUpdateId, receipt, CancellationToken.None);

            RepeatDeliveryRequestStart repeated = await store.TryStartAsync(command, CancellationToken.None);
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
}
