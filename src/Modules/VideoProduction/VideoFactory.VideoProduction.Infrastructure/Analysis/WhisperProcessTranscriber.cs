using System.Collections.Immutable;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.Analysis;

/// <summary>Запускает versioned Whisper worker через безопасный process runner.</summary>
public sealed class WhisperProcessTranscriber : IWhisperTranscriber
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IProcessRunner processRunner;
    private readonly string pythonPath;
    private readonly string workerPath;
    private readonly TimeSpan timeout;
    private readonly int maxOutputBytes;

    /// <summary>Создаёт Whisper adapter.</summary>
    public WhisperProcessTranscriber(
        IProcessRunner processRunner,
        string pythonPath,
        string workerPath,
        TimeSpan timeout,
        int maxOutputBytes)
    {
        this.processRunner = processRunner;
        this.pythonPath = pythonPath;
        this.workerPath = workerPath;
        this.timeout = timeout;
        this.maxOutputBytes = maxOutputBytes;
    }

    /// <inheritdoc />
    public async Task<TranscriptionResult> TranscribeAsync(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ProcessExecutionResult result = await processRunner.RunAsync(
            new ProcessStartRequest(
                pythonPath,
                [workerPath, "--input", request.FilePath, "--duration-microseconds", request.DurationMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                timeout,
                maxOutputBytes),
            cancellationToken);

        if (!result.IsSuccess)
        {
            string message = result.ErrorMessage ?? "Whisper worker failed.";
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                message = $"{message}{Environment.NewLine}{result.StandardError}";
            }

            return TranscriptionResult.Failed(
                result.ErrorCode ?? "analysis.whisper_failed",
                message);
        }

        WhisperDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<WhisperDocument>(result.StandardOutput, JsonOptions);
        }
        catch (JsonException exception)
        {
            return TranscriptionResult.Failed(
                "analysis.whisper_invalid_json",
                $"Whisper returned invalid JSON: {exception.Message}");
        }

        if (document is null || document.SchemaVersion != 1 || document.Segments is null)
        {
            return TranscriptionResult.Failed(
                "analysis.whisper_invalid_contract",
                "Whisper output must contain schemaVersion 1 and segments.");
        }

        try
        {
            ImmutableArray<SpeechSegment> segments = document.Segments
                .Select(ConvertSegment)
                .ToImmutableArray();
            return TranscriptionResult.Succeeded(segments);
        }
        catch (ArgumentException exception)
        {
            return TranscriptionResult.Failed(
                "analysis.whisper_invalid_timestamps",
                exception.Message);
        }
    }

    private static SpeechSegment ConvertSegment(WhisperSegment segment)
    {
        if (string.IsNullOrWhiteSpace(segment.Text))
        {
            throw new ArgumentException("Whisper segment text cannot be empty.");
        }

        MasterRange interval = new(segment.StartMicroseconds, segment.EndMicroseconds);
        ImmutableArray<WordTiming> words = (segment.Words ?? [])
            .Select(word => new WordTiming(
                word.Text ?? string.Empty,
                new MasterRange(word.StartMicroseconds, word.EndMicroseconds)))
            .ToImmutableArray();
        return new SpeechSegment(segment.Text, interval, words);
    }

    private sealed record WhisperDocument(
        int SchemaVersion,
        IReadOnlyList<WhisperSegment>? Segments);

    private sealed record WhisperSegment(
        string? Text,
        long StartMicroseconds,
        long EndMicroseconds,
        IReadOnlyList<WhisperWord>? Words);

    private sealed record WhisperWord(
        string? Text,
        long StartMicroseconds,
        long EndMicroseconds);
}
