using System.Diagnostics;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Infrastructure.Analysis;
using VideoFactory.VideoProduction.Infrastructure.Processes;

namespace VideoFactory.VideoProduction.IntegrationTests.Analysis;

public sealed class WhisperProcessLifetimeTests
{
    [WhisperPythonTheory]
    [InlineData(true, "process.cancelled")]
    [InlineData(false, "process.timeout")]
    public async Task StopsWorkerAndChildThroughTranscriber(bool cancel, string expectedCode)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"whisper lifetime {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string readyPath = Path.Combine(directory, "ready.json");
        using CancellationTokenSource cancellation = new();
        IWhisperTranscriber transcriber = new WhisperProcessTranscriber(
            new SystemProcessRunner(), Environment.GetEnvironmentVariable("WHISPER_TEST_PYTHON")!,
            Path.Combine(WhisperAcceptanceTests.RepositoryRoot(), "tests", "Modules", "VideoProduction",
                "VideoFactory.VideoProduction.IntegrationTests", "Analysis", "Fixtures", "slow-whisper.py"),
            TimeSpan.FromSeconds(cancel ? 30 : 5), 65_536);
        Task<TranscriptionResult> execution = transcriber.TranscribeAsync(
            new TranscriptionRequest(readyPath, 25_000_000), cancellation.Token);

        try
        {
            await WaitUntilAsync(() => File.Exists(readyPath) || execution.IsCompleted);
            Assert.True(File.Exists(readyPath), "Worker must start its child before cancellation or timeout.");
            int[] pids = JsonSerializer.Deserialize<int[]>(await File.ReadAllTextAsync(readyPath))!;
            Assert.Equal(2, pids.Length);
            Assert.All(pids, pid => Assert.True(IsRunning(pid)));
            if (cancel)
            {
                cancellation.Cancel();
            }

            TranscriptionResult result = await execution;

            Assert.False(result.IsSuccess);
            Assert.Equal(expectedCode, result.ErrorCode);
            await WaitUntilAsync(() => pids.All(pid => !IsRunning(pid)));
            Assert.All(pids, pid => Assert.False(IsRunning(pid)));
        }
        finally
        {
            cancellation.Cancel();
            await execution;
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, deadline.Token);
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Включает проверки жизненного цикла Python при явно заданном интерпретаторе.</summary>
public sealed class WhisperPythonTheoryAttribute : TheoryAttribute
{
    /// <summary>Пропускает проверку, если путь WHISPER_TEST_PYTHON не задан.</summary>
    public WhisperPythonTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WHISPER_TEST_PYTHON")))
        {
            Skip = "Set WHISPER_TEST_PYTHON to run worker cancellation and timeout tests.";
        }
    }
}
