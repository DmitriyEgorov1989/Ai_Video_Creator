using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Domain.Media;

public sealed record VideoStreamMetadata
{
    public int StreamIndex { get; }
    public string Codec { get; }
    public int Width { get; }
    public int Height { get; }
    public FrameRate? AverageFrameRate { get; }
    public bool? IsVariableFrameRate { get; }
    public int? RotationDegrees { get; }

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

public sealed record AudioStreamMetadata
{
    public int StreamIndex { get; }
    public string Codec { get; }
    public int SampleRate { get; }
    public int Channels { get; }

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

public sealed record MediaMetadata
{
    public string Container { get; }
    public long DurationMicroseconds { get; }
    public ImmutableArray<VideoStreamMetadata> VideoStreams { get; }
    public ImmutableArray<AudioStreamMetadata> AudioStreams { get; }

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

public sealed record MediaAsset
{
    public ArtifactId Id { get; }
    public MediaMetadata Metadata { get; }

    public MediaAsset(ArtifactId id, MediaMetadata metadata)
    {
        if (id.IsEmpty) throw new ArgumentException("Artifact ID cannot be empty.", nameof(id));
        ArgumentNullException.ThrowIfNull(metadata);
        Id = id;
        Metadata = metadata;
    }
}
