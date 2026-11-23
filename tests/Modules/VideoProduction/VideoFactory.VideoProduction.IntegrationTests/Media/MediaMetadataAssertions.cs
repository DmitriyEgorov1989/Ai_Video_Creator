using VideoFactory.VideoProduction.Domain.Media;

namespace VideoFactory.VideoProduction.IntegrationTests.Media;

/// <summary>Проверяет FFprobe metadata относительно fixture oracle.</summary>
public static class MediaMetadataAssertions
{
    /// <summary>Требует точного совпадения основного video/audio profile.</summary>
    /// <param name="fixtureId">Идентификатор fixture для диагностики.</param>
    /// <param name="metadata">Проверенные metadata, не равны <see langword="null"/>.</param>
    /// <param name="width">Ожидаемая ширина в пикселях.</param>
    /// <param name="height">Ожидаемая высота в пикселях.</param>
    /// <param name="framesPerSecond">Ожидаемая частота кадров.</param>
    /// <param name="videoCodec">Ожидаемый video codec.</param>
    /// <param name="audioCodec">Ожидаемый audio codec.</param>
    /// <param name="container">Ожидаемый container.</param>
    /// <param name="durationMicroseconds">Ожидаемая длительность в микросекундах.</param>
    /// <param name="pixelFormat">Ожидаемый pixel format.</param>
    /// <param name="sampleRate">Ожидаемая частота дискретизации в герцах.</param>
    /// <param name="channels">Ожидаемое количество audio channels.</param>
    public static void RequireVideoProfile(
        string fixtureId,
        MediaMetadata metadata,
        int width,
        int height,
        int framesPerSecond,
        string videoCodec,
        string audioCodec,
        string container,
        long durationMicroseconds,
        string pixelFormat,
        int sampleRate,
        int channels)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        VideoStreamMetadata video = metadata.VideoStreams.Length == 1
            ? metadata.VideoStreams[0]
            : throw new InvalidDataException($"{fixtureId}: expected one video stream, found {metadata.VideoStreams.Length}.");
        AudioStreamMetadata audio = metadata.AudioStreams.Length == 1
            ? metadata.AudioStreams[0]
            : throw new InvalidDataException($"{fixtureId}: expected one audio stream, found {metadata.AudioStreams.Length}.");

        RequireEqual(fixtureId, "width", width, video.Width);
        RequireEqual(fixtureId, "height", height, video.Height);
        RequireEqual(fixtureId, "video codec", videoCodec, video.Codec);
        RequireEqual(fixtureId, "audio codec", audioCodec, audio.Codec);
        RequireEqual(fixtureId, "container", container, metadata.Container);
        RequireEqual(fixtureId, "pixel format", pixelFormat, video.PixelFormat);
        RequireEqual(fixtureId, "sample rate", sampleRate, audio.SampleRate);
        RequireEqual(fixtureId, "channels", channels, audio.Channels);
        if (Math.Abs(durationMicroseconds - metadata.DurationMicroseconds) > 1_000)
        {
            throw new InvalidDataException(
                $"{fixtureId}: expected duration {durationMicroseconds} microseconds, observed {metadata.DurationMicroseconds}.");
        }
        if (video.AverageFrameRate is null || video.AverageFrameRate.Value.Denominator == 0)
        {
            throw new InvalidDataException($"{fixtureId}: average frame rate is missing.");
        }

        double actualFps = (double)video.AverageFrameRate.Value.Numerator / video.AverageFrameRate.Value.Denominator;
        if (Math.Abs(framesPerSecond - actualFps) > 0.001)
        {
            throw new InvalidDataException(
                $"{fixtureId}: expected FPS {framesPerSecond}, observed {actualFps:0.###}.");
        }
    }

    private static void RequireEqual<T>(string fixtureId, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidDataException($"{fixtureId}: expected {field} '{expected}', observed '{actual}'.");
        }
    }
}
