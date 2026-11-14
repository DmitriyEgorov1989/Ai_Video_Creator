using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Media;

public sealed class MediaInputProfileValidatorTests
{
    [Fact]
    public void AcceptsSupportedProfile()
    {
        string path = CreateFile(10);
        try
        {
            MediaInputProfileResult result = new MediaInputProfileValidator().Validate(
                path,
                CreateMetadata("mp4", "h264", "aac"),
                new MediaInputProfile());

            Assert.True(result.IsSupported, result.ErrorMessage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsUnsupportedVideoCodec()
    {
        string path = CreateFile(10);
        try
        {
            MediaInputProfileResult result = new MediaInputProfileValidator().Validate(
                path,
                CreateMetadata("mp4", "hevc", "aac"),
                new MediaInputProfile());

            Assert.Equal("media.video_codec_unsupported", result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RejectsHdr()
    {
        string path = CreateFile(10);
        try
        {
            VideoStreamMetadata video = new(
                0, "h264", 1920, 1080, new FrameRate(30, 1), false, 0, "1:1", true);
            MediaMetadata metadata = new(
                "mp4", 1_000_000, [video],
                [new AudioStreamMetadata(1, "aac", 48_000, 2)]);

            MediaInputProfileResult result = new MediaInputProfileValidator().Validate(
                path, metadata, new MediaInputProfile());

            Assert.Equal("media.hdr_unsupported", result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateFile(int length)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private static MediaMetadata CreateMetadata(
        string container,
        string videoCodec,
        string audioCodec) =>
        new(
            container,
            1_000_000,
            [new VideoStreamMetadata(0, videoCodec, 1920, 1080, new FrameRate(30, 1), false, 0)],
            [new AudioStreamMetadata(1, audioCodec, 48_000, 2)]);
}
