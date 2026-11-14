using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Infrastructure.Media;

namespace VideoFactory.VideoProduction.UnitTests.Media;

public sealed class FfprobeMediaProbeTests
{
    [Fact]
    public async Task ParsesTypedMetadataFromFfprobeJson()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            FakeProcessRunner runner = new(SuccessJson);
            IMediaProbe probe = new FfprobeMediaProbe(runner, "ffprobe");

            MediaProbeResult result = await probe.ProbeAsync(
                new ProbeRequest(filePath, TimeSpan.FromSeconds(5), 65_536),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.NotNull(result.Metadata);
            Assert.Equal("mov,mp4,m4a,3gp,3g2,mj2", result.Metadata.Container);
            Assert.Equal(12_345_678, result.Metadata.DurationMicroseconds);
            Assert.Single(result.Metadata.VideoStreams);
            Assert.Equal("h264", result.Metadata.VideoStreams[0].Codec);
            Assert.Equal(30, result.Metadata.VideoStreams[0].AverageFrameRate?.Numerator);
            Assert.Equal(90, result.Metadata.VideoStreams[0].RotationDegrees);
            Assert.Single(result.Metadata.AudioStreams);
            Assert.Equal(48_000, result.Metadata.AudioStreams[0].SampleRate);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ReturnsExpectedErrorForProcessFailure()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            FakeProcessRunner runner = new(string.Empty, succeeds: false);
            IMediaProbe probe = new FfprobeMediaProbe(runner, "ffprobe");

            MediaProbeResult result = await probe.ProbeAsync(
                new ProbeRequest(filePath, TimeSpan.FromSeconds(5), 65_536),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("media.probe_failed", result.ErrorCode);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task RejectsInputWithoutVideoStream()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            FakeProcessRunner runner = new(AudioOnlyJson, succeeds: true);
            IMediaProbe probe = new FfprobeMediaProbe(runner, "ffprobe");

            MediaProbeResult result = await probe.ProbeAsync(
                new ProbeRequest(filePath, TimeSpan.FromSeconds(5), 65_536),
                CancellationToken.None);

            Assert.False(result.IsSuccess);
            Assert.Equal("media.video_stream_missing", result.ErrorCode);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private const string SuccessJson = """
        {
          "format": {
            "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
            "duration": "12.345678"
          },
          "streams": [
            {
              "index": 0,
              "codec_type": "video",
              "codec_name": "h264",
              "width": 1920,
              "height": 1080,
              "avg_frame_rate": "30/1",
              "r_frame_rate": "30/1",
              "tags": { "rotate": "90" }
            },
            {
              "index": 1,
              "codec_type": "audio",
              "codec_name": "aac",
              "sample_rate": 48000,
              "channels": 2
            }
          ]
        }
        """;

    private const string AudioOnlyJson = """
        {
          "format": { "format_name": "wav", "duration": "1.0" },
          "streams": [
            {
              "index": 0,
              "codec_type": "audio",
              "codec_name": "pcm_s16le",
              "sample_rate": 48000,
              "channels": 2
            }
          ]
        }
        """;

    private static ProcessExecutionResult ProcessExecutionResultFailure => new(
        ProcessExecutionStatus.NonZeroExitCode,
        1,
        string.Empty,
        "invalid input",
        "media.probe_failed",
        "FFprobe failed.",
        false,
        false);

    private sealed class FakeProcessRunner(string output, bool succeeds = true) : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(!succeeds
                ? ProcessExecutionResultFailure
                : new ProcessExecutionResult(
                    ProcessExecutionStatus.Succeeded,
                    0,
                    output,
                    string.Empty,
                    null,
                    null,
                    false,
                    false));
    }
}
