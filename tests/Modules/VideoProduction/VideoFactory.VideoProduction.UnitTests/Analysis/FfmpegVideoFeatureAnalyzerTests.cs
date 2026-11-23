using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Analysis;

namespace VideoFactory.VideoProduction.UnitTests.Analysis;

public sealed class FfmpegVideoFeatureAnalyzerTests
{
    [Fact]
    public async Task ParsesFfmpegMetricsAndPassesSegmentAsSeparateArguments()
    {
        SegmentId segmentId = SegmentId.New();
        CapturingRunner runner = new(
            "[Parsed_metadata_1 @ 000] lavfi.signalstats.YAVG=128 lavfi.signalstats.YDIF=32 lavfi.signalstats.SATAVG=64\n" +
            "lavfi.astats.Overall.RMS_level=-12\n" +
            "[Parsed_cropdetect_2 @ 000] crop=320:568:0:0");
        FfmpegVideoFeatureAnalyzer analyzer = new(runner, "ffmpeg", TimeSpan.FromSeconds(5), 50_000);

        VideoFeatureAnalysisResult result = await analyzer.AnalyzeAsync(
            new VideoFeatureAnalysisRequest(
                "C:\\media\\master.mp4",
                [new AllowedSegment(
                    segmentId,
                    new SourceMapping(
                        ArtifactId.New(),
                        new OriginalRange(0, 2_000_000),
                        ArtifactId.New(),
                        new MasterRange(1_000_000, 3_000_000)))]),
            CancellationToken.None);

        SegmentAnalysisFeatures features = Assert.Single(result.Features);
        Assert.Equal(segmentId, features.SegmentId);
        Assert.True(features.Sharpness > 0m);
        Assert.True(features.Motion > 0m);
        Assert.True(features.AudioEnergy > 0m);
        Assert.Equal("ffmpeg", runner.Request!.FileName);
        Assert.Contains("1", runner.Request.Arguments);
        Assert.Contains("2", runner.Request.Arguments);
    }

    private sealed class CapturingRunner(string standardError) : IProcessRunner
    {
        public ProcessStartRequest? Request { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new ProcessExecutionResult(
                ProcessExecutionStatus.Succeeded,
                0,
                string.Empty,
                standardError,
                null,
                null,
                false,
                false));
        }
    }
}
