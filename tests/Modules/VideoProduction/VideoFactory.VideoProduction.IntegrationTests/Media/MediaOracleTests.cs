using System.Text.Json;
using System.Security.Cryptography;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Infrastructure.Media;
using VideoFactory.VideoProduction.Infrastructure.Processes;

namespace VideoFactory.VideoProduction.IntegrationTests.Media;

public sealed class MediaOracleTests
{
    [AcceptanceMediaFact]
    public async Task OracleDeclaresVersionedFixtureMatrixAndTolerances()
    {
        string rootPath = Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!;
        string path = FixturePath("media-oracle.v1.json");
        Assert.True(File.Exists(path), $"Prepare acceptance media first: {path}");

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        JsonElement root = document.RootElement;
        MediaOracleDocument typedOracle = await MediaOracleContract.LoadAsync(rootPath, CancellationToken.None);

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, typedOracle.SchemaVersion);
        Assert.NotEmpty(root.GetProperty("fixtures").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("markers").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("audioWindows").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("pauseWindows").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("safeRegions").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("repeatGroups").EnumerateArray());

        JsonElement tolerances = root.GetProperty("tolerances");
        Assert.True(tolerances.GetProperty("timeMicroseconds").GetInt64() > 0);
        Assert.True(tolerances.GetProperty("frames").GetInt32() >= 0);
        Assert.True(tolerances.GetProperty("decibels").GetDouble() >= 0);

        string[] orientations = root.GetProperty("fixtures")
            .EnumerateArray()
            .Select(fixture => fixture.GetProperty("orientation").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("portrait", orientations);
        Assert.Contains("landscape", orientations);

        int[] frameRates = root.GetProperty("fixtures")
            .EnumerateArray()
            .Select(fixture => fixture.GetProperty("fps").GetInt32())
            .Distinct()
            .ToArray();
        Assert.Contains(24, frameRates);
        Assert.Contains(30, frameRates);
        Assert.Contains(60, frameRates);

        foreach (JsonElement fixture in root.GetProperty("fixtures").EnumerateArray())
        {
            string fixturePath = FixturePath(fixture.GetProperty("file").GetString()!);
            Assert.True(File.Exists(fixturePath), $"Missing oracle fixture: {fixturePath}");
            FileInfo info = new(fixturePath);
            Assert.Equal(fixture.GetProperty("sizeBytes").GetInt64(), info.Length);
            await using FileStream stream = File.OpenRead(fixturePath);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            Assert.Equal(fixture.GetProperty("sha256").GetString(), hash);

            MediaProbeResult probed = await new FfprobeMediaProbe(new SystemProcessRunner(), "ffprobe").ProbeAsync(
                new ProbeRequest(fixturePath, TimeSpan.FromSeconds(30), 1_048_576),
                CancellationToken.None);
            Assert.True(probed.IsSuccess, probed.ErrorMessage);
            MediaMetadataAssertions.RequireVideoProfile(
                fixture.GetProperty("file").GetString()!,
                probed.Metadata!,
                fixture.GetProperty("width").GetInt32(),
                fixture.GetProperty("height").GetInt32(),
                fixture.GetProperty("fps").GetInt32(),
                fixture.GetProperty("videoCodec").GetString()!,
                fixture.GetProperty("audioCodec").GetString()!,
                fixture.GetProperty("container").GetString()!,
                fixture.GetProperty("durationMicroseconds").GetInt64(),
                fixture.GetProperty("pixelFormat").GetString()!,
                fixture.GetProperty("sampleRate").GetInt32(),
                fixture.GetProperty("channels").GetInt32());
        }
    }

    [Fact]
    public void RejectsEscapingPathsAndInvalidIntervals()
    {
        MediaOracleDocument valid = new(
            1,
            "test",
            new(300_000, 1, 2, 5, 25),
            [new("fixture.mp4", new string('a', 64), 1, "portrait", 30, 360, 640, "mov,mp4", 1_000_000, "h264", "aac", "yuv420p", 48_000, 1)],
            [new("marker", "fixture.mp4", 0, 0, "visual", new(0, 0, 1, 1), new(255, 0, 0))],
            [new("tone", "fixture.mp4", 1_000_000, 500_000, 440)],
            [new("pause", "fixture.mp4", 0, 1, null)],
            [new("speech", "fixture.mp4", 0, 1, null)],
            [new("safe", "fixture.mp4", 0, 0, 1, 1)],
            [new("repeat", ["fixture.mp4", "fixture.mp4"], 0, 1)],
            [new("case", "fixture.mp4",
                [new(0, 1, 0, 1, "red", 440), new(1, 2, 1, 2, "blue", 880)],
                new(2, 3, "yellow", 660), new(0, 1, "caption"), new(0, 0, 1, 1, "green"))]);

        InvalidDataException interval = Assert.Throws<InvalidDataException>(() =>
            MediaOracleContract.Validate(Path.GetTempPath(), valid));
        MediaOracleDocument escaping = valid with
        {
            Fixtures = [valid.Fixtures[0] with { File = "../escape.mp4" }],
            AudioWindows = [valid.AudioWindows[0] with { EndMicroseconds = 2_000_000, Fixture = "../escape.mp4" }],
            Markers = [valid.Markers[0] with { Fixture = "../escape.mp4" }],
            PauseWindows = [valid.PauseWindows[0] with { Fixture = "../escape.mp4" }],
            SafeRegions = [valid.SafeRegions[0] with { Fixture = "../escape.mp4" }],
            SpeechWindows = [valid.SpeechWindows[0] with { Fixture = "../escape.mp4" }],
            RepeatGroups = [new("repeat", ["../escape.mp4", "fixture.mp4"], 0, 1)],
            MontageCases = [valid.MontageCases[0] with { Fixture = "../escape.mp4" }],
        };
        InvalidDataException path = Assert.Throws<InvalidDataException>(() =>
            MediaOracleContract.Validate(Path.GetTempPath(), escaping));

        Assert.Contains("positive half-open interval", interval.Message, StringComparison.Ordinal);
        Assert.Contains("escapes fixture root", path.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContractSchemaIsValidJsonAndUnknownOracleFieldsAreRejected()
    {
        string repositoryRoot = FindRepositoryRoot();
        using JsonDocument schema = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(repositoryRoot, "contracts", "media-oracle.v1.schema.json")));
        Assert.Equal(
            "https://ai-video-factory.local/contracts/media-oracle.v1.schema.json",
            schema.RootElement.GetProperty("$id").GetString());

        string root = Path.Combine(Path.GetTempPath(), $"media-oracle-invalid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "media-oracle.v1.json"),
                "{\"schemaVersion\":1,\"unexpected\":true}");
            await Assert.ThrowsAsync<JsonException>(() => MediaOracleContract.LoadAsync(root, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FixturePath(string name) =>
        Path.Combine(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!, name);

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
