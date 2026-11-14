using System.Globalization;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.Media;

/// <summary>
/// Получает metadata медиафайла через FFprobe.
/// </summary>
public sealed class FfprobeMediaProbe : IMediaProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IProcessRunner processRunner;
    private readonly string executablePath;

    /// <summary>
    /// Создаёт FFprobe adapter.
    /// </summary>
    /// <param name="processRunner">Безопасный запуск внешнего процесса.</param>
    /// <param name="executablePath">Путь или имя FFprobe.</param>
    public FfprobeMediaProbe(IProcessRunner processRunner, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        this.processRunner = processRunner;
        this.executablePath = executablePath;
    }

    /// <inheritdoc />
    public async Task<MediaProbeResult> ProbeAsync(
        ProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            return MediaProbeResult.Failed(
                "media.file_path_missing",
                "The media file path is required.");
        }

        if (!File.Exists(request.FilePath))
        {
            return MediaProbeResult.Failed(
                "media.file_missing",
                "The media file does not exist.");
        }

        ProcessExecutionResult processResult = await processRunner.RunAsync(
            new ProcessStartRequest(
                executablePath,
                [
                    "-v", "error",
                    "-print_format", "json",
                    "-show_format",
                    "-show_streams",
                    "-show_entries",
                    "format=format_name,duration:" +
                    "stream=index,codec_type,codec_name,width,height," +
                    "avg_frame_rate,r_frame_rate,sample_rate,channels," +
                    "tags,side_data_list",
                    request.FilePath
                ],
                request.Timeout,
                request.MaxOutputBytes),
            cancellationToken);

        if (!processResult.IsSuccess)
        {
            return MediaProbeResult.Failed(
                processResult.ErrorCode ?? "media.probe_failed",
                processResult.ErrorMessage ??
                "FFprobe could not read the media file.");
        }

        FfprobeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<FfprobeDocument>(
                processResult.StandardOutput,
                JsonOptions);
        }
        catch (JsonException exception)
        {
            return MediaProbeResult.Failed(
                "media.probe_invalid_json",
                $"FFprobe returned invalid JSON: {exception.Message}");
        }

        return Parse(document);
    }

    private static MediaProbeResult Parse(FfprobeDocument? document)
    {
        if (document?.Format is null || document.Streams is null)
        {
            return MediaProbeResult.Failed(
                "media.probe_incomplete",
                "FFprobe did not return format and stream metadata.");
        }

        if (!TryParseDuration(document.Format.Duration, out long duration))
        {
            return MediaProbeResult.Failed(
                "media.duration_invalid",
                "FFprobe returned an invalid media duration.");
        }

        if (string.IsNullOrWhiteSpace(document.Format.FormatName))
        {
            return MediaProbeResult.Failed(
                "media.container_missing",
                "FFprobe did not return a media container.");
        }

        List<VideoStreamMetadata> videos = [];
        List<AudioStreamMetadata> audios = [];

        foreach (FfprobeStream stream in document.Streams)
        {
            if (stream.CodecType is "video")
            {
                if (string.IsNullOrWhiteSpace(stream.CodecName) ||
                    stream.Width is not > 0 ||
                    stream.Height is not > 0)
                {
                    return MediaProbeResult.Failed(
                        "media.video_stream_invalid",
                        "FFprobe returned incomplete video stream metadata.");
                }

                FrameRate? frameRate = TryParseFrameRate(
                    stream.AverageFrameRate ?? stream.RealFrameRate);

                videos.Add(new VideoStreamMetadata(
                    stream.Index,
                    stream.CodecName,
                    stream.Width.Value,
                    stream.Height.Value,
                    frameRate,
                    IsVariableFrameRate(stream),
                    ReadRotation(stream),
                    stream.SampleAspectRatio,
                    IsHdr(stream)));
            }
            else if (stream.CodecType is "audio")
            {
                if (string.IsNullOrWhiteSpace(stream.CodecName) ||
                    !TryReadPositiveInt(stream.SampleRate, out int sampleRate) ||
                    stream.Channels is not > 0)
                {
                    return MediaProbeResult.Failed(
                        "media.audio_stream_invalid",
                        "FFprobe returned incomplete audio stream metadata.");
                }

                audios.Add(new AudioStreamMetadata(
                    stream.Index,
                    stream.CodecName,
                    sampleRate,
                    stream.Channels.Value));
            }
        }

        if (videos.Count == 0)
        {
            return MediaProbeResult.Failed(
                "media.video_stream_missing",
                "The media file does not contain a video stream.");
        }

        try
        {
            return MediaProbeResult.Succeeded(new MediaMetadata(
                document.Format.FormatName,
                duration,
                [.. videos],
                [.. audios]));
        }
        catch (ArgumentException exception)
        {
            return MediaProbeResult.Failed(
                "media.metadata_invalid",
                exception.Message);
        }
    }

    private static bool TryParseDuration(string? value, out long microseconds)
    {
        microseconds = 0;
        if (!decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out decimal seconds) || seconds <= 0)
        {
            return false;
        }

        decimal result = seconds * 1_000_000m;
        if (result > long.MaxValue)
        {
            return false;
        }

        microseconds = checked((long)Math.Round(
            result,
            MidpointRounding.AwayFromZero));
        return microseconds > 0;
    }

    private static FrameRate? TryParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Split('/');
        if (parts.Length != 2 ||
            !long.TryParse(parts[0], out long numerator) ||
            !long.TryParse(parts[1], out long denominator) ||
            numerator <= 0 ||
            denominator <= 0 ||
            numerator > int.MaxValue ||
            denominator > int.MaxValue)
        {
            return null;
        }

        return new FrameRate((int)numerator, (int)denominator);
    }

    private static bool TryReadPositiveInt(JsonElement? value, out int result)
    {
        result = 0;
        if (value is not JsonElement element)
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out result))
        {
            return result > 0;
        }

        return element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result)
            && result > 0;
    }

    private static bool? IsVariableFrameRate(FfprobeStream stream)
    {
        FrameRate? average = TryParseFrameRate(stream.AverageFrameRate);
        FrameRate? real = TryParseFrameRate(stream.RealFrameRate);

        return average.HasValue && real.HasValue && average.Value != real.Value;
    }

    private static int? ReadRotation(FfprobeStream stream)
    {
        int? rotation = stream.SideDataList?
            .FirstOrDefault(data => data.Rotation.HasValue)?.Rotation;

        if (rotation is null &&
            stream.Tags is not null &&
            stream.Tags.TryGetValue("rotate", out string? rawRotation) &&
            int.TryParse(rawRotation, out int tagRotation))
        {
            rotation = tagRotation;
        }

        if (rotation is null)
        {
            return null;
        }

        int normalized = rotation.Value % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static bool? IsHdr(FfprobeStream stream)
    {
        string? transfer = stream.ColorTransfer?.ToLowerInvariant();
        string? primaries = stream.ColorPrimaries?.ToLowerInvariant();
        string? space = stream.ColorSpace?.ToLowerInvariant();

        if (transfer is null && primaries is null && space is null)
        {
            return null;
        }

        return transfer is "smpte2084" or "arib-std-b67"
            || primaries is "bt2020"
            || space is "bt2020nc" or "bt2020ncl";
    }
}
