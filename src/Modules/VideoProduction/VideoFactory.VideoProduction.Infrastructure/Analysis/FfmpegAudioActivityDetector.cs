using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.Infrastructure.Analysis;

/// <summary>Определяет тишину и речевые окна через FFmpeg silencedetect.</summary>
public sealed class FfmpegAudioActivityDetector : IAudioActivityDetector
{
    private readonly IProcessRunner processRunner;
    private readonly string executablePath;
    private readonly SilenceDetectParser parser;
    private readonly TimeSpan timeout;
    private readonly int maxOutputBytes;

    /// <summary>Создаёт FFmpeg detector.</summary>
    public FfmpegAudioActivityDetector(
        IProcessRunner processRunner,
        string executablePath,
        TimeSpan timeout,
        int maxOutputBytes,
        SilenceDetectParser? parser = null)
    {
        this.processRunner = processRunner;
        this.executablePath = executablePath;
        this.timeout = timeout;
        this.maxOutputBytes = maxOutputBytes;
        this.parser = parser ?? new SilenceDetectParser();
    }

    /// <inheritdoc />
    public async Task<AudioActivityResult> DetectAsync(
        AudioActivityRequest request,
        CancellationToken cancellationToken)
    {
        ProcessExecutionResult result = await processRunner.RunAsync(
            new ProcessStartRequest(
                executablePath,
                ["-v", "error", "-i", request.FilePath, "-af", "silencedetect=noise=-35dB:d=0.35", "-f", "null", "-"],
                timeout,
                maxOutputBytes),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return new AudioActivityResult(
                false,
                [],
                [],
                result.ErrorCode ?? "analysis.audio_activity_failed",
                result.ErrorMessage ?? "FFmpeg silence detection failed.");
        }

        return parser.Parse(result.StandardError, request.DurationMicroseconds);
    }
}
