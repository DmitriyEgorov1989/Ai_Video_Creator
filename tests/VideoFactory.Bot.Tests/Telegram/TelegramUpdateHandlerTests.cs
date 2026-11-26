using VideoFactory.Bot.Configuration;
using VideoFactory.Bot.Telegram;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.Cancellation;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using VideoFactory.VideoProduction.Application.Variants;

namespace VideoFactory.Bot.Tests.Telegram;

public sealed class TelegramUpdateHandlerTests
{
    [Fact]
    public async Task StartupRecoveryDeletesOnlyReturnedPathsInsideWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"telegram-recovery-{Guid.NewGuid():N}");
        string outside = Path.Combine(Path.GetTempPath(), $"telegram-outside-{Guid.NewGuid():N}.tmp");
        string inside = Path.Combine(root, "jobs", "intake", "pending.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        await File.WriteAllBytesAsync(inside, [1]);
        await File.WriteAllBytesAsync(outside, [2]);
        try
        {
            FakePersistentIntakeStore intake = new()
            {
                RecoveryPaths = ["jobs/intake/pending.tmp", Path.GetRelativePath(root, outside)]
            };
            TelegramIntakeRecoveryService service = new(intake, root);

            await service.StartAsync(CancellationToken.None);

            Assert.False(File.Exists(inside));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task TwoVideosAndInstructionWaitForDoneBeforeCompletingIntake()
    {
        FakeDownloader downloader = new();
        FakePersistentIntakeStore intake = new();
        TelegramUpdateHandler handler = new(
            new TelegramOptions { AllowedUserId = 123456789 },
            new LimitsOptions { MaxInputFileBytes = 524_288_000 },
            downloader,
            new FakeArtifactStore(),
            intake,
            new FakeResponder());

        await handler.HandleAsync(new TelegramUpdate(123456789, 123456789, null,
            new TelegramMediaInput("file-a", "unique-a", 100, "video/mp4", "a.mp4")) { UpdateId = 10 }, CancellationToken.None);
        await handler.HandleAsync(new TelegramUpdate(123456789, 123456789, null,
            new TelegramMediaInput("file-b", "unique-b", 100, "video/mp4", "b.mp4")) { UpdateId = 20 }, CancellationToken.None);
        await handler.HandleAsync(new TelegramUpdate(123456789, 123456789, "Собери обзор", null) { UpdateId = 30 }, CancellationToken.None);

        Assert.Equal(0, intake.CompleteCalls);
        Assert.Equal(2, intake.AcceptedArtifacts.Count);

        await handler.HandleAsync(new TelegramUpdate(123456789, 123456789, "/done", null) { UpdateId = 40 }, CancellationToken.None);

        Assert.Equal(1, intake.CompleteCalls);
    }

    [Fact]
    public async Task DownloadFailureReleasesPersistentReservation()
    {
        FakePersistentIntakeStore intake = new();
        TelegramUpdateHandler handler = new(
            new TelegramOptions { AllowedUserId = 123456789 },
            new LimitsOptions { MaxInputFileBytes = 524_288_000 },
            new RejectingDownloader(),
            new FakeArtifactStore(),
            intake,
            new FakeResponder());

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, null,
                new TelegramMediaInput("file-a", "unique-a", 100, "video/mp4", "a.mp4")) { UpdateId = 10 },
            CancellationToken.None);

        Assert.Equal(1, intake.FailureCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedDownloadedVideoIsDeletedFromWorkspace(bool artifactRegistrationRejected)
    {
        string root = Path.Combine(Path.GetTempPath(), $"telegram-rejected-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            FileCreatingDownloader downloader = new(root);
            FakePersistentIntakeStore intake = new() { RejectAcceptance = !artifactRegistrationRejected };
            TelegramUpdateHandler handler = new(
                new TelegramOptions { AllowedUserId = 123456789 },
                new LimitsOptions { MaxInputFileBytes = 1_000 },
                downloader,
                new FakeArtifactStore(artifactRegistrationRejected ? "registration rejected" : null),
                intake,
                new FakeResponder(),
                workspaceRoot: root);

            await handler.HandleAsync(
                new TelegramUpdate(123456789, 123456789, null,
                    new TelegramMediaInput("file", "unique", 100, "video/mp4", "video.mp4")) { UpdateId = 10 },
                CancellationToken.None);

            Assert.NotNull(downloader.DownloadedPath);
            Assert.False(File.Exists(downloader.DownloadedPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("auto", RequestedReelsStyle.Auto)]
    [InlineData("expert-clean", RequestedReelsStyle.ExpertClean)]
    [InlineData("dynamic-viral", RequestedReelsStyle.DynamicViral)]
    [InlineData("cinematic", RequestedReelsStyle.Cinematic)]
    [InlineData("premium-ads", RequestedReelsStyle.PremiumAds)]
    public async Task StyleCommandMapsEverySupportedName(string name, RequestedReelsStyle expected)
    {
        FakePersistentIntakeStore intake = new();
        TelegramUpdateHandler handler = CreateHandler(new FakeDownloader(), intake);

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, $"/style {name}", null) { UpdateId = 10 },
            CancellationToken.None);

        Assert.Equal(expected, intake.Style);
    }

    [Theory]
    [InlineData("4", null)]
    [InlineData("5", 5)]
    [InlineData("60", 60)]
    [InlineData("61", null)]
    [InlineData("fast", null)]
    public async Task DurationCommandAcceptsOnlyIntegerFromFiveToSixty(string value, int? expected)
    {
        FakePersistentIntakeStore intake = new();
        TelegramUpdateHandler handler = CreateHandler(new FakeDownloader(), intake);

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, $"/duration {value}", null) { UpdateId = 10 },
            CancellationToken.None);

        Assert.Equal(expected, intake.DurationSeconds);
    }

    [Fact]
    public async Task IntegrationDiagnosticsAndUserContentNeverEnterLogs()
    {
        const string sensitive = "synthetic-token private-comment private-transcript";
        CaptureLogger logger = new();
        TelegramUpdateHandler handler = new(new TelegramOptions { AllowedUserId = 123456789 }, new LimitsOptions(),
            new FakeDownloader(), new FakeArtifactStore(sensitive),
            new FakePersistentIntakeStore(), new FakeResponder(), logger);
        await handler.HandleAsync(new TelegramUpdate(123456789, 123456789, null,
            new TelegramMediaInput("file", null, 100, "video/mp4", "video.mp4")), CancellationToken.None);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, text =>
        {
            Assert.DoesNotContain("synthetic-token", text);
            Assert.DoesNotContain("private-comment", text);
            Assert.DoesNotContain("private-transcript", text);
        });
    }

    private sealed class CaptureLogger : ILogger<TelegramUpdateHandler>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception);
    }

    [Theory]
    [InlineData("application/octet-stream", "video.mp4")]
    [InlineData("video/unknown", "video.mp4")]
    [InlineData(null, "video.mp4")]
    [InlineData("video/mp4", "../../video.mp4")]
    [InlineData("video/mp4", "C:\\outside.mp4")]
    [InlineData("video/mp4", "video.exe")]
    public async Task UntrustedMediaIsRejectedBeforeDownload(string? mime, string name)
    {
        FakeDownloader downloader = new();
        await CreateHandler(downloader).HandleAsync(new TelegramUpdate(123456789, 123456789, null,
            new TelegramMediaInput("file", null, 100, mime, name)), CancellationToken.None);
        Assert.Equal(0, downloader.DownloadCalls);
    }

    [Fact]
    public async Task UnauthorizedUserCannotTriggerFileDownload()
    {
        FakeDownloader downloader = new();
        TelegramUpdateHandler handler = CreateHandler(downloader);

        await handler.HandleAsync(
            new TelegramUpdate(
                987654321,
                987654321,
                null,
                new TelegramMediaInput("telegram-file", "unique-file", 10_000, "video/mp4", "video.mp4")),
            CancellationToken.None);

        Assert.Equal(0, downloader.DownloadCalls);
    }

    [Fact]
    public async Task AllowedUserCompletesIntakeOnlyAfterVideoInstructionAndDone()
    {
        FakeDownloader downloader = new();
        FakePersistentIntakeStore intake = new();
        TelegramUpdateHandler handler = CreateHandler(downloader, intake);

        await handler.HandleAsync(
            new TelegramUpdate(
                123456789,
                123456789,
                null,
                new TelegramMediaInput("telegram-file", "unique-file", 10_000, "video/mp4", "video.mp4")) { UpdateId = 1 },
            CancellationToken.None);

        Assert.Equal(1, downloader.DownloadCalls);

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, "  ", null),
            CancellationToken.None);

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, "Сделай динамично", null) { UpdateId = 2 },
            CancellationToken.None);

        Assert.Equal("Сделай динамично", intake.Instruction);
        Assert.Equal(0, intake.CompleteCalls);

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, "/done", null) { UpdateId = 3 },
            CancellationToken.None);

        Assert.Equal(1, intake.CompleteCalls);
    }

    [Fact]
    public async Task OversizedVideoIsRejectedBeforeDownload()
    {
        FakeDownloader downloader = new();
        TelegramUpdateHandler handler = new(
            new TelegramOptions { AllowedUserId = 123456789 },
            new LimitsOptions { MaxInputFileBytes = 100 },
            downloader,
            new FakeArtifactStore(),
            new FakePersistentIntakeStore(),
            new FakeResponder());

        await handler.HandleAsync(
            new TelegramUpdate(
                123456789,
                123456789,
                null,
                new TelegramMediaInput("telegram-file", null, 101, "video/mp4", "video.mp4")),
            CancellationToken.None);

        Assert.Equal(0, downloader.DownloadCalls);
    }

    [Fact]
    public async Task CancelClearsPendingVideoAndPreventsJobCreation()
    {
        FakePersistentIntakeStore intake = new();
        TelegramUpdateHandler handler = CreateHandler(new FakeDownloader(), intake);

        await handler.HandleAsync(
            new TelegramUpdate(
                123456789,
                123456789,
                null,
                new TelegramMediaInput("telegram-file", "unique-file", 10_000, "video/mp4", "video.mp4")),
            CancellationToken.None);
        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, "/cancel", null),
            CancellationToken.None);
        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, "Комментарий после отмены", null),
            CancellationToken.None);

        Assert.Equal(1, intake.CancelCalls);
        Assert.Equal(0, intake.CompleteCalls);
    }

    [Fact]
    public async Task CancelDeletesOnlyReturnedFilesInsideWorkspace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"telegram-cancel-{Guid.NewGuid():N}");
        string inside = Path.Combine(root, "jobs", "intake", "accepted.bin");
        string outside = Path.Combine(Path.GetTempPath(), $"telegram-cancel-outside-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
        await File.WriteAllBytesAsync(inside, [1]);
        await File.WriteAllBytesAsync(outside, [2]);
        try
        {
            FakePersistentIntakeStore intake = new()
            {
                CancellationPaths = ["jobs/intake/accepted.bin", Path.GetRelativePath(root, outside)]
            };
            TelegramUpdateHandler handler = new(
                new TelegramOptions { AllowedUserId = 123456789 },
                new LimitsOptions(),
                new FakeDownloader(),
                new FakeArtifactStore(),
                intake,
                new FakeResponder(),
                workspaceRoot: root);

            await handler.HandleAsync(
                new TelegramUpdate(123456789, 123456789, "/cancel", null),
                CancellationToken.None);

            Assert.False(File.Exists(inside));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task CancelJobCommandRequestsCancellationForActiveProductionJob()
    {
        JobCreationRecord job = new(
            JobId.New(),
            VariantId.New(),
            new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
            JobProgress.Restore(JobStatus.Running, ProductionStage.Rendering, ProductionMode.Fast, ProductionMode.Fast, null),
            null)
        {
            TelegramUserId = 123456789
        };
        FakeCancellationCoordinator coordinator = new();
        FakeResponder responder = new();
        TelegramUpdateHandler handler = new(
            new TelegramOptions { AllowedUserId = 123456789 },
            new LimitsOptions(),
            new FakeDownloader(),
            new FakeArtifactStore(),
            new FakePersistentIntakeStore(),
            responder,
            cancelJobUseCase: new CancelJobUseCase(new FakeCancellationStore(job), coordinator));

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, "/cancel-job", null),
            CancellationToken.None);

        Assert.Equal(job.JobId, coordinator.CancelledJobId);
        Assert.Contains("запрошена", responder.Messages.Single());
    }

    [Fact]
    public async Task ResendCommandSendsCompletedJobWithoutRenderHost()
    {
        string path = Path.Combine(Path.GetTempPath(), $"telegram-resend-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(path, [4, 5, 6]);
        try
        {
            JobId jobId = JobId.New();
            JobCreationRecord job = new(
                jobId,
                VariantId.New(),
                new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Completed, ProductionStage.Delivery, ProductionMode.Fast, ProductionMode.Fast, null),
                null)
            {
                TelegramUserId = 123456789
            };
            FakeResponder responder = new();
            TelegramUpdateHandler handler = new(
                new TelegramOptions { AllowedUserId = 123456789 },
                new LimitsOptions(),
                new FakeDownloader(),
                new FakeArtifactStore(),
                new FakePersistentIntakeStore(),
                responder,
                repeatDeliveryUseCase: new RepeatDeliveryUseCase(
                    new RepeatStateStore(job),
                    new RepeatArtifactSource(path),
                    new RepeatDelivery(),
                    new RepeatRequestStore(),
                    new RepeatReceiptStore()));

            await handler.HandleAsync(
                new TelegramUpdate(123456789, 123456789, $"/resend {jobId}", null) { UpdateId = 77 },
                CancellationToken.None);

            Assert.Contains("отправлен повторно", responder.Messages.Single());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RemountCommandCreatesAlternativeVariantFromCompletedJob()
    {
        JobCreationRecord job = new(
            JobId.New(),
            VariantId.New(),
            new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
            JobProgress.Restore(JobStatus.Completed, ProductionStage.Delivery, ProductionMode.Fast, ProductionMode.Fast, null),
            RevisionId.New())
        {
            TelegramUserId = 123456789
        };
        FakeVariantChangeStore store = new();
        FakeResponder responder = new();
        TelegramUpdateHandler handler = new(
            new TelegramOptions { AllowedUserId = 123456789 },
            new LimitsOptions(),
            new FakeDownloader(),
            new FakeArtifactStore(),
            new FakePersistentIntakeStore(),
            responder,
            variantChangeUseCase: new RequestVariantChangeUseCase(store),
            jobStateStore: new RepeatStateStore(job));

        await handler.HandleAsync(
            new TelegramUpdate(
                123456789,
                123456789,
                $"/remount {job.JobId} Сделай короче и начни со второго фрагмента",
                null)
            {
                UpdateId = 88
            },
            CancellationToken.None);

        Assert.Equal(job.VariantId, store.Command?.SourceVariantId);
        Assert.Equal("telegram:88", store.Command?.RequestKey);
        Assert.Contains("альтернативный вариант", responder.Messages.Single());
    }

    private static TelegramUpdateHandler CreateHandler(
        FakeDownloader downloader,
        FakePersistentIntakeStore? intake = null) =>
        new(
            new TelegramOptions { AllowedUserId = 123456789 },
            new LimitsOptions { MaxInputFileBytes = 524_288_000 },
            downloader,
            new FakeArtifactStore(),
            intake ?? new FakePersistentIntakeStore(),
            new FakeResponder());

    private sealed class FakeDownloader : ITelegramFileDownloader
    {
        public int DownloadCalls { get; private set; }

        public Task<TelegramDownloadResult> DownloadAsync(
            TelegramMediaInput input,
            string relativePath,
            long reservedBytes,
            Func<long, CancellationToken, Task<long?>> tryExpandReservation,
            CancellationToken cancellationToken)
        {
            DownloadCalls++;
            return Task.FromResult<TelegramDownloadResult>(new TelegramDownloadResult.Succeeded(new TelegramDownloadedFile(
                "jobs/source/video.mp4",
                "hash",
                input.SizeBytes ?? 0)));
        }
    }

    private sealed class FakeVariantChangeStore : IVariantChangeStore
    {
        public RequestVariantChangeCommand? Command { get; private set; }

        public Task<RequestVariantChangeResult> RequestAsync(
            RequestVariantChangeCommand command,
            VariantId newVariantId,
            CancellationToken cancellationToken)
        {
            Command = command;
            return Task.FromResult<RequestVariantChangeResult>(
                new RequestVariantChangeResult.Succeeded(newVariantId, WasCreated: true));
        }
    }

    private sealed class FakeArtifactStore(string? rejection = null) : IArtifactStore
    {
        public Task<ArtifactRecord?> GetAsync(
            ArtifactId artifactId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactRecord?>(null);

        public Task<ArtifactRegistrationResult> RegisterAsync(
            ArtifactRecord record,
            CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactRegistrationResult>(
                rejection is null ? new ArtifactRegistrationResult.Created(record) : new ArtifactRegistrationResult.Rejected(rejection));

        public Task<ArtifactRecord?> FindLatestAsync(
            JobId jobId,
            string artifactType,
            CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactRecord?>(null);

        public Task<IReadOnlyList<ArtifactRecord>> ListAsync(
            JobId jobId,
            string artifactType,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtifactRecord>>([]);
    }

    private sealed class FakeResponder : ITelegramResponder
    {
        public List<string> Messages { get; } = [];

        public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken) =>
            Capture(text);

        private Task Capture(string text)
        {
            Messages.Add(text);
            return Task.CompletedTask;
        }
    }

    private sealed class RejectingDownloader : ITelegramFileDownloader
    {
        public Task<TelegramDownloadResult> DownloadAsync(
            TelegramMediaInput input,
            string relativePath,
            long reservedBytes,
            Func<long, CancellationToken, Task<long?>> tryExpandReservation,
            CancellationToken cancellationToken) =>
            Task.FromResult<TelegramDownloadResult>(
                new TelegramDownloadResult.Rejected("telegram.download_failed", "Download failed."));
    }

    private sealed class FileCreatingDownloader(string workspaceRoot) : ITelegramFileDownloader
    {
        public string? DownloadedPath { get; private set; }

        public async Task<TelegramDownloadResult> DownloadAsync(
            TelegramMediaInput input,
            string relativePath,
            long reservedBytes,
            Func<long, CancellationToken, Task<long?>> tryExpandReservation,
            CancellationToken cancellationToken)
        {
            DownloadedPath = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(DownloadedPath)!);
            await File.WriteAllBytesAsync(DownloadedPath, [1], cancellationToken);
            return new TelegramDownloadResult.Succeeded(
                new TelegramDownloadedFile(relativePath, "hash", 1, 1_000_000));
        }
    }

    private sealed class FakePersistentIntakeStore : VideoFactory.VideoProduction.Application.TelegramIntake.ITelegramIntakeStore
    {
        private readonly Guid sessionId = Guid.NewGuid();

        public List<ArtifactId> AcceptedArtifacts { get; } = [];
        public int CompleteCalls { get; private set; }
        public int FailureCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public RequestedReelsStyle? Style { get; private set; }
        public int? DurationSeconds { get; private set; }
        public ImmutableArray<string> RecoveryPaths { get; init; } = [];
        public ImmutableArray<string> CancellationPaths { get; init; } = [];
        public bool RejectAcceptance { get; init; }
        public string? Instruction { get; private set; }

        public Task<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeReservationResult> ReserveAsync(
            VideoFactory.VideoProduction.Application.TelegramIntake.IntakeReservation request,
            CancellationToken cancellationToken) =>
            Task.FromResult<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeReservationResult>(
                new VideoFactory.VideoProduction.Application.TelegramIntake.IntakeReservationResult.Reserved(
                    sessionId,
                    request.DeclaredSizeBytes ?? request.MaximumFileBytes));

        public Task<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeAcceptanceResult> AcceptAsync(
            VideoFactory.VideoProduction.Application.TelegramIntake.IntakeAcceptance request,
            CancellationToken cancellationToken)
        {
            AcceptedArtifacts.Add(request.ArtifactId);
            if (RejectAcceptance)
            {
                return Task.FromResult<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeAcceptanceResult>(
                    new VideoFactory.VideoProduction.Application.TelegramIntake.IntakeAcceptanceResult.Rejected(
                        "intake.rejected",
                        "Rejected."));
            }
            return Task.FromResult<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeAcceptanceResult>(
                new VideoFactory.VideoProduction.Application.TelegramIntake.IntakeAcceptanceResult.Accepted());
        }

        public Task FailAsync(Guid sessionId, long messageId, string errorCode, CancellationToken cancellationToken)
        {
            FailureCalls++;
            return Task.CompletedTask;
        }

        public Task<long?> TryExpandReservationAsync(Guid sessionId, long messageId, long requiredBytes, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(requiredBytes);

        public Task<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeSessionUpdateResult> SetInstructionAsync(long userId, long chatId, string instruction, CancellationToken cancellationToken)
        {
            Instruction = instruction;
            return Task.FromResult<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeSessionUpdateResult>(
                new VideoFactory.VideoProduction.Application.TelegramIntake.IntakeSessionUpdateResult.Updated(
                    CreateSession(userId, chatId)));
        }

        public Task<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeSessionUpdateResult> SetStyleAsync(long userId, long chatId, RequestedReelsStyle style, CancellationToken cancellationToken)
        {
            Style = style;
            return Task.FromResult<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeSessionUpdateResult>(
                new VideoFactory.VideoProduction.Application.TelegramIntake.IntakeSessionUpdateResult.Updated(
                    CreateSession(userId, chatId)));
        }

        public Task<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeSessionUpdateResult> SetDurationAsync(long userId, long chatId, int durationSeconds, CancellationToken cancellationToken)
        {
            DurationSeconds = durationSeconds;
            return Task.FromResult<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeSessionUpdateResult>(
                new VideoFactory.VideoProduction.Application.TelegramIntake.IntakeSessionUpdateResult.Updated(
                    CreateSession(userId, chatId)));
        }

        public Task<VideoFactory.VideoProduction.Application.TelegramIntake.PendingRecoveryResult> RecoverPendingAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VideoFactory.VideoProduction.Application.TelegramIntake.PendingRecoveryResult(RecoveryPaths));

        public Task<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeCompletionResult> CompleteAsync(
            VideoFactory.VideoProduction.Application.TelegramIntake.IntakeCompletion completion,
            CancellationToken cancellationToken)
        {
            CompleteCalls++;
            return Task.FromResult<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeCompletionResult>(
                new VideoFactory.VideoProduction.Application.TelegramIntake.IntakeCompletionResult.Created(JobId.New(), VariantId.New()));
        }

        public Task<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeCancellationResult> CancelAsync(long userId, long chatId, CancellationToken cancellationToken)
        {
            CancelCalls++;
            return Task.FromResult<VideoFactory.VideoProduction.Application.TelegramIntake.IntakeCancellationResult>(
                new VideoFactory.VideoProduction.Application.TelegramIntake.IntakeCancellationResult.Cancelled(CancellationPaths));
        }

        public Task<VideoFactory.VideoProduction.Application.TelegramIntake.TelegramIntakeSession?> GetOpenAsync(long userId, long chatId, CancellationToken cancellationToken) =>
            Task.FromResult<VideoFactory.VideoProduction.Application.TelegramIntake.TelegramIntakeSession?>(null);

        private VideoFactory.VideoProduction.Application.TelegramIntake.TelegramIntakeSession CreateSession(long userId, long chatId) =>
            new(
                sessionId,
                userId,
                chatId,
                VideoFactory.VideoProduction.Application.TelegramIntake.TelegramIntakeSessionStatus.Open,
                Instruction,
                Style ?? RequestedReelsStyle.Auto,
                DurationSeconds,
                [],
                null);
    }

    private sealed class FakeCancellationStore(JobCreationRecord job) : IJobCancellationStore
    {
        public Task<JobCreationRecord?> FindActiveAsync(long telegramUserId, CancellationToken cancellationToken) =>
            Task.FromResult<JobCreationRecord?>(job);

        public Task<bool> RequestAsync(JobCancellationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> IsRequestedAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class FakeCancellationCoordinator : IJobCancellationCoordinator
    {
        public JobId? CancelledJobId { get; private set; }

        public CancellationToken Register(JobId jobId, CancellationToken hostToken) => hostToken;

        public void Cancel(JobId jobId) => CancelledJobId = jobId;

        public void Unregister(JobId jobId) { }
    }

    private sealed class RepeatStateStore(JobCreationRecord job) : IJobStateStore
    {
        public Task<JobCreationRecord?> GetAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult<JobCreationRecord?>(job);

        public Task<JobStateTransitionResult> TryTransitionAsync(JobId jobId, JobStatus expectedStatus, ProductionStage expectedStage, JobProgress nextProgress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RepeatArtifactSource(string path) : IRepeatDeliveryArtifactSource
    {
        public Task<RepeatDeliveryArtifactResult> GetVerifiedAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
            Task.FromResult<RepeatDeliveryArtifactResult>(new RepeatDeliveryArtifactResult.Succeeded(new RepeatDeliveryArtifact(ArtifactId.New(), path)));
    }

    private sealed class RepeatDelivery : IVideoDelivery
    {
        public Task<VideoDeliveryResult> SendAsync(VideoDeliveryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(VideoDeliveryResult.Succeeded("message-1"));
    }

    private sealed class RepeatRequestStore : IRepeatDeliveryRequestStore
    {
        public Task<RepeatDeliveryRequestStart> TryStartAsync(RepeatDeliveryCommand command, CancellationToken cancellationToken) =>
            Task.FromResult<RepeatDeliveryRequestStart>(new RepeatDeliveryRequestStart.Started());

        public Task CompleteAsync(long requestId, DeliveryReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RepeatReceiptStore : IDeliveryReceiptStore
    {
        public Task AppendAsync(DeliveryReceipt receipt, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> HasSucceededAsync(JobId jobId, VariantId variantId, ArtifactId artifactId, long recipientId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
