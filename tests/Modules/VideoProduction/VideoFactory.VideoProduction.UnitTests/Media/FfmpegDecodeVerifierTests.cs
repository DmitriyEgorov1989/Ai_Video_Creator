using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Infrastructure.Media;

namespace VideoFactory.VideoProduction.UnitTests.Media;

public sealed class FfmpegDecodeVerifierTests
{
    [Fact]
    public async Task UsesNullOutputAndReportsSuccessfulDecode()
    {
        string path = Path.GetTempFileName();
        try
        {
            FakeRunner runner = new();
            IMediaDecodeVerifier verifier = new FfmpegDecodeVerifier(runner, "ffmpeg");

            MediaDecodeResult result = await verifier.VerifyAsync(
                new MediaDecodeRequest(path, TimeSpan.FromSeconds(5), 4_096),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.ErrorMessage);
            Assert.Contains("-f", runner.Request.Arguments);
            Assert.Contains("null", runner.Request.Arguments);
            Assert.Contains("-", runner.Request.Arguments);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public ProcessStartRequest Request { get; private set; } = null!;

        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartRequest request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new ProcessExecutionResult(
                ProcessExecutionStatus.Succeeded, 0, string.Empty, string.Empty,
                null, null, false, false));
        }
    }
}
