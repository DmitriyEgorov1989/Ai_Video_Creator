using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>Выполняет технический QA итогового MP4.</summary>
public sealed class OutputQualityAssurance : IOutputQualityAssurance
{
    private readonly IMediaProbe mediaProbe;
    private readonly IMediaDecodeVerifier decodeVerifier;
    private readonly TimeSpan defaultProbeTimeout;
    private readonly int defaultMaxOutputBytes;

    /// <summary>Создаёт проверку итогового MP4.</summary>
    public OutputQualityAssurance(
        IMediaProbe mediaProbe,
        IMediaDecodeVerifier decodeVerifier,
        TimeSpan defaultProbeTimeout,
        int defaultMaxOutputBytes)
    {
        this.mediaProbe = mediaProbe ?? throw new ArgumentNullException(nameof(mediaProbe));
        this.decodeVerifier = decodeVerifier ?? throw new ArgumentNullException(nameof(decodeVerifier));
        if (defaultProbeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(defaultProbeTimeout));
        if (defaultMaxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(defaultMaxOutputBytes));
        this.defaultProbeTimeout = defaultProbeTimeout;
        this.defaultMaxOutputBytes = defaultMaxOutputBytes;
    }

    /// <inheritdoc />
    public async Task<OutputQualityResult> VerifyAsync(
        OutputQualityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            return OutputQualityResult.Failed("media.file_path_missing", "The output file path is required.");
        }

        FileInfo file = new(request.FilePath);
        if (!file.Exists)
        {
            return OutputQualityResult.Failed("media.file_missing", "The output file does not exist.");
        }

        if (file.Length <= 0)
        {
            return OutputQualityResult.Failed("media.file_empty", "The output file is empty.");
        }

        MediaProbeResult probe = await mediaProbe.ProbeAsync(
            new ProbeRequest(
                request.FilePath,
                request.ProbeTimeout ?? defaultProbeTimeout,
                request.MaxOutputBytes ?? defaultMaxOutputBytes),
            cancellationToken);
        if (!probe.IsSuccess)
        {
            return OutputQualityResult.Failed(
                probe.ErrorCode ?? "media.probe_failed",
                probe.ErrorMessage ?? "FFprobe could not read the output file.");
        }

        MediaMetadata metadata = probe.Metadata!;
        if (!metadata.Container.Split(',').Any(value => value.Equals("mp4", StringComparison.OrdinalIgnoreCase)))
        {
            return OutputQualityResult.Failed("media.container_not_mp4", "The output container is not MP4.");
        }

        if (metadata.VideoStreams.Length != 1)
        {
            return OutputQualityResult.Failed(
                "media.video_stream_count_invalid",
                "The output must contain exactly one video stream.");
        }

        if (request.RequireAudio && metadata.AudioStreams.Length == 0)
        {
            return OutputQualityResult.Failed(
                "media.audio_stream_missing",
                "The output must contain an audio stream.");
        }

        VideoStreamMetadata video = metadata.VideoStreams[0];
        if (video.Width != request.ExpectedWidth || video.Height != request.ExpectedHeight)
        {
            return OutputQualityResult.Failed(
                "media.video_dimensions_mismatch",
                $"Expected {request.ExpectedWidth}x{request.ExpectedHeight}, got {video.Width}x{video.Height}.");
        }

        if (video.AverageFrameRate is not FrameRate actualFrameRate || actualFrameRate != request.ExpectedFrameRate)
        {
            return OutputQualityResult.Failed(
                "media.frame_rate_mismatch",
                "The output frame rate does not match the configured profile.");
        }

        if (request.ExpectedPixelFormat is not null &&
            !string.Equals(CanonicalPixelFormat(video.PixelFormat), CanonicalPixelFormat(request.ExpectedPixelFormat), StringComparison.OrdinalIgnoreCase))
        {
            return OutputQualityResult.Failed(
                "media.pixel_format_mismatch",
                $"Expected pixel format '{request.ExpectedPixelFormat}', got '{video.PixelFormat ?? "unknown"}'.");
        }

        int durationFrames = checked((int)Math.Round(
            metadata.DurationMicroseconds / 1_000_000d * request.ExpectedFrameRate.Numerator / request.ExpectedFrameRate.Denominator,
            MidpointRounding.AwayFromZero));
        if (Math.Abs(durationFrames - request.ExpectedDurationFrames) > request.DurationToleranceFrames)
        {
            return OutputQualityResult.Failed(
                "media.duration_mismatch",
                $"Expected {request.ExpectedDurationFrames} frames, got {durationFrames}.");
        }

        MediaDecodeResult decode = await decodeVerifier.VerifyAsync(
            new MediaDecodeRequest(
                request.FilePath,
                request.ProbeTimeout ?? defaultProbeTimeout,
                request.MaxOutputBytes ?? defaultMaxOutputBytes),
            cancellationToken);
        if (!decode.IsSuccess)
        {
            return OutputQualityResult.Failed(
                decode.ErrorCode ?? "media.decode_failed",
                decode.ErrorMessage ?? "FFmpeg could not fully decode the output file.");
        }

        return OutputQualityResult.Succeeded(video.Width, video.Height, durationFrames);
    }

    private static string? CanonicalPixelFormat(string? pixelFormat) =>
        string.Equals(pixelFormat, "yuvj420p", StringComparison.OrdinalIgnoreCase)
            ? "yuv420p"
            : pixelFormat;
}
