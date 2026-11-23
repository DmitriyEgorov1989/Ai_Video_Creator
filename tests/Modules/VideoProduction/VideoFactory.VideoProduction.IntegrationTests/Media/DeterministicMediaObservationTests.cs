using VideoFactory.VideoProduction.Infrastructure.Processes;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.IntegrationTests.Media;

public sealed class DeterministicMediaObservationTests
{
    [AcceptanceMediaFact]
    public async Task RepeatedObservationProducesEquivalentObjectiveMeasurements()
    {
        string root = Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!;
        SystemProcessRunner runner = new();

        MediaObservation first = await ObserveAsync(runner, root);
        MediaObservation second = await ObserveAsync(runner, root);

        Assert.InRange(Math.Abs(first.Marker.Red - second.Marker.Red), 0, 1);
        Assert.InRange(Math.Abs(first.Marker.Green - second.Marker.Green), 0, 1);
        Assert.InRange(Math.Abs(first.Marker.Blue - second.Marker.Blue), 0, 1);
        Assert.InRange(Math.Abs(first.FirstTone.FrequencyHz - second.FirstTone.FrequencyHz), 0, 1);
        Assert.InRange(Math.Abs(first.SecondTone.FrequencyHz - second.SecondTone.FrequencyHz), 0, 1);
        Assert.InRange(Math.Abs(first.FirstTone.PeakDecibels - second.FirstTone.PeakDecibels), 0, 0.1);
        Assert.InRange(Math.Abs(first.SecondTone.PeakDecibels - second.SecondTone.PeakDecibels), 0, 0.1);
        Assert.InRange(Math.Abs(first.FirstTone.RmsDecibels - second.FirstTone.RmsDecibels), 0, 0.1);
        Assert.InRange(Math.Abs(first.SecondTone.RmsDecibels - second.SecondTone.RmsDecibels), 0, 0.1);
    }

    [AcceptanceMediaFact]
    public async Task IndependentGeneratedRevisionHasEquivalentFixtureHashesAndMetadata()
    {
        string existingRoot = Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!;
        string generatedRoot = Path.Combine(Path.GetTempPath(), $"video-factory-repeat-{Guid.NewGuid():N}");
        string script = Path.Combine(FindRepositoryRoot(), "scripts", "Prepare-AcceptanceMedia.ps1");
        try
        {
            ProcessExecutionResult generated = await new SystemProcessRunner().RunAsync(
                new ProcessStartRequest(
                    "powershell",
                    ["-NoProfile", "-File", script, "-OutputDirectory", generatedRoot],
                    TimeSpan.FromMinutes(2),
                    1_048_576),
                CancellationToken.None);
            Assert.True(generated.IsSuccess, generated.ErrorMessage ?? generated.StandardError);

            MediaOracleDocument expected = await MediaOracleContract.LoadAsync(existingRoot, CancellationToken.None);
            MediaOracleDocument actual = await MediaOracleContract.LoadAsync(generatedRoot, CancellationToken.None);
            Assert.Equal(
                expected.Fixtures.OrderBy(item => item.File).ToArray(),
                actual.Fixtures.OrderBy(item => item.File).ToArray());
            Assert.Equal(expected.Markers, actual.Markers);
            Assert.Equal(expected.AudioWindows, actual.AudioWindows);
            Assert.Equal(expected.Tolerances, actual.Tolerances);
        }
        finally
        {
            if (Directory.Exists(generatedRoot)) Directory.Delete(generatedRoot, recursive: true);
        }
    }

    private static async Task<MediaObservation> ObserveAsync(
        SystemProcessRunner runner,
        string root)
    {
        RgbSample marker = await MediaMeasurements.SampleRegionAsync(
            runner,
            Path.Combine(root, "similar-take-a.mp4"),
            TimeSpan.FromSeconds(5),
            new PixelRegion(130, 260, 100, 100),
            CancellationToken.None);
        ToneMeasurement firstTone = await MediaMeasurements.MeasureToneAsync(
            runner,
            Path.Combine(root, "tones.mp4"),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        ToneMeasurement secondTone = await MediaMeasurements.MeasureToneAsync(
            runner,
            Path.Combine(root, "tones.mp4"),
            TimeSpan.FromSeconds(6),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        return new(marker, firstTone, secondTone);
    }

    private sealed record MediaObservation(
        RgbSample Marker,
        ToneMeasurement FirstTone,
        ToneMeasurement SecondTone);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ai_Video_Creator.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
