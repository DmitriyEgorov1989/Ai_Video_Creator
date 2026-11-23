using System.Collections.Immutable;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Analysis;

public sealed class AnalysisServiceTests
{
    [Fact]
    public async Task SpeechInputCreatesSpeechAndAllowedSegment()
    {
        ArtifactId originalId = ArtifactId.New();
        ArtifactId masterId = ArtifactId.New();
        AnalysisService service = new(
            new FakeAudioActivityDetector(new AudioActivityResult(
                true,
                [new MasterRange(1_000_000, 3_000_000)],
                [new MasterRange(0, 1_000_000)])),
            new FakeTranscriber(new SpeechSegment[] {
                new SpeechSegment(
                    "hello",
                    new MasterRange(1_000_000, 3_000_000),
                     new[] { new WordTiming("hello", new MasterRange(1_000_000, 1_800_000)) }
                         .ToImmutableArray()) }),
            new FakeSceneDetector(new MasterRange[] { new(0, 4_000_000) }));

        AnalysisExecutionResult result = await service.AnalyzeAsync(
            new AnalysisRequest(
                originalId,
                masterId,
                "master.mp4",
                4_000_000,
                4_000_000),
            CancellationToken.None);

        AnalysisExecutionResult.Succeeded succeeded =
            Assert.IsType<AnalysisExecutionResult.Succeeded>(result);

        Assert.NotEmpty(succeeded.Analysis.Speech);
        AllowedSegment segment = Assert.Single(succeeded.Analysis.AllowedSegments);
        Assert.Equal(originalId, segment.Mapping.OriginalAssetId);
        Assert.Equal(masterId, segment.Mapping.MasterAssetId);
        Assert.Equal(1_000_000, segment.Mapping.Master.StartMicroseconds);
        Assert.Equal(3_000_000, segment.Mapping.Master.EndMicroseconds);
    }

    [Fact]
    public async Task AnalysisV2LinksSegmentEvidenceAndQualityFeatures()
    {
        AnalysisService service = new(
            new FakeAudioActivityDetector(new AudioActivityResult(
                true,
                [new MasterRange(1_000_000, 3_000_000)],
                [new MasterRange(0, 1_000_000)])),
            new FakeTranscriber(new SpeechSegment[] {
                new SpeechSegment(
                    "hello world",
                    new MasterRange(1_000_000, 3_000_000),
                    [
                        new WordTiming("hello", new MasterRange(1_000_000, 1_500_000)),
                        new WordTiming("world", new MasterRange(1_600_000, 2_000_000))
                    ]) }),
            new FakeSceneDetector([new MasterRange(0, 4_000_000)]),
            new FakeFeatureExtractor(new SegmentQualityFeatures(
                0.8m, 0.7m, 0.9m, 0.6m, true, [])));

        AnalysisExecutionResult result = await service.AnalyzeAsync(
            new AnalysisRequest(ArtifactId.New(), ArtifactId.New(), "master.mp4", 4_000_000, 4_000_000),
            CancellationToken.None);

        AnalysisResult analysis = Assert.IsType<AnalysisExecutionResult.Succeeded>(result).Analysis;
        AllowedSegment segment = Assert.Single(analysis.AllowedSegments);
        SpeechSegment speech = Assert.Single(analysis.Speech);

        Assert.Equal(2, analysis.SchemaVersion);
        Assert.Equal(segment.Id, speech.SegmentId);
        Assert.Equal(segment.Id, segment.SpeechSegmentIds.Single());
        Assert.Equal(0.8m, segment.Features.Motion);
        Assert.Equal(0.9m, segment.Features.Exposure);
        Assert.True(segment.IsApproved);
        Assert.Contains("speech-aligned", segment.Reasons);
    }

    [Fact]
    public async Task InputWithoutUsefulSpeechDoesNotInvokeTranscriber()
    {
        FakeTranscriber transcriber = new([]);
        AnalysisService service = new(
            new FakeAudioActivityDetector(new AudioActivityResult(
                false,
                [],
                [new MasterRange(0, 4_000_000)])),
            transcriber,
            new FakeSceneDetector([new MasterRange(0, 4_000_000)]));

        AnalysisExecutionResult result = await service.AnalyzeAsync(
            new AnalysisRequest(
                ArtifactId.New(),
                ArtifactId.New(),
                "silent.mp4",
                4_000_000,
                4_000_000),
            CancellationToken.None);

        AnalysisExecutionResult.Succeeded succeeded =
            Assert.IsType<AnalysisExecutionResult.Succeeded>(result);

        Assert.Empty(succeeded.Analysis.Speech);
        Assert.Empty(transcriber.Calls);
        Assert.NotEmpty(succeeded.Analysis.AllowedSegments);
    }

