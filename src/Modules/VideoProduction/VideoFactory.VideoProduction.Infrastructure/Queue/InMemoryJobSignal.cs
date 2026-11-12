using System.Threading.Channels;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Infrastructure.Queue;

/// <summary>
/// Передаёт сигнал о новой работе через bounded Channel.
/// </summary>
public sealed class InMemoryJobSignal : IJobSignal
{
    private readonly Channel<JobId> channel = Channel.CreateBounded<JobId>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    /// <inheritdoc />
    public ValueTask PublishAsync(JobId jobId, CancellationToken cancellationToken) =>
        channel.Writer.WriteAsync(jobId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        channel.Reader.WaitToReadAsync(cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<JobId> ReadAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}
