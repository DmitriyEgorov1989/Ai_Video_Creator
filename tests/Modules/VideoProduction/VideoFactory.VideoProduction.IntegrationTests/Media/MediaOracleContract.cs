using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoFactory.VideoProduction.IntegrationTests.Media;

internal sealed record MediaOracleDocument(
    int SchemaVersion,
    string Provenance,
    MediaOracleTolerances Tolerances,
    MediaOracleFixture[] Fixtures,
    MediaOracleMarker[] Markers,
    MediaOracleAudioWindow[] AudioWindows,
    MediaOracleAudioWindow[] PauseWindows,
    MediaOracleAudioWindow[] SpeechWindows,
    MediaOracleSafeRegion[] SafeRegions,
    MediaOracleRepeatGroup[] RepeatGroups,
    MediaOracleMontageCase[] MontageCases);

internal sealed record MediaOracleTolerances(
    long TimeMicroseconds, int Frames, double Decibels, double FrequencyHz, double ColorChannel);
internal sealed record MediaOracleFixture(
    string File, string Sha256, long SizeBytes, string Orientation, int Fps, int Width, int Height,
    string Container, long DurationMicroseconds, string VideoCodec, string AudioCodec, string PixelFormat,
    int SampleRate, int Channels);
internal sealed record MediaOracleMarker(string Id, string Fixture, long SourceTimeMicroseconds, int ExpectedOutputFrame, string Kind, PixelRegion Region, RgbSample ExpectedRgb);
internal sealed record MediaOracleAudioWindow(string Id, string Fixture, long StartMicroseconds, long EndMicroseconds, int? ExpectedFrequencyHz);
internal sealed record MediaOracleSafeRegion(string Id, string Fixture, int X, int Y, int Width, int Height);
internal sealed record MediaOracleRepeatGroup(string Id, string[] FixtureIds, long StartMicroseconds, long EndMicroseconds);
internal sealed record MediaOracleMontageCase(
    string Id, string Fixture, MediaOracleMontageClip[] Clips, MediaOracleRemovedInterval Removed,
    MediaOracleCaption Caption, MediaOracleCropRegion CropSafeRegion);
internal sealed record MediaOracleMontageClip(
    long SourceStartMicroseconds, long SourceEndMicroseconds, int OutputStartFrame, int OutputEndFrame,
    string ExpectedColor, int ExpectedFrequencyHz);
internal sealed record MediaOracleRemovedInterval(long SourceStartMicroseconds, long SourceEndMicroseconds, string Color, int FrequencyHz);
internal sealed record MediaOracleCaption(int StartFrame, int EndFrame, string Text);
internal sealed record MediaOracleCropRegion(int X, int Y, int Width, int Height, string ExpectedColor);

