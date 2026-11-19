using VideoFactory.Bot.Configuration;
using VideoFactory.Bot.Telegram;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.Cancellation;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using Microsoft.Extensions.Logging;

namespace VideoFactory.Bot.Tests.Telegram;

public sealed class TelegramUpdateHandlerTests
{
    [Fact]
    public async Task IntegrationDiagnosticsAndUserContentNeverEnterLogs()
    {
        const string sensitive = "synthetic-token private-comment private-transcript";
        CaptureLogger logger = new();
        TelegramUpdateHandler handler = new(new TelegramOptions { AllowedUserId = 123456789 }, new LimitsOptions(),
            new FakeDownloader(), new FakeArtifactStore(sensitive), new FakeCreateJobUseCase(),
            new InMemoryTelegramIntakeSessionStore(), new FakeResponder(), logger);
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
    public async Task AllowedUserCreatesJobOnlyAfterVideoAndNonEmptyComment()
    {
        FakeDownloader downloader = new();
        FakeCreateJobUseCase createJob = new();
        TelegramUpdateHandler handler = CreateHandler(downloader, createJob);

        await handler.HandleAsync(
            new TelegramUpdate(
                123456789,
                123456789,
                null,
                new TelegramMediaInput("telegram-file", "unique-file", 10_000, "video/mp4", "video.mp4")),
            CancellationToken.None);

        Assert.Equal(1, downloader.DownloadCalls);
        Assert.Null(createJob.Command);

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, "  ", null),
            CancellationToken.None);
        Assert.Null(createJob.Command);

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, "Сделай динамично", null),
            CancellationToken.None);

        Assert.NotNull(createJob.Command);
        Assert.Equal("Сделай динамично", createJob.Command.Comment);
        Assert.True(createJob.Command.Settings!.CaptionsEnabled);
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
            new FakeCreateJobUseCase(),
            new InMemoryTelegramIntakeSessionStore(),
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
        FakeCreateJobUseCase createJob = new();
        TelegramUpdateHandler handler = CreateHandler(new FakeDownloader(), createJob);

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

        Assert.Null(createJob.Command);
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
            new FakeCreateJobUseCase(),
            new InMemoryTelegramIntakeSessionStore(),
            responder,
            cancelJobUseCase: new CancelJobUseCase(new FakeCancellationStore(job), coordinator));

        await handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, "/cancel-job", null),
            CancellationToken.None);

        Assert.Equal(job.JobId, coordinator.CancelledJobId);
        Assert.Contains("запрошена", responder.Messages.Single());
    }

    private static TelegramUpdateHandler CreateHandler(
        FakeDownloader downloader,
        FakeCreateJobUseCase? createJob = null) =>
        new(
            new TelegramOptions { AllowedUserId = 123456789 },
            new LimitsOptions { MaxInputFileBytes = 524_288_000 },
            downloader,
            new FakeArtifactStore(),
            createJob ?? new FakeCreateJobUseCase(),
            new InMemoryTelegramIntakeSessionStore(),
            new FakeResponder());

    private sealed class FakeDownloader : ITelegramFileDownloader
    {
        public int DownloadCalls { get; private set; }

        public Task<TelegramDownloadResult> DownloadAsync(
            TelegramMediaInput input,
            CancellationToken cancellationToken)
        {
            DownloadCalls++;
            return Task.FromResult<TelegramDownloadResult>(new TelegramDownloadResult.Succeeded(new TelegramDownloadedFile(
                "jobs/source/video.mp4",
                "hash",
                input.SizeBytes ?? 0)));
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
    }

    private sealed class FakeCreateJobUseCase : ICreateJobUseCase
    {
        public CreateJobCommand? Command { get; private set; }

        public Task<CreateJobResult> ExecuteAsync(
            CreateJobCommand command,
            CancellationToken cancellationToken) =>
            Capture(command);

        private Task<CreateJobResult> Capture(CreateJobCommand command)
        {
            Command = command;
            return Task.FromResult<CreateJobResult>(new CreateJobResult.Succeeded(
                JobId.New(),
                VariantId.New(),
                WasCreated: true));
        }
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
}
