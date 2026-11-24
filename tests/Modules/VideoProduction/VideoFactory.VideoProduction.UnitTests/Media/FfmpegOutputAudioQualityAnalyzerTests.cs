using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Infrastructure.Media;

namespace VideoFactory.VideoProduction.UnitTests.Media;

public sealed class FfmpegOutputAudioQualityAnalyzerTests
{
    [Fact]
    public async Task ParsesLoudnormMeasurementsFromFfmpegDiagnosticOutput()
    {
        CapturingProcessRunner runner = new(
            new ProcessExecutionResult(
                ProcessExecutionStatus.Succeeded,
                0,
                string.Empty,
                "diagnostic\n{\n  \"input_i\" : \"-16.20\",\n  \"input_tp\" : \"-1.30\"\n}\n",
                null,
                null,
                false,
                false));
        FfmpegOutputAudioQualityAnalyzer analyzer = new(runner, "ffmpeg");

        OutputAudioQualityResult result = await analyzer.AnalyzeAsync(
            new OutputAudioQualityRequest("final.mp4", TimeSpan.FromSeconds(10), 65_536),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal(-16.2m, result.IntegratedLoudnessLufs);
        Assert.Equal(-1.3m, result.TruePeakDbtp);
        Assert.Equal("final.mp4", runner.Request!.Arguments[3]);
        Assert.Contains(runner.Request.Arguments, argument => argument.Contains("print_format=json", StringComparison.Ordinal));
    }

    private sealed class CapturingProcessRunner(ProcessExecutionResult result) : IProcessRunner
    {
        public ProcessStartRequest? Request { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(result);
        }
    }
}
