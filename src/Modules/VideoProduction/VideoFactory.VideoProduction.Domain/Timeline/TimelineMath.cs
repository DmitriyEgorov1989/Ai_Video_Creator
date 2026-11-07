namespace VideoFactory.VideoProduction.Domain.Timeline;

public static class TimelineMath
{
    public static int ToNearestFrameCount(long durationMicroseconds, FrameRate frameRate)
    {
        if (durationMicroseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMicroseconds));
        }

        if (!frameRate.IsValid)
        {
            throw new ArgumentException("Frame rate must be valid.", nameof(frameRate));
        }

        Int128 numerator = (Int128)durationMicroseconds * frameRate.Numerator;
        Int128 denominator = (Int128)1_000_000 * frameRate.Denominator;
        return checked((int)((numerator + denominator / 2) / denominator));
    }
}
