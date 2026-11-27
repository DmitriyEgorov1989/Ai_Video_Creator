using System.Text.Json;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Analysis;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.UnitTests.Analysis;

public sealed class AnalysisArtifactPublisherTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PublishesV2FeaturesAndReadsV1WithoutFeatures()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "video-factory-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            JobId jobId = JobId.New();
            ArtifactId originalId = ArtifactId.New();
            ArtifactId masterId = ArtifactId.New();
            SegmentId segmentId = SegmentId.New();
            AnalysisResult analysis = new(
                1,
                RevisionId.New(),
                originalId,
                masterId,
                [],
                [],
                [new Scene(new MasterRange(0, 1_000_000))],
                [new AllowedSegment(segmentId, new SourceMapping(
                    originalId,
                    new OriginalRange(0, 1_000_000),
                    masterId,
                    new MasterRange(0, 1_000_000)))])
            {
                Features = new AnalysisFeatureSet(
                [new SegmentAnalysisFeatures(segmentId, 0.8m, 0.7m, 0.6m, 0.5m, 0.4m, 0.9m, "dup-1")])
                {
                    FocalObservations = [new FocalObservation(segmentId, 500_000, 0.4m, 0.3m, 0.2m, 0.4m, 0.9m)]
                }
            };
            MemoryArtifactStore store = new();
            AnalysisArtifactPublisher publisher = new(new LocalArtifactStorage(workspace), store);

            AnalysisArtifactPublishResult published = await publisher.PublishAsync(jobId, analysis, CancellationToken.None);

            Assert.True(published.IsSuccess);
            Assert.NotNull(store.Record);
            ArtifactRecord record = store.Record!;
            string path = Path.Combine(workspace, record.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            AnalysisResult restored = JsonSerializer.Deserialize<AnalysisResult>(await File.ReadAllTextAsync(path), JsonOptions)!;
            Assert.Equal(0.8m, Assert.Single(restored.Features.Segments).SemanticRelevance);
            Assert.Equal(0.4m, Assert.Single(restored.Features.FocalObservations).X);
            Assert.Equal("analysis-v2", record.ToolVersion);

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Dictionary<string, JsonElement> v1 = document.RootElement.EnumerateObject()
                .Where(property => !string.Equals(property.Name, "features", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(property => property.Name, property => property.Value);
            AnalysisResult restoredV1 = JsonSerializer.Deserialize<AnalysisResult>(JsonSerializer.Serialize(v1), JsonOptions)!;
            Assert.Empty(restoredV1.Features.Segments);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class MemoryArtifactStore : IArtifactStore
    {
        public ArtifactRecord? Record { get; private set; }

        public Task<ArtifactRecord?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(Record?.ArtifactId == artifactId ? Record : null);

        public Task<ArtifactRecord?> FindLatestAsync(JobId jobId, string artifactType, CancellationToken cancellationToken) =>
            Task.FromResult(Record?.JobId == jobId && Record.ArtifactType == artifactType ? Record : null);

        public Task<IReadOnlyList<ArtifactRecord>> ListAsync(JobId jobId, string artifactType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtifactRecord>>(
                Record?.JobId == jobId && Record.ArtifactType == artifactType ? [Record] : []);

        public Task<ArtifactRegistrationResult> RegisterAsync(ArtifactRecord record, CancellationToken cancellationToken)
        {
            Record = record;
            return Task.FromResult<ArtifactRegistrationResult>(new ArtifactRegistrationResult.Created(record));
        }
    }
}
