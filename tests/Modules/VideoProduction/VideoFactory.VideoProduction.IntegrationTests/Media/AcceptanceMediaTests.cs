using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Infrastructure.Media;
using VideoFactory.VideoProduction.Infrastructure.Processes;

namespace VideoFactory.VideoProduction.IntegrationTests.Media;

public sealed class AcceptanceMediaTests
{
    [AcceptanceMediaFact]
    public async Task SpeechFixtureHasSupportedProfileAndFullyDecodes()
    {
        string path = FixturePath("speech.mp4");
        Assert.True(File.Exists(path), $"Prepare acceptance media first: {path}");
        IMediaProbe probe = new FfprobeMediaProbe(new SystemProcessRunner(), "ffprobe");
        MediaProbeResult result = await probe.ProbeAsync(
            new ProbeRequest(path, TimeSpan.FromSeconds(30), 1_048_576), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var metadata = Assert.IsType<VideoFactory.VideoProduction.Domain.Media.MediaMetadata>(result.Metadata);
        Assert.InRange(metadata.DurationMicroseconds, 25_000_000, 25_100_000);
        var video = Assert.Single(metadata.VideoStreams);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(360, video.Width);
        Assert.Equal(640, video.Height);
        Assert.NotEqual(true, video.IsHdr);
        Assert.Equal(30, video.AverageFrameRate?.Numerator);
        Assert.Equal(1, video.AverageFrameRate?.Denominator);
        Assert.Equal("aac", Assert.Single(metadata.AudioStreams).Codec);
        Assert.True(new MediaInputProfileValidator().Validate(path, metadata, new MediaInputProfile()).IsSupported);

        IMediaDecodeVerifier verifier = new FfmpegDecodeVerifier(new SystemProcessRunner(), "ffmpeg");
        MediaDecodeResult decoded = await verifier.VerifyAsync(
            new MediaDecodeRequest(path, TimeSpan.FromSeconds(60), 1_048_576), CancellationToken.None);
        Assert.True(decoded.IsSuccess, decoded.ErrorMessage);
    }

    [AcceptanceMediaFact]
    public async Task CorruptFixtureIsRejectedByPublicProbe()
    {
        IMediaProbe probe = new FfprobeMediaProbe(new SystemProcessRunner(), "ffprobe");
        MediaProbeResult result = await probe.ProbeAsync(
            new ProbeRequest(FixturePath("corrupt.mp4"), TimeSpan.FromSeconds(30), 1_048_576), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("process.exit_code", result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [AcceptanceMediaFact]
    public async Task UnsupportedFixtureIsRejectedForItsVideoCodec()
    {
        string path = FixturePath("unsupported.mp4");
        IMediaProbe probe = new FfprobeMediaProbe(new SystemProcessRunner(), "ffprobe");
        MediaProbeResult result = await probe.ProbeAsync(
            new ProbeRequest(path, TimeSpan.FromSeconds(30), 1_048_576), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        MediaInputProfileResult validation = new MediaInputProfileValidator().Validate(
            path, result.Metadata!, new MediaInputProfile());
        Assert.False(validation.IsSupported);
        Assert.Equal("media.video_codec_unsupported", validation.ErrorCode);
    }

    [AcceptanceMediaFact]
    public async Task NoSpeechFixtureIsSupportedAndContainsOnlySilence()
    {
        string path = FixturePath("no-speech.mp4");
        var runner = new SystemProcessRunner();
        IMediaProbe probe = new FfprobeMediaProbe(runner, "ffprobe");
        MediaProbeResult result = await probe.ProbeAsync(
            new ProbeRequest(path, TimeSpan.FromSeconds(30), 1_048_576), CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(new MediaInputProfileValidator().Validate(path, result.Metadata!, new MediaInputProfile()).IsSupported);

        // Проверяется реальный звук, а не только наличие аудиопотока.
        var silence = await runner.RunAsync(new ProcessStartRequest(
            "ffmpeg", ["-hide_banner", "-nostdin", "-i", path, "-af", "silencedetect=noise=-60dB:d=0.5", "-vn", "-f", "null", "-"],
            TimeSpan.FromSeconds(60), 1_048_576), CancellationToken.None);
        Assert.True(silence.IsSuccess, silence.ErrorMessage);
        Assert.Contains("silence_start: 0", silence.StandardError);
        Assert.Contains("silence_end: 25", silence.StandardError);
    }

    [AcceptanceMediaFact]
    public async Task ManifestMatchesActualFiles()
    {
        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath("manifest.json")));
        JsonElement files = manifest.RootElement.GetProperty("files");
        Assert.Equal(4, files.GetArrayLength());
        foreach (JsonElement file in files.EnumerateArray())
        {
            string path = FixturePath(file.GetProperty("file").GetString()!);
            Assert.Equal(file.GetProperty("sizeBytes").GetInt64(), new FileInfo(path).Length);
            await using FileStream stream = File.OpenRead(path);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            Assert.Equal(file.GetProperty("sha256").GetString(), hash);
        }
    }

    private static string FixturePath(string name) =>
        Path.Combine(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!, name);
}

// Обычные тесты не требуют системного голоса и локально подготовленных медиа.
public sealed class AcceptanceMediaFactAttribute : FactAttribute
{
    public AcceptanceMediaFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")))
        {
            Skip = "Set ACCEPTANCE_MEDIA_ROOT after running scripts/Prepare-AcceptanceMedia.ps1.";
        }
    }
}
