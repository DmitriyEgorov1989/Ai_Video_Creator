using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Rendering;

public sealed class RenderSizeEstimatorTests
{
    [Fact]
    public void EstimatesOutputSizeUsingEncodingProfileAndRoundsUp()
    {
        RenderSizeEstimate result = RenderSizeEstimator.Estimate(
            durationFrames: 2_700,
            frameRate: new FrameRate(30, 1),
            profile: new OutputEncodingProfile(4_000_000, 128_000, 2_097_152));

        Assert.Equal(48_537_152, result.EstimatedBytes);
        Assert.Equal(90, result.DurationSeconds);
        Assert.Equal(4_128_000, result.TotalBitrateBitsPerSecond);
    }

    [Fact]
    public void RejectsEstimateWhenProfileValuesAreInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RenderSizeEstimator.Estimate(
                durationFrames: 30,
                frameRate: new FrameRate(30, 1),
                profile: new OutputEncodingProfile(0, 128_000, 2_097_152)));
    }
}
