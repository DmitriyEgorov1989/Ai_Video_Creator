using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>Создаёт CFR Master через FFmpeg и публикует его атомарно.</summary>
public sealed class FfmpegMezzanineBuilder : IMezzanineBuilder
{
    private readonly IProcessRunner processRunner;
    private readonly IMediaProbe mediaProbe;
    private readonly IMediaDecodeVerifier decodeVerifier;
    private readonly string executablePath;

    /// <summary>Создаёт builder.</summary>
    public FfmpegMezzanineBuilder(
        IProcessRunner processRunner,
        IMediaProbe mediaProbe,
        IMediaDecodeVerifier decodeVerifier,
        string executablePath)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(mediaProbe);
        ArgumentNullException.ThrowIfNull(decodeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.processRunner = processRunner;
        this.mediaProbe = mediaProbe;
        this.decodeVerifier = decodeVerifier;
        this.executablePath = executablePath;
    }

    /// <inheritdoc />
    public async Task<MezzanineResult> BuildAsync(
        MezzanineRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool needsNormalization = request.OriginalMetadata.VideoStreams.Any(video =>
            video.IsVariableFrameRate == true || video.RotationDegrees is not null and not 0);

        string masterDirectory = Path.GetDirectoryName(request.MasterPath)
            ?? throw new ArgumentException("Master path must include a directory.", nameof(request));
        Directory.CreateDirectory(masterDirectory);

        if (!needsNormalization)
        {
            TimelineMapping mapping = CreateMapping(request);
            return MezzanineResult.Succeeded(request.OriginalPath, false, mapping);
        }

        string temporaryPath = Path.Combine(
            masterDirectory,
            $".{Path.GetFileName(request.MasterPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            ProcessExecutionResult processResult = await processRunner.RunAsync(
                new ProcessStartRequest(
                    executablePath,
                    [
                        "-y", "-i", request.OriginalPath,
                        "-map", "0:v:0",
                        "-map", "0:a:0?",
                        "-vf", "setpts=PTS-STARTPTS",
                        "-r", "30",
                        "-c:v", "libx264",
                        "-pix_fmt", "yuv420p",
                        "-c:a", "aac",
                        "-movflags", "+faststart",
                        temporaryPath
                    ],
                    request.NormalizeTimeout,
                    request.MaxOutputBytes),
                cancellationToken);

            if (!processResult.IsSuccess || !File.Exists(temporaryPath))
            {
                return MezzanineResult.Failed(
                    processResult.ErrorCode ?? "media.normalize_failed",
                    processResult.ErrorMessage ?? "FFmpeg did not create the Master file.");
            }

            MediaProbeResult probeResult = await mediaProbe.ProbeAsync(
                new ProbeRequest(temporaryPath, request.ProbeTimeout, request.MaxOutputBytes),
                cancellationToken);
            if (!probeResult.IsSuccess)
            {
                return MezzanineResult.Failed(
                    probeResult.ErrorCode ?? "media.master_probe_failed",
                    probeResult.ErrorMessage ?? "The generated Master failed FFprobe.");
            }

            MediaDecodeResult decodeResult = await decodeVerifier.VerifyAsync(
                new MediaDecodeRequest(temporaryPath, request.DecodeTimeout, request.MaxOutputBytes),
                cancellationToken);
            if (!decodeResult.IsSuccess)
            {
                return MezzanineResult.Failed(
                    decodeResult.ErrorCode ?? "media.master_decode_failed",
                    decodeResult.ErrorMessage ?? "The generated Master failed full decode.");
            }

            File.Move(temporaryPath, request.MasterPath, overwrite: false);
            return MezzanineResult.Succeeded(
                request.MasterPath,
                true,
                CreateMapping(request, probeResult.Metadata!.DurationMicroseconds));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static TimelineMapping CreateMapping(
        MezzanineRequest request,
        long? masterDuration = null) =>
        new(
            request.OriginalArtifactId,
            request.MasterArtifactId,
            0,
            request.OriginalMetadata.DurationMicroseconds,
            0,
            masterDuration ?? request.OriginalMetadata.DurationMicroseconds);
}
