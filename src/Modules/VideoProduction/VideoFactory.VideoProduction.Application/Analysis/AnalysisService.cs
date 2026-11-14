using System.Collections.Immutable;
using System.Security.Cryptography;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Analysis;

/// <summary>Создаёт воспроизводимый результат анализа одного source.</summary>
public sealed class AnalysisService : IAnalysisService
{
    private readonly IAudioActivityDetector activityDetector;
    private readonly IWhisperTranscriber transcriber;
    private readonly ISceneBoundaryDetector sceneDetector;

    /// <summary>Создаёт сервис анализа.</summary>
    public AnalysisService(
        IAudioActivityDetector activityDetector,
        IWhisperTranscriber transcriber,
        ISceneBoundaryDetector sceneDetector)
    {
        this.activityDetector = activityDetector;
        this.transcriber = transcriber;
        this.sceneDetector = sceneDetector;
    }

    /// <inheritdoc />
    public async Task<AnalysisExecutionResult> AnalyzeAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken)
    {
        request.Validate();

        AudioActivityResult activity = await activityDetector.DetectAsync(
            new AudioActivityRequest(request.FilePath, request.MasterDurationMicroseconds),
            cancellationToken);

        if (activity.ErrorCode is not null)
        {
            return new AnalysisExecutionResult.Failed(
                activity.ErrorCode,
                activity.ErrorMessage ?? "Audio activity detection failed.");
        }

        SceneDetectionResult scenes = await sceneDetector.DetectAsync(
            new SceneDetectionRequest(request.FilePath, request.MasterDurationMicroseconds),
            cancellationToken);

        if (!scenes.IsSuccess)
        {
            return new AnalysisExecutionResult.Failed(
                scenes.ErrorCode ?? "analysis.scenes_failed",
                scenes.ErrorMessage ?? "Scene detection failed.");
        }

        IReadOnlyList<SpeechSegment> speech = [];
        if (activity.HasUsefulSpeech)
        {
            TranscriptionResult transcription = await transcriber.TranscribeAsync(
                new TranscriptionRequest(request.FilePath, request.MasterDurationMicroseconds),
                cancellationToken);

            if (!transcription.IsSuccess)
            {
                return new AnalysisExecutionResult.Failed(
                    transcription.ErrorCode ?? "analysis.transcription_failed",
                    transcription.ErrorMessage ?? "Transcription failed.");
            }

            speech = transcription.Segments;
        }

        IReadOnlyList<MasterRange> allowedRanges = activity.HasUsefulSpeech
            ? speech.Select(static segment => segment.Interval).ToArray()
            : scenes.Scenes;

        if (allowedRanges.Count == 0)
        {
            allowedRanges = [new MasterRange(0, request.MasterDurationMicroseconds)];
        }

        AnalysisResult analysis = new(
            1,
            CreateRevisionId(request, activity, speech, scenes.Scenes),
            request.OriginalAssetId,
            request.MasterAssetId,
            speech.ToImmutableArray(),
            activity.Pauses.ToImmutableArray(),
            scenes.Scenes.Select(static interval => new Scene(interval)).ToImmutableArray(),
            CreateAllowedSegments(request, allowedRanges));

        IReadOnlyList<string> errors = Validate(analysis, request);
        return errors.Count == 0
            ? new AnalysisExecutionResult.Succeeded(analysis)
            : new AnalysisExecutionResult.Failed(
                "analysis.validation_failed",
                string.Join("; ", errors));
    }

    private static ImmutableArray<AllowedSegment> CreateAllowedSegments(
        AnalysisRequest request,
        IReadOnlyList<MasterRange> ranges)
    {
        return ranges
            .OrderBy(static range => range.StartMicroseconds)
            .Select(range => new AllowedSegment(
                CreateSegmentId(request.MasterAssetId, range),
                new SourceMapping(
                    request.OriginalAssetId,
                    MapToOriginal(range, request),
                    request.MasterAssetId,
                    range)))
            .ToImmutableArray();
    }

    private static OriginalRange MapToOriginal(
        MasterRange range,
        AnalysisRequest request)
    {
        decimal ratio = (decimal)request.OriginalDurationMicroseconds /
                        request.MasterDurationMicroseconds;
        long start = checked((long)Math.Round(
            range.StartMicroseconds * ratio,
            MidpointRounding.AwayFromZero));
        long end = checked((long)Math.Round(
            range.EndMicroseconds * ratio,
            MidpointRounding.AwayFromZero));

        if (end <= start)
        {
            end = checked(start + 1);
        }

        return new OriginalRange(start, Math.Min(end, request.OriginalDurationMicroseconds));
    }

    private static SegmentId CreateSegmentId(ArtifactId masterId, MasterRange range)
    {
        byte[] bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{masterId}:{range.StartMicroseconds}:{range.EndMicroseconds}"));
        return new SegmentId(new Guid(bytes[..16]));
    }

    private static RevisionId CreateRevisionId(
        AnalysisRequest request,
        AudioActivityResult activity,
        IReadOnlyList<SpeechSegment> speech,
        IReadOnlyList<MasterRange> scenes)
    {
        string input = string.Join(
            "|",
            request.OriginalAssetId,
            request.MasterAssetId,
            request.OriginalDurationMicroseconds,
            request.MasterDurationMicroseconds,
            activity.HasUsefulSpeech,
            string.Join(',', activity.SpeechWindows),
            string.Join(',', speech),
            string.Join(',', scenes));
        byte[] bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return new RevisionId(new Guid(bytes[..16]));
    }

    private static IReadOnlyList<string> Validate(
        AnalysisResult analysis,
        AnalysisRequest request)
    {
        List<string> errors = [];
        foreach (AllowedSegment segment in analysis.AllowedSegments)
        {
            if (segment.Mapping.Master.EndMicroseconds > request.MasterDurationMicroseconds)
            {
                errors.Add("Allowed segment exceeds master duration.");
            }

            if (segment.Mapping.Original.EndMicroseconds > request.OriginalDurationMicroseconds)
            {
                errors.Add("Allowed segment exceeds original duration.");
            }
        }

        foreach (SpeechSegment speech in analysis.Speech)
        {
            foreach (WordTiming word in speech.Words)
            {
                if (!speech.Interval.Contains(word.Interval))
                {
                    errors.Add("Word timestamp is outside speech segment.");
                }
            }
        }

        return errors;
    }
}
