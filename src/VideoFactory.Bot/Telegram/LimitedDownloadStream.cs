using VideoFactory.VideoProduction.Application.Storage;

namespace VideoFactory.Bot.Telegram;

internal sealed class LimitedDownloadStream(Stream output, long limit, IStorageBudget budget, string directory) : Stream
{
    private long written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => written;
    public override long Position { get => written; set => throw new NotSupportedException(); }
    public override void Flush() => output.Flush();
    public override Task FlushAsync(CancellationToken token) => output.FlushAsync(token);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Only asynchronous download is supported.");
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        WriteAsync(buffer.AsMemory(offset, count), token).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length > limit - written) throw new IOException("Upload exceeds the configured byte limit.");
        StorageBudgetFailure? failure = await budget.CheckAsync(directory, buffer.Length, cancellationToken);
        if (failure is not null) throw new IOException(failure.Code);
        await output.WriteAsync(buffer, cancellationToken);
        written += buffer.Length;
    }
}
