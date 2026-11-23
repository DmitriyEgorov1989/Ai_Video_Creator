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
    private readonly IAnalysisFeatureExtractor? featureExtractor;

    /// <summary>Создаёт сервис анализа.</summary>
    public AnalysisService(
        IAudioActivityDetector activityDetector,
        IWhisperTranscriber transcriber,
        ISceneBoundaryDetector sceneDetector,
        IAnalysisFeatureExtractor? featureExtractor = null)
    {
        this.activityDetector = activityDetector;
        this.transcriber = transcriber;
        this.sceneDetector = sceneDetector;
        this.featureExtractor = featureExtractor;
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

        IReadOnlyList<SpeechSegment> linkedSpeech = speech
            .Select(item => item with
            {
                SegmentId = CreateSegmentId(request.MasterAssetId, item.Interval)
            })
            .ToArray();

        ImmutableArray<AllowedSegment> allowedSegments = await CreateAllowedSegmentsAsync(
            request,
            allowedRanges,
            linkedSpeech,
            scenes.Scenes,
            cancellationToken);

        AnalysisResult analysis = new(
            2,
            CreateRevisionId(request, activity, linkedSpeech, scenes.Scenes, allowedSegments),
            request.OriginalAssetId,
            request.MasterAssetId,
            linkedSpeech.ToImmutableArray(),
            activity.Pauses.ToImmutableArray(),
            scenes.Scenes.Select(static interval => new Scene(interval)).ToImmutableArray(),
            allowedSegments);

        IReadOnlyList<string> errors = Validate(analysis, request);
        return errors.Count == 0
            ? new AnalysisExecutionResult.Succeeded(analysis)
            : new AnalysisExecutionResult.Failed(
                "analysis.validation_failed",
                string.Join("; ", errors));
    }

    private async Task<ImmutableArray<AllowedSegment>> CreateAllowedSegmentsAsync(
        AnalysisRequest request,
        IReadOnlyList<MasterRange> ranges,
        IReadOnlyList<SpeechSegment> speech,
        IReadOnlyList<MasterRange> scenes,
        CancellationToken cancellationToken)
    {
        List<AllowedSegment> result = [];
        Dictionary<string, SegmentId> firstSpeechByText = [];
        HashSet<SegmentId> duplicateSpeechIds = [];
        Dictionary<SegmentId, SegmentId> duplicateFirstById = [];
        foreach (SpeechSegment item in speech)
        {
            string key = NormalizeSpeech(item.Text);
            if (key.Length == 0) continue;
            SegmentId id = item.SegmentId!.Value;
            if (firstSpeechByText.TryGetValue(key, out SegmentId firstId))
            {
                duplicateSpeechIds.Add(id);
                duplicateFirstById[id] = firstId;
            }
            else
                firstSpeechByText[key] = id;
        }

        foreach (MasterRange range in ranges.OrderBy(static range => range.StartMicroseconds))
        {
            SegmentId id = CreateSegmentId(request.MasterAssetId, range);
            ImmutableArray<SegmentId> speechIds = speech
                .Where(item => Overlaps(item.Interval, range))
                .Select(item => item.SegmentId!.Value)
                .ToImmutableArray();
            ImmutableArray<int> sceneIndexes = scenes
                .Select((scene, index) => (scene, index))
                .Where(item => Overlaps(item.scene, range))
                .Select(item => item.index)
                .ToImmutableArray();
            bool duplicate = speechIds.Any(duplicateSpeechIds.Contains);
            SegmentId? duplicateOf = speechIds
                .Where(duplicateFirstById.ContainsKey)
                .Select(id => duplicateFirstById[id])
                .FirstOrDefault();

            SegmentQualityFeatures features = featureExtractor is null
                ? SegmentQualityFeatures.Fallback
                : await featureExtractor.ExtractAsync(
                    new FeatureExtractionRequest(request.FilePath, range),
                    cancellationToken);
            List<string> reasons = [];
            if (speechIds.Length > 0) reasons.Add("speech-aligned");
            if (sceneIndexes.Length > 0) reasons.Add("scene-aligned");
            if (duplicate)
                reasons.Add(duplicateOf is { } first && !first.IsEmpty ? $"duplicate-of:{first}" : "duplicate-fallback");
            reasons.AddRange(features.TechnicalIssues);

            result.Add(new AllowedSegment(
                id,
                new SourceMapping(
                    request.OriginalAssetId,
                    MapToOriginal(range, request),
                    request.MasterAssetId,
                    range))
            {
                SpeechSegmentIds = speechIds,
                SceneIndexes = sceneIndexes,
                Features = features.ToSnapshot(),
                Confidence = CalculateConfidence(features, speechIds.Length > 0, sceneIndexes.Length > 0),
                Reasons = reasons.Distinct(StringComparer.Ordinal).ToImmutableArray(),
                IsApproved = features.TechnicalValidity && !duplicate
            });
        }

        return [.. result];
    }

    private static decimal CalculateConfidence(
        SegmentQualityFeatures features,
        bool hasSpeech,
        bool hasScene) =>
        Math.Clamp(
            (features.Motion + features.Sharpness + features.Exposure + features.AudioEnergy) / 4m
            + (hasSpeech ? 0.2m : 0m)
            + (hasScene ? 0.1m : 0m),
            0m,
            1m);

    private static bool Overlaps(MasterRange left, MasterRange right) =>
        left.StartMicroseconds < right.EndMicroseconds
        && right.StartMicroseconds < left.EndMicroseconds;

    private static string NormalizeSpeech(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToUpperInvariant();

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
        IReadOnlyList<AllowedSegment> allowedSegments)
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
            string.Join(',', allowedSegments.Select(static segment =>
                $"{segment.Id}:{segment.Confidence}:{segment.IsApproved}:{string.Join('|', segment.Reasons)}:" +
                $"{segment.Features.Motion}:{segment.Features.Sharpness}:{segment.Features.Exposure}:{segment.Features.AudioEnergy}")));
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
