using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Infrastructure.Media;

namespace VideoFactory.VideoProduction.UnitTests.Media;

public sealed class FfmpegRenderOutputNormalizerTests
{
    [Fact]
    public async Task CopiesVideoAndNormalizesAudioToRequestedProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"render-normalization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string output = Path.Combine(root, "normalized.mp4");
            FakeRunner runner = new(output);
            FfmpegRenderOutputNormalizer normalizer = new(
                runner, "ffmpeg", TimeSpan.FromMinutes(1), 4096);

            RenderOutputNormalizationResult result = await normalizer.NormalizeAsync(
                new RenderOutputNormalizationRequest(
                    Path.Combine(root, "render.mp4"), output, -16, -1, 128_000, 48_000, "stereo"),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Equal(2, runner.Requests.Count);
            Assert.Contains(runner.Requests[0].Arguments, argument =>
                argument.Contains("print_format=json", StringComparison.Ordinal));
            ProcessStartRequest request = runner.Requests[1];
            Assert.Contains("copy", request.Arguments);
            Assert.Contains(request.Arguments, argument =>
                argument.Contains("loudnorm=I=-16:TP=-1:LRA=11:measured_I=-18.81", StringComparison.Ordinal));
            Assert.Contains("128k", request.Arguments);
            Assert.Contains("48000", request.Arguments);
            Assert.Contains("2", request.Arguments);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeRunner(string output) : IProcessRunner
    {
        public List<ProcessStartRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 2) File.WriteAllBytes(output, [1, 2, 3]);
            string standardError = Requests.Count == 1
                ? """
                  {"input_i":"-18.81","input_tp":"-2.00","input_lra":"3.10","input_thresh":"-29.00","target_offset":"0.01"}
                  """
                : "";
            return Task.FromResult(new ProcessExecutionResult(
                ProcessExecutionStatus.Succeeded, 0, "", standardError, null, null, false, false));
        }
    }
}
