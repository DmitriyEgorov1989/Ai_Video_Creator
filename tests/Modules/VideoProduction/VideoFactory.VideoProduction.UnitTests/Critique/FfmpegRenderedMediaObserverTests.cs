using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.QualityAssurance;

namespace VideoFactory.VideoProduction.UnitTests.Critique;

public sealed class FfmpegRenderedMediaObserverTests
{
    [Fact]
    public async Task DetectsBlackIntervalsWithoutShellInterpolation()
    {
        FakeProcessRunner process = new(
            "[blackdetect @ 000] black_start:1.000 black_end:1.500 black_duration:0.500\n"
            + "[freezedetect @ 000] freeze_start: 2.000\n[freezedetect @ 000] freeze_end: 2.500 | freeze_duration: 0.500");
        FfmpegRenderedMediaObserver observer = new(
            process,
            "ffmpeg",
            TimeSpan.FromSeconds(10),
            32_768,
            minimumBlackDurationMilliseconds: 200);

        RenderedMediaObservationResult result = await observer.ObserveAsync(
            new RenderedMediaObservationRequest(
                "C:\\media files\\render.mp4",
                new FrameRate(30, 1),
                90),
            CancellationToken.None);

        RenderedMediaObservation observation = Assert.IsType<RenderedMediaObservationResult.Succeeded>(result).Observation;
        Assert.Equal(new OutputRange(30, 45), Assert.Single(observation.BlackIntervals));
        Assert.Equal(new OutputRange(60, 75), Assert.Single(observation.FrozenIntervals));
        Assert.Equal("ffmpeg", process.Request?.FileName);
        Assert.Contains("C:\\media files\\render.mp4", process.Request!.Arguments);
        Assert.Contains("blackdetect=d=0.2:pix_th=0.10,freezedetect=n=-60dB:d=1", process.Request.Arguments);
    }

    private sealed class FakeProcessRunner(string standardError) : IProcessRunner
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
