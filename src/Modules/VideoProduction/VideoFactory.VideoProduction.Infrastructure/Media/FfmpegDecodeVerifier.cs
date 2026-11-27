using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>Проверяет полный decode через FFmpeg.</summary>
public sealed class FfmpegDecodeVerifier : IMediaDecodeVerifier
{
    private readonly IProcessRunner processRunner;
    private readonly string executablePath;

    /// <summary>Создаёт verifier.</summary>
    public FfmpegDecodeVerifier(IProcessRunner processRunner, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.processRunner = processRunner;
        this.executablePath = executablePath;
    }

    /// <inheritdoc />
    public async Task<MediaDecodeResult> VerifyAsync(
        MediaDecodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!File.Exists(request.FilePath))
        {
            return MediaDecodeResult.Failed(
                "media.file_missing", "The media file does not exist.");
        }

        ProcessExecutionResult result = await processRunner.RunAsync(
            new ProcessStartRequest(
                executablePath,
                [
                    "-nostdin",
                    "-v", "error",
                    "-xerror",
                    "-i", request.FilePath,
                    "-map", "0:v:0",
                    "-map", "0:a:0?",
                    "-f", "null",
                    "-"
                ],
                request.Timeout,
                request.MaxOutputBytes),
            cancellationToken);

        return result.IsSuccess
            ? MediaDecodeResult.Succeeded()
            : MediaDecodeResult.Failed(
                result.ErrorCode ?? "media.decode_failed",
                result.ErrorMessage ?? "FFmpeg could not fully decode the media file.");
    }
}
