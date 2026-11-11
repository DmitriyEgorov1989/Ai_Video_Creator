using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Domain.Media;

/// <summary>
/// Описывает видеопоток медиафайла.
/// </summary>
public sealed record VideoStreamMetadata
{
    /// <summary>
    /// Получает индекс потока в контейнере.
    /// </summary>
    public int StreamIndex { get; }

    /// <summary>
    /// Получает имя видеокодека.
    /// </summary>
    public string Codec { get; }

    /// <summary>
    /// Получает ширину кадра в пикселях.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Получает высоту кадра в пикселях.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Получает среднюю частоту кадров или <see langword="null"/>, если она не определена.
    /// </summary>
    public FrameRate? AverageFrameRate { get; }

    /// <summary>
    /// Получает признак переменной частоты кадров или <see langword="null"/>, если режим частоты кадров не определён.
    /// </summary>
    public bool? IsVariableFrameRate { get; }

    /// <summary>
    /// Получает поворот кадра в градусах или <see langword="null"/>, если метаданные поворота отсутствуют.
    /// </summary>
    public int? RotationDegrees { get; }

    /// <summary>
    /// Создаёт описание видеопотока.
    /// </summary>
    /// <param name="streamIndex">Неотрицательный индекс потока в контейнере.</param>
    /// <param name="codec">Непустое имя видеокодека.</param>
    /// <param name="width">Ширина кадра в пикселях.</param>
    /// <param name="height">Высота кадра в пикселях.</param>
    /// <param name="averageFrameRate">Средняя частота кадров или <see langword="null"/>, если она не определена.</param>
    /// <param name="isVariableFrameRate">Признак переменной частоты кадров или <see langword="null"/>, если режим не определён.</param>
    /// <param name="rotationDegrees">Поворот кадра в градусах или <see langword="null"/>, если метаданные поворота отсутствуют.</param>
    public VideoStreamMetadata(
        int streamIndex,
        string codec,
        int width,
        int height,
        FrameRate? averageFrameRate,
        bool? isVariableFrameRate,
        int? rotationDegrees)
    {
        if (streamIndex < 0) throw new ArgumentOutOfRangeException(nameof(streamIndex));
        if (string.IsNullOrWhiteSpace(codec)) throw new ArgumentException("Codec is required.", nameof(codec));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (averageFrameRate is { IsValid: false }) throw new ArgumentException("Frame rate must be valid.", nameof(averageFrameRate));

        StreamIndex = streamIndex;
        Codec = codec;
        Width = width;
        Height = height;
        AverageFrameRate = averageFrameRate;
        IsVariableFrameRate = isVariableFrameRate;
        RotationDegrees = rotationDegrees;
    }
}

/// <summary>
/// Описывает аудиопоток медиафайла.
/// </summary>
public sealed record AudioStreamMetadata
{
    /// <summary>
    /// Получает индекс потока в контейнере.
    /// </summary>
    public int StreamIndex { get; }

    /// <summary>
    /// Получает имя аудиокодека.
    /// </summary>
    public string Codec { get; }

    /// <summary>
    /// Получает частоту дискретизации в герцах.
    /// </summary>
    public int SampleRate { get; }

    /// <summary>
    /// Получает количество аудиоканалов.
    /// </summary>
    public int Channels { get; }

    /// <summary>
    /// Создаёт описание аудиопотока.
    /// </summary>
    /// <param name="streamIndex">Неотрицательный индекс потока в контейнере.</param>
    /// <param name="codec">Непустое имя аудиокодека.</param>
    /// <param name="sampleRate">Частота дискретизации в герцах.</param>
    /// <param name="channels">Количество аудиоканалов.</param>
    public AudioStreamMetadata(int streamIndex, string codec, int sampleRate, int channels)
    {
        if (streamIndex < 0) throw new ArgumentOutOfRangeException(nameof(streamIndex));
        if (string.IsNullOrWhiteSpace(codec)) throw new ArgumentException("Codec is required.", nameof(codec));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        StreamIndex = streamIndex;
        Codec = codec;
        SampleRate = sampleRate;
        Channels = channels;
    }
}