internal static class MediaOracleContract
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static async Task<MediaOracleDocument> LoadAsync(string root, CancellationToken cancellationToken)
    {
        string oraclePath = ResolveInsideRoot(root, "media-oracle.v1.json");
        await using FileStream stream = File.OpenRead(oraclePath);
        MediaOracleDocument oracle = await JsonSerializer.DeserializeAsync<MediaOracleDocument>(
            stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Media oracle is empty.");
        Validate(root, oracle);
        return oracle;
    }

    internal static void Validate(string root, MediaOracleDocument oracle)
    {
        if (oracle.SchemaVersion != 1) throw new InvalidDataException("Media oracle schemaVersion must be 1.");
        if (string.IsNullOrWhiteSpace(oracle.Provenance)) throw new InvalidDataException("Media oracle provenance is required.");
        if (oracle.Fixtures is null || oracle.Markers is null || oracle.AudioWindows is null
            || oracle.PauseWindows is null || oracle.SpeechWindows is null || oracle.SafeRegions is null
            || oracle.RepeatGroups is null || oracle.MontageCases is null)
        {
            throw new InvalidDataException("Media oracle collections are required.");
        }
        if (oracle.Tolerances.TimeMicroseconds <= 0 || oracle.Tolerances.Frames < 0 || oracle.Tolerances.Decibels < 0
            || oracle.Tolerances.FrequencyHz < 0 || oracle.Tolerances.ColorChannel < 0)
        {
            throw new InvalidDataException("Media oracle tolerances are invalid.");
        }

        RequireUnique(oracle.Fixtures.Select(item => item.File), "fixture file");
        RequireUnique(oracle.Markers.Select(item => item.Id), "marker ID");
        RequireUnique(oracle.AudioWindows.Concat(oracle.PauseWindows).Concat(oracle.SpeechWindows).Select(item => item.Id), "audio window ID");
        RequireUnique(oracle.SafeRegions.Select(item => item.Id), "safe region ID");
        RequireUnique(oracle.RepeatGroups.Select(item => item.Id), "repeat group ID");
        RequireUnique(oracle.MontageCases.Select(item => item.Id), "montage case ID");
        Dictionary<string, MediaOracleFixture> fixtures = oracle.Fixtures.ToDictionary(item => item.File, StringComparer.Ordinal);
        foreach (MediaOracleFixture fixture in oracle.Fixtures)
        {
            _ = ResolveInsideRoot(root, fixture.File);
            if (fixture.SizeBytes <= 0 || fixture.Width <= 0 || fixture.Height <= 0 || fixture.Fps <= 0
                || fixture.DurationMicroseconds <= 0 || fixture.SampleRate <= 0 || fixture.Channels <= 0
                || fixture.Sha256.Length != 64 || fixture.Sha256.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidDataException($"Fixture '{fixture.File}' has invalid metadata.");
            }
        }

        foreach (MediaOracleMarker marker in oracle.Markers)
        {
            MediaOracleFixture fixture = RequireFixture(fixtures, marker.Fixture, marker.Id);
            if (marker.SourceTimeMicroseconds < 0 || marker.ExpectedOutputFrame < 0
                || marker.SourceTimeMicroseconds >= fixture.DurationMicroseconds
                || marker.Region.X < 0 || marker.Region.Y < 0 || marker.Region.Width <= 0 || marker.Region.Height <= 0
                || marker.Region.X + marker.Region.Width > fixture.Width || marker.Region.Y + marker.Region.Height > fixture.Height)
            {
                throw new InvalidDataException($"Marker '{marker.Id}' has invalid timing or region.");
            }
        }

        foreach (MediaOracleAudioWindow window in oracle.AudioWindows.Concat(oracle.PauseWindows).Concat(oracle.SpeechWindows))
        {
            MediaOracleFixture fixture = RequireFixture(fixtures, window.Fixture, window.Id);
            if (window.StartMicroseconds < 0 || window.EndMicroseconds <= window.StartMicroseconds
                || window.EndMicroseconds > fixture.DurationMicroseconds)
            {
                throw new InvalidDataException($"Audio window '{window.Id}' must be a positive half-open interval.");
            }
        }

        foreach (MediaOracleSafeRegion region in oracle.SafeRegions)
        {
            MediaOracleFixture fixture = RequireFixture(fixtures, region.Fixture, region.Id);
            if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0)
            {
                throw new InvalidDataException($"Safe region '{region.Id}' is invalid.");
            }
        }

        foreach (MediaOracleRepeatGroup group in oracle.RepeatGroups)
        {
            if (group.FixtureIds.Length < 2) throw new InvalidDataException($"Repeat group '{group.Id}' requires two fixtures.");
            if (group.FixtureIds.Distinct(StringComparer.Ordinal).Count() != group.FixtureIds.Length)
                throw new InvalidDataException($"Repeat group '{group.Id}' contains duplicate fixtures.");
            foreach (string fixture in group.FixtureIds) _ = RequireFixture(fixtures, fixture, group.Id);
            if (group.StartMicroseconds < 0 || group.EndMicroseconds <= group.StartMicroseconds)
                throw new InvalidDataException($"Repeat group '{group.Id}' has an invalid interval.");
        }

        foreach (MediaOracleMontageCase montageCase in oracle.MontageCases)
        {
            MediaOracleFixture fixture = RequireFixture(fixtures, montageCase.Fixture, montageCase.Id);
            foreach (MediaOracleMontageClip clip in montageCase.Clips)
            {
                if (clip.SourceStartMicroseconds < 0 || clip.SourceEndMicroseconds <= clip.SourceStartMicroseconds
                    || clip.SourceEndMicroseconds > fixture.DurationMicroseconds || clip.OutputEndFrame <= clip.OutputStartFrame)
                    throw new InvalidDataException($"Montage case '{montageCase.Id}' has an invalid clip.");
            }
        }
    }

    private static string ResolveInsideRoot(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Oracle path escapes fixture root: {relativePath}");
        }

        return fullPath;
    }

    private static MediaOracleFixture RequireFixture(Dictionary<string, MediaOracleFixture> fixtures, string fixture, string owner)
    {
        if (!fixtures.TryGetValue(fixture, out MediaOracleFixture? value))
            throw new InvalidDataException($"'{owner}' references unknown fixture '{fixture}'.");
        return value;
    }

    private static void RequireUnique(IEnumerable<string> values, string description)
    {
        HashSet<string> unique = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !unique.Add(value))
            {
                throw new InvalidDataException($"Media oracle contains an empty or duplicate {description}: '{value}'.");
            }
        }
    }
}
