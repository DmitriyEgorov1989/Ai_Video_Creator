using System.Threading.Channels;
using VideoFactory.VideoProduction.Application.Events;

namespace VideoFactory.Bot.Telegram;

/// <summary>
/// Передаёт новые сохранённые события между процессором Job и Telegram notifier.
/// SQLite остаётся источником истины, а канал используется только как сигнал.
/// </summary>
public sealed class InMemoryJobEventSignal : IJobEventSignal
{
    private readonly Channel<JobEventRecord> channel =
        Channel.CreateUnbounded<JobEventRecord>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    /// <inheritdoc />
    public void Publish(JobEventRecord eventRecord) => channel.Writer.TryWrite(eventRecord);

    /// <inheritdoc />
    public IAsyncEnumerable<JobEventRecord> ReadAllAsync(CancellationToken cancellationToken) =>
        channel.Reader.ReadAllAsync(cancellationToken);
}
