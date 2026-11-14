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
}
