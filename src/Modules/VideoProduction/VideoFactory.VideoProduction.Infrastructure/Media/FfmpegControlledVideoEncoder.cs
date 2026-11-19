using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>Выполняет controlled encode через FFmpeg без shell-интерполяции.</summary>
public sealed class FfmpegControlledVideoEncoder : IControlledVideoEncoder
{
    private readonly IProcessRunner processRunner;
    private readonly string ffmpegPath;
    private readonly TimeSpan timeout;
    private readonly int maxOutputBytes;

    /// <summary>Создаёт FFmpeg encoder.</summary>
    public FfmpegControlledVideoEncoder(IProcessRunner processRunner, string ffmpegPath, TimeSpan timeout, int maxOutputBytes)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath) ? throw new ArgumentException("FFmpeg path is required.", nameof(ffmpegPath)) : ffmpegPath;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        this.timeout = timeout;
        this.maxOutputBytes = maxOutputBytes;
    }

    /// <inheritdoc />
    public async Task<ControlledEncodeResult> EncodeAsync(ControlledEncodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourcePath) || string.IsNullOrWhiteSpace(request.OutputPath))
            return new(false, "delivery_encode.path_missing", "Controlled encode paths are required.");
        if (request.VideoBitrateBitsPerSecond <= 0 || request.AudioBitrateBitsPerSecond <= 0)
            return new(false, "delivery_encode.profile_invalid", "Controlled encode bitrates must be positive.");

        ProcessExecutionResult result = await processRunner.RunAsync(new ProcessStartRequest(
            ffmpegPath,
            ["-v", "error", "-xerror", "-nostdin", "-y", "-i", request.SourcePath,
             "-map", "0:v:0", "-map", "0:a:0?", "-c:v", "libx264",
             "-b:v", $"{request.VideoBitrateBitsPerSecond / 1000}k",
             "-maxrate", $"{request.VideoBitrateBitsPerSecond / 1000}k",
             "-bufsize", $"{request.VideoBitrateBitsPerSecond / 500}k",
             "-pix_fmt", "yuv420p", "-c:a", "aac",
             "-b:a", $"{request.AudioBitrateBitsPerSecond / 1000}k", "-ar", "48000", "-ac", "2",
             "-movflags", "+faststart", request.OutputPath], timeout, maxOutputBytes), cancellationToken);

        if (!result.IsSuccess)
            return new(false, result.ErrorCode ?? "delivery_encode.process_failed", result.ErrorMessage ?? result.StandardError);
        if (!File.Exists(request.OutputPath) || new FileInfo(request.OutputPath).Length <= 0)
            return new(false, "delivery_encode.output_missing", "FFmpeg did not create a non-empty output file.");
        return new(true, null, null);
    }
}
