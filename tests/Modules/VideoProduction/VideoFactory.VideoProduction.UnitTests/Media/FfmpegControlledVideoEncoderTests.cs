using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Infrastructure.Media;

namespace VideoFactory.VideoProduction.UnitTests.Media;

public sealed class FfmpegControlledVideoEncoderTests
{
    [Fact]
    public async Task UsesSingleProcessWithAgreedDeliveryProfile()
    {
        string root = Path.Combine(Path.GetTempPath(), $"delivery-encode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string output = Path.Combine(root, "encoded.mp4");
            FakeRunner runner = new(output);
            FfmpegControlledVideoEncoder encoder = new(runner, "ffmpeg", TimeSpan.FromMinutes(1), 4096);

            ControlledEncodeResult result = await encoder.EncodeAsync(
                new ControlledEncodeRequest(Path.Combine(root, "render.mp4"), output, 3_500_000, 96_000),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.Single(runner.Requests);
            Assert.Contains("3500k", runner.Requests[0].Arguments);
            Assert.Contains("96k", runner.Requests[0].Arguments);
            Assert.Contains("yuv420p", runner.Requests[0].Arguments);
            Assert.Contains("48000", runner.Requests[0].Arguments);
            Assert.Contains("+faststart", runner.Requests[0].Arguments);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FakeRunner(string output) : IProcessRunner
    {
        public List<ProcessStartRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(ProcessStartRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            File.WriteAllBytes(output, [1, 2, 3]);
            return Task.FromResult(new ProcessExecutionResult(ProcessExecutionStatus.Succeeded, 0, "", "", null, null, false, false));
        }
    }
}
