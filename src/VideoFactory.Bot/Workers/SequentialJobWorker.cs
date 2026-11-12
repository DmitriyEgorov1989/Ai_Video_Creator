using Microsoft.Extensions.Options;
using VideoFactory.Bot.Configuration;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Pipeline;

namespace VideoFactory.Bot.Workers;

/// <summary>
/// Последовательно обрабатывает Job, используя SQLite как источник истины.
/// </summary>
public sealed class SequentialJobWorker : BackgroundService
{
    private readonly SequentialJobProcessor processor;
    private readonly IJobSignal signal;
    private readonly WorkerOptions options;
    private readonly ILogger<SequentialJobWorker> logger;
    private readonly string workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}";

    /// <summary>Создаёт последовательный worker.</summary>
    public SequentialJobWorker(
        SequentialJobProcessor processor,
        IJobSignal signal,
        IOptions<WorkerOptions> options,
        ILogger<SequentialJobWorker> logger)
    {
        this.processor = processor;
        this.signal = signal;
        this.options = options.Value;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan leaseTimeout = TimeSpan.FromSeconds(options.LeaseTimeoutSeconds);
        TimeSpan pollInterval = TimeSpan.FromSeconds(options.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed = await processor.ProcessNextAsync(
                workerId,
                leaseTimeout,
                TimeSpan.FromSeconds(options.HeartbeatIntervalSeconds),
                options.MaxStageAttempts,
                stoppingToken);

            if (processed)
            {
                continue;
            }

            using CancellationTokenSource waitCts =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            waitCts.CancelAfter(pollInterval);

            try
            {
                await signal.WaitToReadAsync(waitCts.Token);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug("Job queue polling interval elapsed");
            }
        }
    }
}
