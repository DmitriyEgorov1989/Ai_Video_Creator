namespace VideoFactory.VideoProduction.Domain.Timeline;

public readonly record struct OutputRange
{
    public int StartFrame { get; }

    public int EndFrame { get; }

    public int DurationFrames => EndFrame - StartFrame;

    public OutputRange(int startFrame, int endFrame)
    {
        if (startFrame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        }

        if (endFrame <= startFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(endFrame));
        }

        StartFrame = startFrame;
        EndFrame = endFrame;
    }

    public bool ContainsFrame(int frame) => frame >= StartFrame && frame < EndFrame;

    public bool Contains(OutputRange other) =>
        other.StartFrame >= StartFrame && other.EndFrame <= EndFrame;
}
