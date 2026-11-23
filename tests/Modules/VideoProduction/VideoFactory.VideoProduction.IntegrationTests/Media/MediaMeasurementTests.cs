using VideoFactory.VideoProduction.Infrastructure.Processes;

namespace VideoFactory.VideoProduction.IntegrationTests.Media;

public sealed class MediaMeasurementTests
{
    [AcceptanceMediaFact]
    public async Task MeasuresVisualMarkerAndOrderedAudioTones()
    {
        SystemProcessRunner runner = new();
        string root = Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!;
        MediaOracleDocument oracle = await MediaOracleContract.LoadAsync(root, CancellationToken.None);
        MediaOracleMarker markerOracle = Assert.Single(oracle.Markers, marker => marker.Id == "similar-take-a");
        MediaOracleAudioWindow firstToneOracle = Assert.Single(oracle.AudioWindows, window => window.Id == "tone-window-one");
        MediaOracleAudioWindow secondToneOracle = Assert.Single(oracle.AudioWindows, window => window.Id == "tone-window-two");

        RgbSample marker = await MediaMeasurements.SampleRegionAsync(
            runner,
            Path.Combine(root, markerOracle.Fixture),
            TimeSpan.FromTicks(markerOracle.SourceTimeMicroseconds * 10),
            markerOracle.Region,
            CancellationToken.None);
        ToneMeasurement firstTone = await MediaMeasurements.MeasureToneAsync(
            runner,
            Path.Combine(root, firstToneOracle.Fixture),
            TimeSpan.FromTicks((firstToneOracle.StartMicroseconds + 1_000_000) * 10),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        ToneMeasurement secondTone = await MediaMeasurements.MeasureToneAsync(
            runner,
            Path.Combine(root, secondToneOracle.Fixture),
            TimeSpan.FromTicks((secondToneOracle.StartMicroseconds + 1_000_000) * 10),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        MontageQualityAssertions.RequireRgbNear(
            markerOracle.Id, marker, markerOracle.ExpectedRgb, oracle.Tolerances.ColorChannel);
        MontageQualityAssertions.RequireTone(
            firstToneOracle.Id, firstTone, firstToneOracle.ExpectedFrequencyHz!.Value, oracle.Tolerances.FrequencyHz);
        MontageQualityAssertions.RequireTone(
            secondToneOracle.Id, secondTone, secondToneOracle.ExpectedFrequencyHz!.Value, oracle.Tolerances.FrequencyHz);
        Assert.True(firstTone.PeakDecibels > -30);
        Assert.True(secondTone.PeakDecibels > -30);
        Assert.True(firstTone.RmsDecibels > -30);
        Assert.True(secondTone.RmsDecibels > -30);
    }
}
