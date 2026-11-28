using System.Globalization;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>Нормализует итоговую аудиодорожку MP4 через FFmpeg без перекодирования видео.</summary>
public sealed class FfmpegRenderOutputNormalizer : IRenderOutputNormalizer
{
    private readonly IProcessRunner processRunner;
    private readonly string ffmpegPath;
    private readonly TimeSpan timeout;
    private readonly int maxOutputBytes;

    /// <summary>Создаёт адаптер нормализации результата рендера.</summary>
    public FfmpegRenderOutputNormalizer(
        IProcessRunner processRunner,
        string ffmpegPath,
        TimeSpan timeout,
        int maxOutputBytes)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.ffmpegPath = string.IsNullOrWhiteSpace(ffmpegPath)
            ? throw new ArgumentException("FFmpeg path is required.", nameof(ffmpegPath))
            : ffmpegPath;
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        this.timeout = timeout;
        this.maxOutputBytes = maxOutputBytes;
    }

    /// <inheritdoc />
    public async Task<RenderOutputNormalizationResult> NormalizeAsync(
        RenderOutputNormalizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourcePath) || string.IsNullOrWhiteSpace(request.OutputPath))
            return new(false, "render.audio_normalization_path_missing", "Normalization paths are required.");
        if (request.TargetLoudnessLufs > 0 || request.MaximumTruePeakDbtp > 0
            || request.AudioBitrateBitsPerSecond <= 0 || request.SampleRate <= 0
            || request.ChannelLayout is not ("mono" or "stereo"))
            return new(false, "render.audio_normalization_profile_invalid", "Audio normalization profile is invalid.");

        string loudness = request.TargetLoudnessLufs.ToString("0.###", CultureInfo.InvariantCulture);
        string truePeak = request.MaximumTruePeakDbtp.ToString("0.###", CultureInfo.InvariantCulture);
        string channels = request.ChannelLayout == "mono" ? "1" : "2";
        string baseFilter = $"loudnorm=I={loudness}:TP={truePeak}:LRA=11";
        ProcessExecutionResult measurement = await processRunner.RunAsync(
            new ProcessStartRequest(
                ffmpegPath,
                [
                    "-v", "info", "-nostats", "-nostdin", "-i", request.SourcePath,
                    "-map", "0:a:0", "-af", $"{baseFilter}:print_format=json", "-f", "null", "-"
                ],
                timeout,
                maxOutputBytes),
            cancellationToken);
        if (!measurement.IsSuccess)
            return new(false, measurement.ErrorCode ?? "render.audio_measurement_failed", measurement.ErrorMessage ?? measurement.StandardError);

        LoudnessMeasurement measured;
        try
        {
            int start = measurement.StandardError.LastIndexOf('{');
            int end = measurement.StandardError.LastIndexOf('}');
            if (start < 0 || end <= start) throw new JsonException("loudnorm JSON was not found.");
            using JsonDocument document = JsonDocument.Parse(measurement.StandardError[start..(end + 1)]);
            JsonElement root = document.RootElement;
            measured = new(
                ReadNumber(root, "input_i"),
                ReadNumber(root, "input_tp"),
                ReadNumber(root, "input_lra"),
                ReadNumber(root, "input_thresh"),
                ReadNumber(root, "target_offset"));
        }
        catch (Exception exception) when (exception is JsonException or FormatException or OverflowException)
        {
            return new(false, "render.audio_measurement_invalid", exception.Message);
        }

        string normalizationFilter = $"{baseFilter}"+
            $":measured_I={measured.Integrated}"+
            $":measured_TP={measured.TruePeak}"+
            $":measured_LRA={measured.Range}"+
            $":measured_thresh={measured.Threshold}"+
            $":offset={measured.Offset}:linear=true";
        ProcessExecutionResult result = await processRunner.RunAsync(
            new ProcessStartRequest(
                ffmpegPath,
                [
                    "-v", "error", "-xerror", "-nostdin", "-y", "-i", request.SourcePath,
                    "-map", "0:v:0", "-map", "0:a:0", "-c:v", "copy",
                    "-af", normalizationFilter,
                    "-c:a", "aac", "-b:a", $"{request.AudioBitrateBitsPerSecond / 1000}k",
                    "-ar", request.SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", channels,
                    "-movflags", "+faststart", request.OutputPath
                ],
                timeout,
                maxOutputBytes),
            cancellationToken);
        if (!result.IsSuccess)
            return new(false, result.ErrorCode ?? "render.audio_normalization_failed", result.ErrorMessage ?? result.StandardError);
        if (!File.Exists(request.OutputPath) || new FileInfo(request.OutputPath).Length <= 0)
            return new(false, "render.audio_normalization_output_missing", "FFmpeg did not create a normalized MP4.");
        return new(true, null, null);
    }

    private static string ReadNumber(JsonElement root, string name)
    {
        string value = root.GetProperty(name).GetString()
            ?? throw new JsonException($"loudnorm property '{name}' is null.");
        _ = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return value;
    }

    private sealed record LoudnessMeasurement(
        string Integrated,
        string TruePeak,
        string Range,
        string Threshold,
        string Offset);
}
