using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Timeline;

public sealed class TimelineTests
{
    [Fact]
    public void RangesUsePositiveHalfOpenIntervals()
    {
        OriginalRange original = new(1_000_000, 4_000_000);
        MasterRange master = new(0, 3_000_000);
        OutputRange output = new(0, 90);

        Assert.Equal(3_000_000, original.DurationMicroseconds);
        Assert.Equal(3_000_000, master.DurationMicroseconds);
        Assert.Equal(90, output.DurationFrames);
        Assert.True(output.ContainsFrame(89));
        Assert.False(output.ContainsFrame(90));
    }

    [Fact]
    public void EmptyRangesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OriginalRange(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MasterRange(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutputRange(1, 1));
    }

    [Fact]
    public void FrameRateIsReducedToCanonicalForm()
    {
        Assert.Equal(new FrameRate(30, 1), new FrameRate(60, 2));
    }

    [Theory]
    [InlineData(3_000_000, 30, 1, 90)]
    [InlineData(1_001_000, 30_000, 1_001, 30)]
    public void DurationConvertsToNearestFrame(
        long microseconds,
        int numerator,
        int denominator,
        int expectedFrames)
    {
        int frames = TimelineMath.ToNearestFrameCount(
            microseconds,
            new FrameRate(numerator, denominator));

        Assert.Equal(expectedFrames, frames);
    }
}
