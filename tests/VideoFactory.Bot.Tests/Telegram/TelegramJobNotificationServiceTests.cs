using VideoFactory.Bot.Configuration;
using VideoFactory.Bot.Telegram;
using VideoFactory.VideoProduction.Application.Events;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.Bot.Tests.Telegram;

public sealed class TelegramJobNotificationServiceTests
{
    [Fact]
    public async Task RepeatedStageEventsWithinConfiguredIntervalProduceOneMessage()
    {
        JobId jobId = JobId.New();
        FakeClock clock = new(DateTimeOffset.Parse("2026-11-20T10:00:00Z"));
        FakeResponder responder = new();
        TelegramJobNotificationService service = new(
            new TelegramOptions { AllowedUserId = 123456789, ProgressMessageIntervalSeconds = 5 },
            new FakeJobStateStore(CreateJob(jobId)),
            responder,
            clock);

        await service.NotifyAsync(Event(jobId, "job.stage_changed", ProductionStage.Analysis), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(4));
        await service.NotifyAsync(Event(jobId, "job.stage_changed", ProductionStage.Rendering), CancellationToken.None);

        Assert.Single(responder.Messages);
        Assert.Contains("анализ", responder.Messages[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FinalFailureUsesUserSafeMessage()
    {
        JobId jobId = JobId.New();
        FakeResponder responder = new();
        TelegramJobNotificationService service = new(
            new TelegramOptions { AllowedUserId = 123456789 },
            new FakeJobStateStore(CreateJob(jobId) with
            {
                Progress = JobProgress.Restore(JobStatus.Failed, ProductionStage.Preparation, ProductionMode.Fast, ProductionMode.Fast, null)
            }),
            responder);

        await service.NotifyAsync(Event(jobId, "job.failed", ProductionStage.Preparation), CancellationToken.None);

        Assert.Equal("Не удалось обработать видео. Проверьте исходный файл и попробуйте ещё раз.", responder.Messages.Single());
    }

    private static JobCreationRecord CreateJob(JobId jobId) => new(
        jobId,
        VariantId.New(),
        new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
        JobProgress.Restore(JobStatus.Running, ProductionStage.Analysis, ProductionMode.Fast, ProductionMode.Fast, null),
        null)
    {
        TelegramUserId = 123456789
    };

    private static JobEventRecord Event(JobId jobId, string type, ProductionStage stage) => new(
        Guid.NewGuid(), jobId, VariantId.New(), type, stage, null, DateTimeOffset.UtcNow);

    private sealed class FakeResponder : ITelegramResponder
    {
        public List<string> Messages { get; } = [];

        public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            Messages.Add(text);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeJobStateStore(JobCreationRecord job) : IJobStateStore
    {
        public Task<JobCreationRecord?> GetAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult<JobCreationRecord?>(job.JobId == jobId ? job : null);

        public Task<JobStateTransitionResult> TryTransitionAsync(
            JobId jobId,
            JobStatus expectedStatus,
            ProductionStage expectedStage,
            JobProgress nextProgress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeClock(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
