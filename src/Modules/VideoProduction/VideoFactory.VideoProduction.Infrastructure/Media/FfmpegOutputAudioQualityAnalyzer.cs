using System.Globalization;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>Измеряет громкость итогового audio stream через диагностический вывод FFmpeg loudnorm.</summary>
public sealed class FfmpegOutputAudioQualityAnalyzer : IOutputAudioQualityAnalyzer
{
    private readonly IProcessRunner processRunner;
    private readonly string executablePath;

    /// <summary>Создаёт анализатор итоговой громкости.</summary>
    /// <param name="processRunner">Безопасный запускатель внешнего процесса.</param>
    /// <param name="executablePath">Путь или имя FFmpeg.</param>
    public FfmpegOutputAudioQualityAnalyzer(IProcessRunner processRunner, string executablePath)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.executablePath = executablePath;
    }

    /// <inheritdoc />
    public async Task<OutputAudioQualityResult> AnalyzeAsync(
        OutputAudioQualityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProcessExecutionResult result = await processRunner.RunAsync(
            new ProcessStartRequest(
                executablePath,
                [
                    "-v", "info",
                    "-i", request.FilePath,
                    "-map", "0:a:0",
                    "-af", "loudnorm=I=-16:TP=-1:LRA=11:print_format=json",
                    "-f", "null",
                    "-"
                ],
                request.Timeout,
                request.MaxOutputBytes),
            cancellationToken);
        if (!result.IsSuccess)
        {
            return OutputAudioQualityResult.Failed(
                result.ErrorCode ?? "media.audio_quality_measurement_failed",
                result.ErrorMessage ?? "FFmpeg could not measure final audio quality.");
        }

        try
        {
            int start = result.StandardError.LastIndexOf('{');
            int end = result.StandardError.LastIndexOf('}');
            if (start < 0 || end <= start) throw new JsonException("loudnorm JSON was not found.");
            using JsonDocument document = JsonDocument.Parse(result.StandardError[start..(end + 1)]);
            decimal loudness = decimal.Parse(
                document.RootElement.GetProperty("input_i").GetString()!, CultureInfo.InvariantCulture);
            decimal truePeak = decimal.Parse(
                document.RootElement.GetProperty("input_tp").GetString()!, CultureInfo.InvariantCulture);
            return OutputAudioQualityResult.Succeeded(loudness, truePeak);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            return OutputAudioQualityResult.Failed(
                "media.audio_quality_output_invalid",
                $"FFmpeg returned invalid loudness measurements: {exception.Message}");
        }
    }
}
