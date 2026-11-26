using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.QualityAssurance;

namespace VideoFactory.VideoProduction.UnitTests.Critique;

public sealed class BlackDetectParserTests
{
    [Fact]
    public void ParsesBlackIntervalIntoOutputFrames()
    {
        const string diagnostic = "[blackdetect @ 000] black_start:1.000 black_end:1.500 black_duration:0.500";

        IReadOnlyList<OutputRange> intervals = new BlackDetectParser().Parse(
            diagnostic,
            new FrameRate(30, 1),
            durationFrames: 90);

        Assert.Equal(new OutputRange(30, 45), Assert.Single(intervals));
    }

    [Fact]
    public void ParsesFrozenIntervalIntoOutputFrames()
    {
        const string diagnostic = "[freezedetect @ 000] freeze_start: 1.000\n[freezedetect @ 000] freeze_end: 2.500 | freeze_duration: 1.500";

        IReadOnlyList<OutputRange> intervals = new FreezeDetectParser().Parse(
            diagnostic,
            new FrameRate(30, 1),
            durationFrames: 120);

        Assert.Equal(new OutputRange(30, 75), Assert.Single(intervals));
    }
}