    [Fact]
    public async Task RepeatedSpeechIsRejectedByDeterministicFallback()
    {
        AnalysisService service = new(
            new FakeAudioActivityDetector(new AudioActivityResult(true, [], [])),
            new FakeTranscriber([
                new SpeechSegment("same words", new MasterRange(0, 1_000_000), [new WordTiming("same", new MasterRange(0, 500_000))]),
                new SpeechSegment("same   words", new MasterRange(1_000_000, 2_000_000), [new WordTiming("words", new MasterRange(1_000_000, 1_500_000))])]),
            new FakeSceneDetector([new MasterRange(0, 2_000_000)]));

        AnalysisResult analysis = Assert.IsType<AnalysisExecutionResult.Succeeded>(await service.AnalyzeAsync(
            new AnalysisRequest(ArtifactId.New(), ArtifactId.New(), "master.mp4", 2_000_000, 2_000_000),
            CancellationToken.None)).Analysis;

        AllowedSegment[] segments = analysis.AllowedSegments.ToArray();
        Assert.Equal(2, segments.Length);
        Assert.True(segments[0].IsApproved);
        Assert.False(segments[1].IsApproved);
        Assert.Contains("duplicate-of:", segments[1].Reasons.Single(reason => reason.StartsWith("duplicate-of:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AnalysisPopulatesFeaturesFromVideoAnalyzerAndTranscript()
    {
        ArtifactId originalId = ArtifactId.New();
        ArtifactId masterId = ArtifactId.New();
        SegmentAnalysisFeatures visual = new(
            SegmentId.New(), 0m, 0.8m, 0.7m, 0.6m, 0.5m, 0.9m);
        AnalysisService service = new(
            new FakeAudioActivityDetector(new AudioActivityResult(
                true, [new MasterRange(0, 2_000_000), new MasterRange(2_000_000, 4_000_000)], [])),
            new FakeTranscriber(
            [
                new SpeechSegment(
                    "clear useful point",
                    new MasterRange(0, 2_000_000),
                    [new WordTiming("clear", new MasterRange(100_000, 300_000))]),
                new SpeechSegment(
                    "clear useful point",
                    new MasterRange(2_000_000, 4_000_000),
                    [new WordTiming("clear", new MasterRange(2_100_000, 2_300_000))])
            ]),
            new FakeSceneDetector([new MasterRange(0, 2_000_000), new MasterRange(2_000_000, 4_000_000)]),
            null,
            new FakeVideoFeatureAnalyzer(visual));

        AnalysisExecutionResult.Succeeded result = Assert.IsType<AnalysisExecutionResult.Succeeded>(
            await service.AnalyzeAsync(
                new AnalysisRequest(originalId, masterId, "master.mp4", 4_000_000, 4_000_000),
                CancellationToken.None));

        SegmentAnalysisFeatures features = result.Analysis.Features.Segments[0];
        Assert.Equal(result.Analysis.AllowedSegments[0].Id, features.SegmentId);
        Assert.Equal(0.8m, features.Sharpness);
        Assert.True(features.SemanticRelevance > 0m);
        Assert.NotNull(features.DuplicateGroup);
    }

    private sealed class FakeAudioActivityDetector(AudioActivityResult result)
        : IAudioActivityDetector
    {
        public Task<AudioActivityResult> DetectAsync(
            AudioActivityRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class FakeTranscriber(IReadOnlyList<SpeechSegment> segments)
        : IWhisperTranscriber
    {
        public List<TranscriptionRequest> Calls { get; } = [];

        public Task<TranscriptionResult> TranscribeAsync(
            TranscriptionRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add(request);
            return Task.FromResult(TranscriptionResult.Succeeded(segments));
        }
    }

    private sealed class FakeSceneDetector(IReadOnlyList<MasterRange> scenes)
        : ISceneBoundaryDetector
    {
        public Task<SceneDetectionResult> DetectAsync(
            SceneDetectionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(SceneDetectionResult.Succeeded(scenes));
    }

    private sealed class FakeFeatureExtractor(SegmentQualityFeatures features) : IAnalysisFeatureExtractor
    {
        public Task<SegmentQualityFeatures> ExtractAsync(
            FeatureExtractionRequest request,
            CancellationToken cancellationToken) => Task.FromResult(features);
    }

    private sealed class FakeVideoFeatureAnalyzer(SegmentAnalysisFeatures result)
        : IVideoFeatureAnalyzer
    {
        public Task<VideoFeatureAnalysisResult> AnalyzeAsync(
            VideoFeatureAnalysisRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(VideoFeatureAnalysisResult.Succeeded(
                request.Segments.Select(segment => result with { SegmentId = segment.Id }).ToArray()));
    }
}
