using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.TelegramIntake;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class SqliteTelegramIntakeStoreTests
{
    [Fact]
    public async Task FailedDownloadReleasesPendingReservation()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);
        IntakeReservationResult.Reserved failed = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(
                new IntakeReservation(123, 123, 10, "failed", 524_288_000, 524_288_000, "intake/failed.tmp"),
                CancellationToken.None));

        await store.FailAsync(failed.SessionId, 10, "telegram.download_failed", CancellationToken.None);
        IntakeReservationResult replacement = await store.ReserveAsync(
            new IntakeReservation(123, 123, 20, "replacement", 524_288_000, 524_288_000, "intake/replacement.tmp"),
            CancellationToken.None);

        Assert.IsType<IntakeReservationResult.Reserved>(replacement);
        TelegramIntakeSession session = Assert.IsType<TelegramIntakeSession>(
            await store.GetOpenAsync(123, 123, CancellationToken.None));
        Assert.Equal(TelegramIntakeItemStatus.Failed, session.Items[0].Status);
    }

    [Fact]
    public async Task ReservationExpansionIsPersistedBeforeLargerFileIsAccepted()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);
        IntakeReservationResult.Reserved reservation = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(
                new IntakeReservation(123, 123, 10, "file", 100, 1_000, "intake/file.tmp"),
                CancellationToken.None));

        long? expanded = await store.TryExpandReservationAsync(
            reservation.SessionId, 10, 200, CancellationToken.None);
        ArtifactId artifact = await RegisterArtifactAsync(database, "expanded.bin");
        IntakeAcceptanceResult accepted = await store.AcceptAsync(
            new IntakeAcceptance(reservation.SessionId, 10, artifact, "expanded", 200, 1_000_000),
            CancellationToken.None);

        Assert.Equal(200, expanded);
        Assert.IsType<IntakeAcceptanceResult.Accepted>(accepted);
    }

    [Fact]
    public async Task DurationQuotaFailureReleasesPendingReservation()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);
        IntakeReservationResult.Reserved first = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(new IntakeReservation(123, 123, 10, "a", 100, 1_000, "intake/a.tmp"), CancellationToken.None));
        IntakeReservationResult.Reserved second = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(new IntakeReservation(123, 123, 20, "b", 100, 1_000, "intake/b.tmp"), CancellationToken.None));
        ArtifactId firstArtifact = await RegisterArtifactAsync(database, "duration-a.bin");
        ArtifactId secondArtifact = await RegisterArtifactAsync(database, "duration-b.bin");
        await store.AcceptAsync(new IntakeAcceptance(first.SessionId, 10, firstArtifact, "aaa", 100, 590_000_000), CancellationToken.None);

        IntakeAcceptanceResult result = await store.AcceptAsync(
            new IntakeAcceptance(second.SessionId, 20, secondArtifact, "bbb", 100, 20_000_000),
            CancellationToken.None);

        IntakeAcceptanceResult.Rejected rejected = Assert.IsType<IntakeAcceptanceResult.Rejected>(result);
        Assert.Equal("intake.duration_limit_exceeded", rejected.Code);
        TelegramIntakeSession session = Assert.IsType<TelegramIntakeSession>(
            await store.GetOpenAsync(123, 123, CancellationToken.None));
        Assert.Equal(TelegramIntakeItemStatus.Failed, session.Items[1].Status);
        Assert.Null(await new SqliteArtifactStore(database.ConnectionString)
            .GetAsync(secondArtifact, CancellationToken.None));
    }

    [Fact]
    public async Task DuplicateShaIsRejectedAndReservationIsReleased()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);
        IntakeReservationResult.Reserved first = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(new IntakeReservation(123, 123, 10, "a", 300_000_000, 300_000_000, "intake/a.tmp"), CancellationToken.None));
        IntakeReservationResult.Reserved second = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(new IntakeReservation(123, 123, 20, "b", 200_000_000, 300_000_000, "intake/b.tmp"), CancellationToken.None));
        ArtifactId firstArtifact = await RegisterArtifactAsync(database, "sha-a.bin");
        ArtifactId secondArtifact = await RegisterArtifactAsync(database, "sha-b.bin");
        await store.AcceptAsync(new IntakeAcceptance(first.SessionId, 10, firstArtifact, "same", 300_000_000, 1_000_000), CancellationToken.None);

        IntakeAcceptanceResult duplicate = await store.AcceptAsync(
            new IntakeAcceptance(second.SessionId, 20, secondArtifact, "same", 200_000_000, 1_000_000),
            CancellationToken.None);
        IntakeReservationResult replacement = await store.ReserveAsync(
            new IntakeReservation(123, 123, 30, "c", 200_000_000, 300_000_000, "intake/c.tmp"),
            CancellationToken.None);

        IntakeAcceptanceResult.Rejected rejected = Assert.IsType<IntakeAcceptanceResult.Rejected>(duplicate);
        Assert.Equal("intake.duplicate_content", rejected.Code);
        Assert.IsType<IntakeReservationResult.Reserved>(replacement);
        Assert.Null(await new SqliteArtifactStore(database.ConnectionString)
            .GetAsync(secondArtifact, CancellationToken.None));
    }

    [Fact]
    public async Task CancelClosesSessionAndReturnsPendingTemporaryPathsIdempotently()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);
        await store.ReserveAsync(
            new IntakeReservation(123, 123, 10, "pending", 100, 1_000, "intake/pending.tmp"),
            CancellationToken.None);

        IntakeCancellationResult.Cancelled cancelled = Assert.IsType<IntakeCancellationResult.Cancelled>(
            await store.CancelAsync(123, 123, CancellationToken.None));
        IntakeCancellationResult.AlreadyClosed repeated = Assert.IsType<IntakeCancellationResult.AlreadyClosed>(
            await store.CancelAsync(123, 123, CancellationToken.None));

        Assert.Equal(["intake/pending.tmp"], cancelled.TemporaryRelativePaths.ToArray());
        Assert.Empty(repeated.TemporaryRelativePaths);
        Assert.Null(await store.GetOpenAsync(123, 123, CancellationToken.None));
    }

    [Fact]
    public async Task CancelReturnsAcceptedFileAndDeletesItsUnownedArtifact()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);
        IntakeReservationResult.Reserved reservation = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(
                new IntakeReservation(123, 123, 10, "accepted", 100, 1_000, "intake/accepted.tmp"),
                CancellationToken.None));
        ArtifactId artifactId = await RegisterArtifactAsync(database, "accepted.bin");
        Assert.IsType<IntakeAcceptanceResult.Accepted>(await store.AcceptAsync(
            new IntakeAcceptance(reservation.SessionId, 10, artifactId, "accepted", 100, 1_000_000),
            CancellationToken.None));

        IntakeCancellationResult.Cancelled cancelled = Assert.IsType<IntakeCancellationResult.Cancelled>(
            await store.CancelAsync(123, 123, CancellationToken.None));

        Assert.Equal(["jobs/intake/accepted.bin"], cancelled.TemporaryRelativePaths.ToArray());
        Assert.Null(await new SqliteArtifactStore(database.ConnectionString)
            .GetAsync(artifactId, CancellationToken.None));
    }

    [Fact]
    public async Task DoneCreatesOneJobAndRepeatedReceiptReturnsIt()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);
        await store.SetInstructionAsync(123, 123, "Собери обзор", CancellationToken.None);
        IntakeReservationResult.Reserved first = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(new IntakeReservation(123, 123, 10, "a", 100, 1_000, "intake/a.tmp"), CancellationToken.None));
        IntakeReservationResult.Reserved second = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(new IntakeReservation(123, 123, 20, "b", 100, 1_000, "intake/b.tmp"), CancellationToken.None));
        ArtifactId firstArtifact = await RegisterArtifactAsync(database, "done-a.bin");
        ArtifactId secondArtifact = await RegisterArtifactAsync(database, "done-b.bin");
        await store.AcceptAsync(new IntakeAcceptance(first.SessionId, 10, firstArtifact, "aaa", 100, 10_000_000), CancellationToken.None);
        await store.AcceptAsync(new IntakeAcceptance(second.SessionId, 20, secondArtifact, "bbb", 100, 10_000_000), CancellationToken.None);

        IntakeCompletionResult.Created created = Assert.IsType<IntakeCompletionResult.Created>(
            await store.CompleteAsync(new IntakeCompletion(123, 123, 30), CancellationToken.None));
        IntakeCompletionResult.Existing repeated = Assert.IsType<IntakeCompletionResult.Existing>(
            await store.CompleteAsync(new IntakeCompletion(123, 123, 30), CancellationToken.None));

        Assert.Equal(created.JobId, repeated.JobId);
        Assert.Null(await store.GetOpenAsync(123, 123, CancellationToken.None));
        JobCreationRecord restored = Assert.IsType<JobCreationRecord>(
            await new SqliteJobStateStore(database.ConnectionString)
                .GetAsync(created.JobId, CancellationToken.None));
        Assert.Equal([firstArtifact, secondArtifact], restored.Request.SourceAssetIds.ToArray());
    }

    [Fact]
    public async Task StartupRecoveryFailsPendingItemsAndReleasesReservations()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);
        await store.ReserveAsync(
            new IntakeReservation(123, 123, 10, "pending", 524_288_000, 524_288_000, "intake/pending.tmp"),
            CancellationToken.None);

        PendingRecoveryResult recovery = await new SqliteTelegramIntakeStore(database.ConnectionString)
            .RecoverPendingAsync(CancellationToken.None);
        IntakeReservationResult next = await store.ReserveAsync(
            new IntakeReservation(123, 123, 20, "next", 524_288_000, 524_288_000, "intake/next.tmp"),
            CancellationToken.None);

        Assert.Equal(["intake/pending.tmp"], recovery.TemporaryRelativePaths.ToArray());
        Assert.IsType<IntakeReservationResult.Reserved>(next);
        TelegramIntakeSession session = Assert.IsType<TelegramIntakeSession>(
            await store.GetOpenAsync(123, 123, CancellationToken.None));
        Assert.Equal(TelegramIntakeItemStatus.Failed, session.Items[0].Status);
        Assert.Equal(TelegramIntakeItemStatus.Pending, session.Items[1].Status);
    }

    [Fact]
    public async Task InstructionStyleAndDurationSurviveRestart()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);

        await store.SetInstructionAsync(123, 123, "Собери обзор", CancellationToken.None);
        await store.SetStyleAsync(123, 123, RequestedReelsStyle.Cinematic, CancellationToken.None);
        await store.SetDurationAsync(123, 123, 30, CancellationToken.None);

        TelegramIntakeSession session = Assert.IsType<TelegramIntakeSession>(
            await new SqliteTelegramIntakeStore(database.ConnectionString)
                .GetOpenAsync(123, 123, CancellationToken.None));
        Assert.Equal("Собери обзор", session.Instruction);
        Assert.Equal(RequestedReelsStyle.Cinematic, session.RequestedStyle);
        Assert.Equal(30, session.TargetDurationSeconds);
    }

    [Fact]
    public async Task DuplicateMessageAndFileUniqueIdDoNotConsumeAdditionalSlots()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore store = new(database.ConnectionString);
        IntakeReservationResult.Reserved first = Assert.IsType<IntakeReservationResult.Reserved>(
            await store.ReserveAsync(
                new IntakeReservation(123, 123, 10, "same-file", 100, 1_000, "intake/a.tmp"),
                CancellationToken.None));

        IntakeReservationResult sameMessage = await store.ReserveAsync(
            new IntakeReservation(123, 123, 10, "same-file", 100, 1_000, "intake/a.tmp"),
            CancellationToken.None);
        IntakeReservationResult sameFile = await store.ReserveAsync(
            new IntakeReservation(123, 123, 20, "same-file", 100, 1_000, "intake/b.tmp"),
            CancellationToken.None);

        Assert.IsType<IntakeReservationResult.Duplicate>(sameMessage);
        Assert.IsType<IntakeReservationResult.Duplicate>(sameFile);
        TelegramIntakeSession session = Assert.IsType<TelegramIntakeSession>(
            await store.GetOpenAsync(123, 123, CancellationToken.None));
        Assert.Single(session.Items);
        Assert.Equal(first.SessionId, session.SessionId);
    }

    [Fact]
    public async Task AcceptedItemsSurviveRestartInTelegramMessageOrder()
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        SqliteTelegramIntakeStore firstStore = new(database.ConnectionString);
        IntakeReservationResult.Reserved later = Assert.IsType<IntakeReservationResult.Reserved>(
            await firstStore.ReserveAsync(
                new IntakeReservation(123, 123, 20, "file-b", 200, 1_000, "intake/b.tmp"),
                CancellationToken.None));
        IntakeReservationResult.Reserved earlier = Assert.IsType<IntakeReservationResult.Reserved>(
            await firstStore.ReserveAsync(
                new IntakeReservation(123, 123, 10, "file-a", 100, 1_000, "intake/a.tmp"),
                CancellationToken.None));
        ArtifactId firstArtifact = await RegisterArtifactAsync(database, "a.bin");
        ArtifactId secondArtifact = await RegisterArtifactAsync(database, "b.bin");

        await firstStore.AcceptAsync(
            new IntakeAcceptance(later.SessionId, 20, secondArtifact, "bbb", 200, 2_000_000),
            CancellationToken.None);
        await firstStore.AcceptAsync(
            new IntakeAcceptance(earlier.SessionId, 10, firstArtifact, "aaa", 100, 1_000_000),
            CancellationToken.None);

        TelegramIntakeSession? restored = await new SqliteTelegramIntakeStore(database.ConnectionString)
            .GetOpenAsync(123, 123, CancellationToken.None);

        Assert.NotNull(restored);
        Assert.Equal([10L, 20L], restored.Items.Select(item => item.MessageId).ToArray());
        Assert.All(restored.Items, item => Assert.Equal(TelegramIntakeItemStatus.Accepted, item.Status));
        Assert.Equal([firstArtifact, secondArtifact], restored.Items.Select(item => item.ArtifactId!.Value).ToArray());
    }

    private static async Task<ArtifactId> RegisterArtifactAsync(
        TemporarySqliteDatabase database,
        string fileName)
    {
        ArtifactId artifactId = ArtifactId.New();
        ArtifactRegistrationResult result = await new SqliteArtifactStore(database.ConnectionString)
            .RegisterAsync(
                new ArtifactRecord(
                    artifactId,
                    "OriginalVideo",
                    $"jobs/intake/{fileName}",
                    "hash",
                    1,
                    "{}",
                    null,
                    "test"),
                CancellationToken.None);
        Assert.IsType<ArtifactRegistrationResult.Created>(result);
        return artifactId;
    }
}
