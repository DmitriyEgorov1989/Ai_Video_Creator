using VideoFactory.VideoProduction.Infrastructure.Analysis;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Analysis;

public sealed class SilenceDetectParserTests
{
    [Fact]
    public void ConvertsBeginningAndEndingSilenceToSpeechWindow()
    {
        SilenceDetectParser parser = new();

        AudioActivityResult result = parser.Parse(
            "silence_start: 0\n" +
            "silence_end: 1.250\n" +
            "silence_start: 4.500\n" +
            "silence_end: 6.000\n",
            6_000_000);

        Assert.Equal(
            new[] { new MasterRange(1_250_000, 4_500_000) },
            result.SpeechWindows);
        Assert.True(result.HasUsefulSpeech);
    }

    [Fact]
    public void TreatsUnclosedSilenceAsEndingAtFileDuration()
    {
        SilenceDetectParser parser = new();

        AudioActivityResult result = parser.Parse(
            "silence_start: 2.0\n",
            3_000_000);

        Assert.Equal(
            new[] { new MasterRange(0, 2_000_000) },
            result.SpeechWindows);
        Assert.True(result.HasUsefulSpeech);
    }
}
