using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Infrastructure.Media;

namespace VideoFactory.VideoProduction.UnitTests.Media;

public sealed class FfmpegAudioStemBuilderTests
{
    [Fact]
    public async Task SkipsWhenSourceHasNoAudioStream()
    {
        string sourcePath = Path.GetTempFileName();
        string outputPath = Path.Combine(Path.GetTempPath(), $"stem-{Guid.NewGuid():N}.m4a");
        try
        {
            MediaMetadata metadata = new(
                "mp4",
                1_000_000,
                [new VideoStreamMetadata(0, "h264", 1920, 1080, null, false, 0)],
                []);
            IAudioStemBuilder builder = new FfmpegAudioStemBuilder(
                new FakeRunner(), "ffmpeg");

            AudioStemResult result = await builder.BuildAsync(
                new AudioStemRequest(
                    sourcePath,
                    outputPath,
                    metadata,
                    TimeSpan.FromSeconds(5),
                    4_096),
                CancellationToken.None);

            Assert.False(result.IsCreated);
            Assert.Null(result.ErrorCode);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task NormalizesAudioToConfiguredProfile()
    {
        string sourcePath = Path.GetTempFileName();
        string outputPath = Path.Combine(Path.GetTempPath(), $"stem-{Guid.NewGuid():N}.m4a");
        FakeRunner runner = new();
        try
        {
            MediaMetadata metadata = new(
                "mp4",
                1_000_000,
                [new VideoStreamMetadata(0, "h264", 1920, 1080, null, false, 0)],
                [new AudioStreamMetadata(1, "aac", 44_100, 1)]);
            IAudioStemBuilder builder = new FfmpegAudioStemBuilder(runner, "ffmpeg");

            AudioStemResult result = await builder.BuildAsync(
                new AudioStemRequest(sourcePath, outputPath, metadata, TimeSpan.FromSeconds(5), 4_096),
                CancellationToken.None);

            Assert.True(result.IsCreated);
            Assert.Contains("loudnorm=I=-16:TP=-1:LRA=11", runner.Request!.Arguments);
            Assert.Contains("-ar", runner.Request.Arguments);
            Assert.Contains("48000", runner.Request.Arguments);
            Assert.Contains("-channel_layout", runner.Request.Arguments);
            Assert.Contains("stereo", runner.Request.Arguments);
            Assert.EndsWith(".m4a", runner.Request.Arguments[^1], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public ProcessStartRequest? Request { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            File.WriteAllBytes(request.Arguments[^1], [1]);
            return Task.FromResult(new ProcessExecutionResult(
                ProcessExecutionStatus.Succeeded, 0, string.Empty, string.Empty,
                null, null, false, false));
        }
    }
}
