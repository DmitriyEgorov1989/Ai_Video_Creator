using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Infrastructure.Analysis;
using VideoFactory.VideoProduction.Infrastructure.Processes;

namespace VideoFactory.VideoProduction.IntegrationTests.Analysis;

public sealed class WhisperAcceptanceTests
{
    [WhisperAcceptanceFact]
    public async Task RealSpeechProducesSegmentsAndWordTimingsWithinMaster()
    {
        string path = Path.Combine(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!, "speech.mp4");
        Assert.True(File.Exists(path), "Prepare acceptance media before running Whisper acceptance tests.");
        IWhisperTranscriber transcriber = CreateTranscriber();

        TranscriptionResult result = await transcriber.TranscribeAsync(
            new TranscriptionRequest(path, 25_000_000), CancellationToken.None);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.NotEmpty(result.Segments);
        foreach (var segment in result.Segments)
        {
            Assert.False(string.IsNullOrWhiteSpace(segment.Text));
            Assert.InRange(segment.Interval.StartMicroseconds, 0, 24_999_999);
            Assert.InRange(segment.Interval.EndMicroseconds, segment.Interval.StartMicroseconds + 1, 25_000_000);
            Assert.NotEmpty(segment.Words);
            foreach (var word in segment.Words)
            {
                Assert.False(string.IsNullOrWhiteSpace(word.Text));
                Assert.True(segment.Interval.Contains(word.Interval));
            }
        }
    }

    [WhisperAcceptanceFact]
    public async Task CorruptMediaPreservesDecoderDiagnosticThroughTranscriber()
    {
        string path = Path.Combine(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!, "corrupt.mp4");
        TranscriptionResult result = await CreateTranscriber().TranscribeAsync(
            new TranscriptionRequest(path, 25_000_000), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("process.exit_code", result.ErrorCode);
        Assert.Contains("code 1", result.ErrorMessage);
        Assert.Contains("whisper.failed:", result.ErrorMessage);
        Assert.Contains("Invalid data", result.ErrorMessage);
    }

    private static IWhisperTranscriber CreateTranscriber() => new WhisperProcessTranscriber(
        new SystemProcessRunner(),
        Environment.GetEnvironmentVariable("WHISPER_TEST_PYTHON")!,
        Path.Combine(RepositoryRoot(), "scripts", "whisper-worker.py"),
        TimeSpan.FromSeconds(180), 1_048_576);

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ai_Video_Creator.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>Включает настоящее распознавание только при явной настройке локальной приёмки.</summary>
public sealed class WhisperAcceptanceFactAttribute : FactAttribute
{
    /// <summary>Пропускает проверку, если Python или каталог приёмочных медиа не заданы.</summary>
    public WhisperAcceptanceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WHISPER_TEST_PYTHON")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")))
        {
            Skip = "Set WHISPER_TEST_PYTHON and ACCEPTANCE_MEDIA_ROOT after preparing the model and media.";
        }
    }
}
