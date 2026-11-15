using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Infrastructure.Analysis;

namespace VideoFactory.VideoProduction.IntegrationTests.Analysis;

public sealed class WhisperProcessTranscriberTests
{
    [Fact]
    public async Task PreservesWorkerExitCodeAndOriginalDiagnostic()
    {
        IWhisperTranscriber transcriber = new WhisperProcessTranscriber(
            new StubRunner(new ProcessExecutionResult(
                ProcessExecutionStatus.NonZeroExitCode, 42, "", "test worker failure",
                "process.exit_code", "External process exited with code 42.", false, false)),
            "python", "scripts/whisper-worker.py", TimeSpan.FromSeconds(10), 65_536);

        TranscriptionResult result = await transcriber.TranscribeAsync(
            new TranscriptionRequest("master.mp4", 25_000_000), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("process.exit_code", result.ErrorCode);
        Assert.Contains("42", result.ErrorMessage);
        Assert.Contains("test worker failure", result.ErrorMessage);
    }

    [Fact]
    public async Task ReadsVersionOneSegmentAndWordIntervalsInMicroseconds()
    {
        IWhisperTranscriber transcriber = WithOutput("""
            {"schemaVersion":1,"segments":[{"text":"Пример речи",
              "startMicroseconds":2000000,"endMicroseconds":3500000,
              "words":[{"text":"Пример","startMicroseconds":2000000,"endMicroseconds":2700000}]}]}
            """);

        TranscriptionResult result = await transcriber.TranscribeAsync(
            new TranscriptionRequest("master.mp4", 25_000_000), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var segment = Assert.Single(result.Segments);
        Assert.Equal("Пример речи", segment.Text);
        Assert.Equal(2_000_000, segment.Interval.StartMicroseconds);
        Assert.Equal(3_500_000, segment.Interval.EndMicroseconds);
        var word = Assert.Single(segment.Words);
        Assert.Equal("Пример", word.Text);
        Assert.Equal(2_000_000, word.Interval.StartMicroseconds);
        Assert.Equal(2_700_000, word.Interval.EndMicroseconds);
    }

    [Theory]
    [InlineData("not JSON", "analysis.whisper_invalid_json")]
    [InlineData("{\"schemaVersion\":2,\"segments\":[]}", "analysis.whisper_invalid_contract")]
    [InlineData("{\"schemaVersion\":1}", "analysis.whisper_invalid_contract")]
    [InlineData("{\"schemaVersion\":1,\"segments\":[{\"text\":\"word\",\"startMicroseconds\":2,\"endMicroseconds\":1}]}",
        "analysis.whisper_invalid_timestamps")]
    public async Task RejectsMalformedWorkerResponse(string json, string expectedCode)
    {
        TranscriptionResult result = await WithOutput(json).TranscribeAsync(
            new TranscriptionRequest("master.mp4", 25_000_000), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.NotEmpty(result.ErrorMessage!);
    }

    private static IWhisperTranscriber WithOutput(string output) => new WhisperProcessTranscriber(
        new StubRunner(new ProcessExecutionResult(
            ProcessExecutionStatus.Succeeded, 0, output, "", null, null, false, false)),
        "python", "scripts/whisper-worker.py", TimeSpan.FromSeconds(10), 65_536);

    private sealed class StubRunner(ProcessExecutionResult result) : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartRequest request, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
