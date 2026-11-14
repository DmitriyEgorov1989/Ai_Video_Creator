using System.Text;

namespace VideoFactory.VideoProduction.Infrastructure.Processes;

internal sealed record BoundedProcessOutput(string Text, bool Truncated);

internal static class BoundedProcessOutputCollector
{
    public static async Task<BoundedProcessOutput> ReadAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream stored = new();
        bool truncated = false;

        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            long remaining = maxBytes - stored.Length;
            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            int bytesToStore = (int)Math.Min(remaining, bytesRead);
            await stored.WriteAsync(buffer.AsMemory(0, bytesToStore), cancellationToken);

            if (bytesToStore < bytesRead)
            {
                truncated = true;
            }
        }

        return new BoundedProcessOutput(
            Encoding.UTF8.GetString(stored.GetBuffer(), 0, checked((int)stored.Length)),
            truncated);
    }
}
