using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>Создаёт CFR Master через FFmpeg и публикует его атомарно.</summary>
public sealed class FfmpegMezzanineBuilder : IMezzanineBuilder
{
    private readonly IProcessRunner processRunner;
    private readonly IMediaProbe mediaProbe;
    private readonly IMediaDecodeVerifier decodeVerifier;
    private readonly string executablePath;
    private readonly IStorageBudget storageBudget;

    /// <summary>Создаёт builder.</summary>
    public FfmpegMezzanineBuilder(
        IProcessRunner processRunner,
        IMediaProbe mediaProbe,
        IMediaDecodeVerifier decodeVerifier,
        string executablePath,
        IStorageBudget? storageBudget = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(mediaProbe);
        ArgumentNullException.ThrowIfNull(decodeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.processRunner = processRunner;
        this.mediaProbe = mediaProbe;
        this.decodeVerifier = decodeVerifier;
        this.executablePath = executablePath;
        this.storageBudget = storageBudget ?? StorageBudgetRunner.Default;
    }

    /// <inheritdoc />
    public async Task<MezzanineResult> BuildAsync(
        MezzanineRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        bool requiresOutput = request.OriginalMetadata.VideoStreams.Any(video =>
            video.IsVariableFrameRate == true || video.RotationDegrees is not null and not 0);
        if (!requiresOutput) return await BuildCoreAsync(request, cancellationToken);
        long estimate = checked((long)Math.Ceiling(request.OriginalMetadata.DurationMicroseconds / 1_000_000d) * 2_600_000 + 1_048_576);
        return await StorageBudgetRunner.RunAsync(storageBudget, StorageBudgetRunner.JobDirectory(request.MasterPath), estimate,
            token => BuildCoreAsync(request, token), failure => MezzanineResult.Failed(failure.Code, failure.Message), cancellationToken);
    }

    private async Task<MezzanineResult> BuildCoreAsync(MezzanineRequest request, CancellationToken cancellationToken)
    {

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
                        "-maxrate", "20M", "-bufsize", "40M",
                        "-pix_fmt", "yuv420p",
                        "-c:a", "aac",
                        "-movflags", "+faststart",
                        "-f", "mp4",
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

            cancellationToken.ThrowIfCancellationRequested();
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
