using VideoFactory.VideoProduction.Domain.Media;

namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>
/// Ограниченный входной профиль первого MVP.
/// </summary>
public sealed record MediaInputProfile
{
    /// <summary>Контейнеры, разрешённые профилем.</summary>
    public IReadOnlySet<string> AllowedContainers { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mov", "mp4" };

    /// <summary>Видеокодеки, разрешённые профилем.</summary>
    public IReadOnlySet<string> AllowedVideoCodecs { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264" };

    /// <summary>Аудиокодеки, разрешённые профилем.</summary>
    public IReadOnlySet<string> AllowedAudioCodecs { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aac", "opus" };

    /// <summary>Максимальный размер файла в байтах.</summary>
    public long MaxFileBytes { get; init; } = 524_288_000;

    /// <summary>Максимальная длительность в секундах.</summary>
    public int MaxDurationSeconds { get; init; } = 1_800;
}

/// <summary>Результат проверки входного профиля.</summary>
public sealed record MediaInputProfileResult(
    bool IsSupported,
    string? ErrorCode,
    string? ErrorMessage)
{
    /// <summary>Создаёт успешный результат.</summary>
    public static MediaInputProfileResult Supported() => new(true, null, null);

    /// <summary>Создаёт ожидаемую ошибку профиля.</summary>
    public static MediaInputProfileResult Rejected(
        string errorCode,
        string errorMessage) => new(false, errorCode, errorMessage);
}

/// <summary>Проверяет фактические metadata файла против входного профиля MVP.</summary>
public sealed class MediaInputProfileValidator
{
    /// <summary>Выполняет проверку файла, контейнера и фактических потоков.</summary>
    public MediaInputProfileResult Validate(
        string filePath,
        MediaMetadata metadata,
        MediaInputProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(profile);

        FileInfo file = new(filePath);
        if (!file.Exists)
        {
            return MediaInputProfileResult.Rejected(
                "media.file_missing", "The media file does not exist.");
        }

        if (file.Length > profile.MaxFileBytes)
        {
            return MediaInputProfileResult.Rejected(
                "media.file_size_limit_exceeded",
                "The media file exceeds the configured size limit.");
        }

        long maxDuration = checked((long)profile.MaxDurationSeconds * 1_000_000);
        if (metadata.DurationMicroseconds > maxDuration)
        {
            return MediaInputProfileResult.Rejected(
                "media.duration_limit_exceeded",
                "The media duration exceeds the configured limit.");
        }

        string container = metadata.Container.Split(',')[0];
        if (!profile.AllowedContainers.Contains(container))
        {
            return MediaInputProfileResult.Rejected(
                "media.container_unsupported",
                $"Container '{metadata.Container}' is not supported by the MVP.");
        }

        if (metadata.VideoStreams.Length != 1)
        {
            return MediaInputProfileResult.Rejected(
                "media.video_stream_count_unsupported",
                "The MVP accepts exactly one video stream.");
        }

        VideoStreamMetadata video = metadata.VideoStreams[0];
        if (!profile.AllowedVideoCodecs.Contains(video.Codec))
        {
            return MediaInputProfileResult.Rejected(
                "media.video_codec_unsupported",
                $"Video codec '{video.Codec}' is not supported by the MVP.");
        }

        if (video.IsHdr == true)
        {
            return MediaInputProfileResult.Rejected(
                "media.hdr_unsupported", "HDR input is not supported by the MVP.");
        }

        if (!IsSimpleSampleAspectRatio(video.SampleAspectRatio))
        {
            return MediaInputProfileResult.Rejected(
                "media.sample_aspect_ratio_unsupported",
                "The input sample aspect ratio is not supported by the MVP.");
        }

        foreach (AudioStreamMetadata audio in metadata.AudioStreams)
        {
            if (!profile.AllowedAudioCodecs.Contains(audio.Codec))
            {
                return MediaInputProfileResult.Rejected(
                    "media.audio_codec_unsupported",
                    $"Audio codec '{audio.Codec}' is not supported by the MVP.");
            }
        }

        return MediaInputProfileResult.Supported();
    }

    private static bool IsSimpleSampleAspectRatio(string? value) =>
        string.IsNullOrWhiteSpace(value) || value is "1:1" or "0:1";
}
