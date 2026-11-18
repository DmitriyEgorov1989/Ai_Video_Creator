using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Rendering;

/// <summary>
/// Описывает профиль кодирования, используемый для предварительной оценки размера MP4.
/// </summary>
/// <param name="VideoBitrateBitsPerSecond">Битрейт видео в битах в секунду.</param>
/// <param name="AudioBitrateBitsPerSecond">Битрейт аудио в битах в секунду.</param>
/// <param name="ContainerReserveBytes">Резерв контейнера в байтах.</param>
public sealed record OutputEncodingProfile(
    long VideoBitrateBitsPerSecond,
    long AudioBitrateBitsPerSecond,
    long ContainerReserveBytes)
{
    /// <summary>Проверяет, что профиль содержит допустимые положительные значения.</summary>
    public void Validate()
    {
        if (VideoBitrateBitsPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(VideoBitrateBitsPerSecond));
        if (AudioBitrateBitsPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(AudioBitrateBitsPerSecond));
        if (ContainerReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(ContainerReserveBytes));
    }
}

/// <summary>Содержит результат предварительной оценки размера render-output.</summary>
/// <param name="EstimatedBytes">Оценочный размер в байтах.</param>
/// <param name="DurationSeconds">Длительность монтажной шкалы в секундах.</param>
/// <param name="TotalBitrateBitsPerSecond">Суммарный bitrate потоков в битах в секунду.</param>
public sealed record RenderSizeEstimate(
    long EstimatedBytes,
    double DurationSeconds,
    long TotalBitrateBitsPerSecond);

/// <summary>Вычисляет оценочный размер MP4 до запуска render-host.</summary>
public static class RenderSizeEstimator
{
    /// <summary>
    /// Оценивает размер по длительности, профилю потоков и резерву контейнера.
    /// </summary>
    /// <param name="durationFrames">Положительная длительность монтажной шкалы в кадрах.</param>
    /// <param name="frameRate">Положительная частота кадров.</param>
    /// <param name="profile">Профиль кодирования; значение не может быть <see langword="null"/>.</param>
    /// <returns>Оценка размера в байтах.</returns>
    public static RenderSizeEstimate Estimate(
        int durationFrames,
        FrameRate frameRate,
        OutputEncodingProfile profile)
    {
        if (durationFrames <= 0) throw new ArgumentOutOfRangeException(nameof(durationFrames));
        if (!frameRate.IsValid) throw new ArgumentOutOfRangeException(nameof(frameRate));
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        long totalBitrate = checked(
            profile.VideoBitrateBitsPerSecond + profile.AudioBitrateBitsPerSecond);
        long numerator = checked(
            (long)durationFrames * totalBitrate * frameRate.Denominator);
        long denominator = checked((long)frameRate.Numerator * 8);
        long mediaBytes = checked((numerator + denominator - 1) / denominator);
        long estimatedBytes = checked(mediaBytes + profile.ContainerReserveBytes);
        double durationSeconds = (double)durationFrames
            * frameRate.Denominator / frameRate.Numerator;

        return new RenderSizeEstimate(estimatedBytes, durationSeconds, totalBitrate);
    }
}