/// <summary>
/// Содержит проверенные метаданные медиафайла и его потоков.
/// </summary>
public sealed record MediaMetadata
{
    /// <summary>
    /// Получает имя медиа-контейнера.
    /// </summary>
    public string Container { get; }

    /// <summary>
    /// Получает длительность медиафайла в микросекундах.
    /// </summary>
    public long DurationMicroseconds { get; }

    /// <summary>
    /// Получает инициализированную коллекцию видеопотоков; коллекция может быть пустой.
    /// </summary>
    public ImmutableArray<VideoStreamMetadata> VideoStreams { get; }

    /// <summary>
    /// Получает инициализированную коллекцию аудиопотоков; коллекция может быть пустой.
    /// </summary>
    public ImmutableArray<AudioStreamMetadata> AudioStreams { get; }

    /// <summary>
    /// Создаёт метаданные медиафайла с уникальными индексами потоков.
    /// </summary>
    /// <param name="container">Непустое имя медиа-контейнера.</param>
    /// <param name="durationMicroseconds">Положительная длительность медиафайла в микросекундах.</param>
    /// <param name="videoStreams">Инициализированная коллекция видеопотоков; может быть пустой.</param>
    /// <param name="audioStreams">Инициализированная коллекция аудиопотоков; может быть пустой.</param>
    public MediaMetadata(
        string container,
        long durationMicroseconds,
        ImmutableArray<VideoStreamMetadata> videoStreams,
        ImmutableArray<AudioStreamMetadata> audioStreams)
    {
        if (string.IsNullOrWhiteSpace(container)) throw new ArgumentException("Container is required.", nameof(container));
        if (durationMicroseconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationMicroseconds));
        if (videoStreams.IsDefault) throw new ArgumentException("Video streams must be initialized.", nameof(videoStreams));
        if (audioStreams.IsDefault) throw new ArgumentException("Audio streams must be initialized.", nameof(audioStreams));
        if (videoStreams.Select(stream => stream.StreamIndex).Distinct().Count() != videoStreams.Length)
        {
            throw new ArgumentException("Video stream indexes must be unique.", nameof(videoStreams));
        }

        if (audioStreams.Select(stream => stream.StreamIndex).Distinct().Count() != audioStreams.Length)
        {
            throw new ArgumentException("Audio stream indexes must be unique.", nameof(audioStreams));
        }

        if (videoStreams.Select(stream => stream.StreamIndex)
            .Intersect(audioStreams.Select(stream => stream.StreamIndex))
            .Any())
        {
            throw new ArgumentException("Stream indexes must be unique across the media container.");
        }

        Container = container;
        DurationMicroseconds = durationMicroseconds;
        VideoStreams = videoStreams;
        AudioStreams = audioStreams;
    }
}

/// <summary>
/// Связывает медиа-артефакт с его проверенными метаданными.
/// </summary>
public sealed record MediaAsset
{
    /// <summary>
    /// Получает идентификатор медиа-артефакта.
    /// </summary>
    public ArtifactId Id { get; }

    /// <summary>
    /// Получает метаданные медиа-артефакта; значение никогда не равно <see langword="null"/>.
    /// </summary>
    public MediaMetadata Metadata { get; }

    /// <summary>
    /// Создаёт медиа-ресурс с указанными метаданными.
    /// </summary>
    /// <param name="id">Непустой идентификатор артефакта.</param>
    /// <param name="metadata">Метаданные артефакта; не могут быть <see langword="null"/>.</param>
    public MediaAsset(ArtifactId id, MediaMetadata metadata)
    {
        if (id.IsEmpty) throw new ArgumentException("Artifact ID cannot be empty.", nameof(id));
        ArgumentNullException.ThrowIfNull(metadata);
        Id = id;
        Metadata = metadata;
    }
}
