namespace VideoFactory.VideoProduction.IntegrationTests.Media;

public sealed class MontageQualityAssertionTests
{
    [Fact]
    public void RejectsBlackFrameAndMissingExpectedColor()
    {
        InvalidDataException black = Assert.Throws<InvalidDataException>(() =>
            MontageQualityAssertions.RequireNonBlack("transition", new RgbSample(2, 3, 2), minimumLuminance: 10));
        InvalidDataException color = Assert.Throws<InvalidDataException>(() =>
            MontageQualityAssertions.RequireDominantColor("first clip", new RgbSample(20, 10, 180), DominantColor.Red));
        InvalidDataException leakedAudio = Assert.Throws<InvalidDataException>(() =>
            MontageQualityAssertions.RequireTone("removed source interval", new ToneMeasurement(660, -10, -13), 880, 5));
        InvalidDataException unsafeCrop = Assert.Throws<InvalidDataException>(() =>
            MontageQualityAssertions.RequireRgbNear("crop-safe marker", new(10, 10, 10), new(0, 255, 0), 25));
        InvalidDataException caption = Assert.Throws<InvalidDataException>(() =>
            MontageQualityAssertions.RequireOverlayContrast("caption window", new(200, 0, 0), new(195, 0, 0), 20));

        Assert.Contains("transition", black.Message, StringComparison.Ordinal);
        Assert.Contains("first clip", color.Message, StringComparison.Ordinal);
        Assert.Contains("removed source interval", leakedAudio.Message, StringComparison.Ordinal);
        Assert.Contains("crop-safe marker", unsafeCrop.Message, StringComparison.Ordinal);
        Assert.Contains("caption window", caption.Message, StringComparison.Ordinal);
    }
}
