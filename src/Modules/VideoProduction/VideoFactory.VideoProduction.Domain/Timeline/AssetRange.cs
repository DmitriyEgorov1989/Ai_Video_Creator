namespace VideoFactory.VideoProduction.Domain.Timeline;

public readonly record struct AssetRange
{
    public long StartMicroseconds { get; }

    public long EndMicroseconds { get; }

    public long DurationMicroseconds => EndMicroseconds - StartMicroseconds;

    public AssetRange(long startMicroseconds, long endMicroseconds)
    {
        if (startMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startMicroseconds));
        }

        if (endMicroseconds <= startMicroseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(endMicroseconds));
        }

        StartMicroseconds = startMicroseconds;
        EndMicroseconds = endMicroseconds;
    }
}
