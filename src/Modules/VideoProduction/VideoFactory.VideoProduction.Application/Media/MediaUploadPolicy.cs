namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>Предварительно отбирает загрузки MVP; фактические потоки затем проверяются через FFprobe.</summary>
public static class MediaUploadPolicy
{
    /// <summary>Разрешает известный MIME и безопасное имя; <see langword="null"/> имени допустим у Telegram Video, MIME обязателен.</summary>
    public static bool Accepts(string? mimeType, string? fileName)
    {
        string? expected = mimeType?.ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            _ => null
        };
        if (expected is null) return false;
        if (fileName is null) return true;
        return !string.IsNullOrWhiteSpace(fileName)
            && !fileName.Any(character => char.IsControl(character) || "\\/:<>\"|?*".Contains(character))
            && fileName is not "." and not ".."
            && Path.GetExtension(fileName).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
