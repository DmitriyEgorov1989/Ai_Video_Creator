using System.Collections.Immutable;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Media;

namespace VideoFactory.VideoProduction.UnitTests.QualityAssurance;

public sealed class OutputQualityAssuranceTests
{
    [Fact]
    public async Task AcceptsOutputWhenProbeAndFullDecodeMatchProfile()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1]);
            OutputQualityAssurance qa = new(
                new FakeProbe(CreateMetadata()),
                new FakeDecode(MediaDecodeResult.Succeeded()),
                TimeSpan.FromSeconds(5),
                65_536);

            OutputQualityResult result = await qa.VerifyAsync(
                new OutputQualityRequest(
                    path,
                    1080,
                    1920,
                    new FrameRate(30, 1),
                    900,
                    1,
                    true),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Equal(900, result.DurationFrames);
            Assert.Equal(1080, result.Width);
            Assert.Equal(1920, result.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsOutputWhenFullDecodeFails()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1]);
            OutputQualityAssurance qa = new(
                new FakeProbe(CreateMetadata()),
                new FakeDecode(MediaDecodeResult.Failed("media.decode_failed", "decode failed")),
                TimeSpan.FromSeconds(5),
                65_536);

            OutputQualityResult result = await qa.VerifyAsync(
                new OutputQualityRequest(path, 1080, 1920, new FrameRate(30, 1), 900, 1, true),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("media.decode_failed", result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static MediaMetadata CreateMetadata() => new(
        "mov,mp4,m4a",
        30_000_000,
        ImmutableArray.Create(new VideoStreamMetadata(
            0,
            "h264",
            1080,
            1920,
            new FrameRate(30, 1),
            false,
            null)),
        ImmutableArray.Create(new AudioStreamMetadata(1, "aac", 48_000, 2)));

    private sealed class FakeProbe(MediaMetadata metadata) : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(MediaProbeResult.Succeeded(metadata));
    }

    private sealed class FakeDecode(MediaDecodeResult result) : IMediaDecodeVerifier
    {
        public Task<MediaDecodeResult> VerifyAsync(MediaDecodeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
