using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Media;

public sealed class MediaMetadataTests
{
    [Fact]
    public void VideoWithoutAudioIsValid()
    {
        MediaMetadata metadata = new(
            "mp4",
            5_000_000,
            [new VideoStreamMetadata(0, "h264", 1080, 1920, new FrameRate(30, 1), false, 0)],
            []);

        Assert.Empty(metadata.AudioStreams);
        Assert.Single(metadata.VideoStreams);
    }

    [Fact]
    public void DuplicateVideoStreamIndexesAreRejected()
    {
        ImmutableArray<VideoStreamMetadata> streams =
        [
            new(0, "h264", 1080, 1920, null, null, null),
            new(0, "hevc", 1080, 1920, null, null, null)
        ];

        Assert.Throws<ArgumentException>(() => new MediaMetadata("mp4", 5_000_000, streams, []));
    }

    [Fact]
    public void UnknownFrameRateAndRotationRemainUnknown()
    {
        VideoStreamMetadata stream = new(0, "h264", 1080, 1920, null, null, null);

        Assert.Null(stream.AverageFrameRate);
        Assert.Null(stream.IsVariableFrameRate);
        Assert.Null(stream.RotationDegrees);
    }

    [Fact]
    public void StreamIndexesMustBeUniqueAcrossVideoAndAudio()
    {
        Assert.Throws<ArgumentException>(() => new MediaMetadata(
            "mp4",
            5_000_000,
            [new VideoStreamMetadata(0, "h264", 1080, 1920, null, null, null)],
            [new AudioStreamMetadata(0, "aac", 48_000, 2)]));
    }
}
