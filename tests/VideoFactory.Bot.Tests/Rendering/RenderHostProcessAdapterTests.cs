using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Infrastructure.Rendering;

namespace VideoFactory.Bot.Tests.Rendering;

public sealed class RenderHostProcessAdapterTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"type\":42}")]
    [InlineData("{\"type\":\"progress\",\"stage\":\"render\",\"percent\":\"bad\"}")]
    public async Task MalformedEventIsExpectedProtocolFailure(string json)
    {
        RenderHostProcessAdapter adapter = new(new FakeProcessRunner(new ProcessExecutionResult(
            ProcessExecutionStatus.Succeeded, 0, json, "diagnostic", null, null, false, false)), "node", "render.js");
        RenderHostResult result = await adapter.RenderAsync(new RenderHostRequest("request.json", "output.mp4", TimeSpan.FromSeconds(1), 4096), CancellationToken.None);
        Assert.Equal(RenderHostResultKind.Failed, result.Kind);
        Assert.Equal("render.protocol_invalid", result.ErrorCode);
        Assert.Equal("diagnostic", result.StandardError);
    }

    [Fact]
    public async Task RenderAsync_ReturnsCompletedEventAndProgress()
    {
        string outputPath = Path.Combine(Path.GetTempPath(), $"render-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(outputPath, [1]);

        try
        {
            FakeProcessRunner runner = new(new ProcessExecutionResult(
                ProcessExecutionStatus.Succeeded,
                0,
                $"{{\"type\":\"progress\",\"stage\":\"render\",\"percent\":42}}\n{{\"type\":\"completed\",\"outputPath\":\"{outputPath.Replace("\\", "\\\\")}\",\"durationFrames\":180}}\n",
                string.Empty,
                null,
                null,
                false,
                false));
            RenderHostProcessAdapter adapter = new(runner, "node", "render-host/dist/index.js");

            RenderHostResult result = await adapter.RenderAsync(
                new RenderHostRequest("request.json", outputPath, TimeSpan.FromSeconds(10), 10_000),
                CancellationToken.None);

            Assert.Equal(RenderHostResultKind.Succeeded, result.Kind);
            Assert.Equal(outputPath, result.OutputPath);
            Assert.Equal(180, result.DurationFrames);
            Assert.Single(result.Progress);
            Assert.Equal("render", result.Progress[0].Stage);
            Assert.Equal(42, result.Progress[0].Percent);
            Assert.Equal("node", runner.Request!.FileName);
            Assert.Equal(["render-host/dist/index.js", "render", "--job", "request.json"], runner.Request.Arguments);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RenderAsync_PreservesFailedEvent()
    {
        FakeProcessRunner runner = new(new ProcessExecutionResult(
            ProcessExecutionStatus.NonZeroExitCode,
            1,
            "{\"type\":\"failed\",\"code\":\"asset_invalid\",\"message\":\"Asset is missing.\"}\n",
            "asset_invalid: Asset is missing.",
            "process.exit_code",
            "External process exited with code 1.",
            false,
            false));
        RenderHostProcessAdapter adapter = new(runner, "node", "render-host/dist/index.js");

        RenderHostResult result = await adapter.RenderAsync(
            new RenderHostRequest("request.json", "output.mp4", TimeSpan.FromSeconds(10), 10_000),
            CancellationToken.None);

        Assert.Equal(RenderHostResultKind.Failed, result.Kind);
        Assert.Equal("asset_invalid", result.ErrorCode);
        Assert.Equal("Asset is missing.", result.ErrorMessage);
    }

    [Fact]
    public async Task RenderAsync_RejectsSuccessfulProcessWithoutCompletedEvent()
    {
        FakeProcessRunner runner = new(new ProcessExecutionResult(
            ProcessExecutionStatus.Succeeded,
            0,
            "{\"type\":\"progress\",\"stage\":\"render\",\"percent\":100}\n",
            string.Empty,
            null,
            null,
            false,
            false));
        RenderHostProcessAdapter adapter = new(runner, "node", "render-host/dist/index.js");

        RenderHostResult result = await adapter.RenderAsync(
            new RenderHostRequest("request.json", "output.mp4", TimeSpan.FromSeconds(10), 10_000),
            CancellationToken.None);

        Assert.Equal(RenderHostResultKind.Failed, result.Kind);
        Assert.Equal("render.completed_event_missing", result.ErrorCode);
    }

    private sealed class FakeProcessRunner(ProcessExecutionResult result) : IProcessRunner
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
