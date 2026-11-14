using VideoFactory.Bot.Configuration;
using VideoFactory.Bot.Telegram;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.Bot.Tests.Telegram;

public sealed class TelegramUpdateHandlerTests
{
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

        public Task<TelegramDownloadedFile> DownloadAsync(
            TelegramMediaInput input,
            CancellationToken cancellationToken)
        {
            DownloadCalls++;
            return Task.FromResult(new TelegramDownloadedFile(
                "jobs/source/video.mp4",
                "hash",
                input.SizeBytes ?? 0));
        }
    }

    private sealed class FakeArtifactStore : IArtifactStore
    {
        public Task<ArtifactRegistrationResult> RegisterAsync(
            ArtifactRecord record,
            CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactRegistrationResult>(
                new ArtifactRegistrationResult.Created(record));
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
        public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
