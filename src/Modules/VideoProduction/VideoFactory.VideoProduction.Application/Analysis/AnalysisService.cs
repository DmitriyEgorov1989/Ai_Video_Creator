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
    private readonly IVideoFeatureAnalyzer? featureAnalyzer;

    /// <summary>Создаёт сервис анализа.</summary>
    public AnalysisService(
        IAudioActivityDetector activityDetector,
        IWhisperTranscriber transcriber,
        ISceneBoundaryDetector sceneDetector,
        IVideoFeatureAnalyzer? featureAnalyzer = null)
    {
        this.activityDetector = activityDetector;
        this.transcriber = transcriber;
        this.sceneDetector = sceneDetector;
        this.featureAnalyzer = featureAnalyzer;
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

        ImmutableArray<AllowedSegment> allowedSegments = CreateAllowedSegments(request, allowedRanges);
        VideoFeatureAnalysisResult visualFeatures = featureAnalyzer is null
            ? VideoFeatureAnalysisResult.Succeeded([])
            : await featureAnalyzer.AnalyzeAsync(
                new VideoFeatureAnalysisRequest(request.FilePath, allowedSegments),
                cancellationToken);
        if (!visualFeatures.IsSuccess)
        {
            return new AnalysisExecutionResult.Failed(
                visualFeatures.ErrorCode ?? "analysis.video_features_failed",
                visualFeatures.ErrorMessage ?? "Video feature analysis failed.");
        }

        AnalysisFeatureSet features = BuildFeatures(
            allowedSegments,
            visualFeatures.Features,
            speech);
        AnalysisResult analysis = new(
            1,
            CreateRevisionId(request, activity, speech, scenes.Scenes, features),
            request.OriginalAssetId,
            request.MasterAssetId,
            speech.ToImmutableArray(),
            activity.Pauses.ToImmutableArray(),
            scenes.Scenes.Select(static interval => new Scene(interval)).ToImmutableArray(),
            allowedSegments)
        {
            Features = features
        };

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
        IReadOnlyList<MasterRange> scenes,
        AnalysisFeatureSet features)
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
            string.Join(',', scenes),
            string.Join(',', features.Segments));
        byte[] bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return new RevisionId(new Guid(bytes[..16]));
    }

    private static AnalysisFeatureSet BuildFeatures(
        ImmutableArray<AllowedSegment> segments,
        IReadOnlyList<SegmentAnalysisFeatures> visualFeatures,
        IReadOnlyList<SpeechSegment> speech)
    {
        Dictionary<SegmentId, SegmentAnalysisFeatures> bySegment = visualFeatures
            .ToDictionary(feature => feature.SegmentId);
        Dictionary<SegmentId, string> normalizedTexts = segments.ToDictionary(
            segment => segment.Id,
            segment => NormalizeSpeech(speech, segment.Mapping.Master));
        Dictionary<string, int> duplicateCounts = normalizedTexts.Values
            .Where(static value => value.Length > 0)
            .GroupBy(static value => value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        ImmutableArray<SegmentAnalysisFeatures>.Builder result = ImmutableArray.CreateBuilder<SegmentAnalysisFeatures>();
        foreach (AllowedSegment segment in segments)
        {
            bySegment.TryGetValue(segment.Id, out SegmentAnalysisFeatures? visual);
            string text = normalizedTexts[segment.Id];
            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(static value => value.Trim('.', ',', '!', '?', ':', ';').ToLowerInvariant())
                .Where(static value => value.Length > 2)
                .ToArray();
            decimal semantic = words.Length == 0
                ? 0m
                : Math.Min(1m, words.Distinct(StringComparer.Ordinal).Count() / 8m);
            result.Add((visual ?? new SegmentAnalysisFeatures(segment.Id, 0.5m, 0.5m, 0.5m, 0.5m, 0.5m, 0.25m)) with
            {
                SemanticRelevance = semantic,
                DuplicateGroup = duplicateCounts.GetValueOrDefault(text) > 1
                    ? DuplicateGroup(text)
                    : null
            });
        }

        return new AnalysisFeatureSet(result.ToImmutable());
    }

    private static bool Intersects(MasterRange left, MasterRange right) =>
        left.StartMicroseconds < right.EndMicroseconds
        && right.StartMicroseconds < left.EndMicroseconds;

    private static string? DuplicateGroup(string text)
    {
        string normalized = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static word => word.Trim('.', ',', '!', '?', ':', ';').ToLowerInvariant()));
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    private static string NormalizeSpeech(IReadOnlyList<SpeechSegment> speech, MasterRange interval) =>
        string.Join(' ', speech
            .Where(item => Intersects(item.Interval, interval))
            .Select(item => item.Text)
            .SelectMany(static value => value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(static word => word.Trim('.', ',', '!', '?', ':', ';').ToLowerInvariant()));

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
