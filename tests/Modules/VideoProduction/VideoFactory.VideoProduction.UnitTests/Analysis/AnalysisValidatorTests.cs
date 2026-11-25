using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Domain.Validation;
using VideoFactory.VideoProduction.Application.Analysis;

namespace VideoFactory.VideoProduction.UnitTests.Analysis;

public sealed class AnalysisValidatorTests
{
    [Fact]
    public void ValidMultiSourceAnalysisAllowsIndependentLocalTimelines()
    {
        ArtifactId originalA = ArtifactId.New();
        ArtifactId masterA = ArtifactId.New();
        ArtifactId originalB = ArtifactId.New();
        ArtifactId masterB = ArtifactId.New();
        SegmentId segmentA = SegmentId.New();
        SegmentId segmentB = SegmentId.New();
        MultiSourceAnalysisResult analysis = new(
            3,
            RevisionId.New(),
            [
                CreateSource(originalA, masterA, segmentA, "first"),
                CreateSource(originalB, masterB, segmentB, "second")
            ],
            [
                CreateAllowedSegment(originalA, masterA, segmentA),
                CreateAllowedSegment(originalB, masterB, segmentB)
            ]);
        Dictionary<ArtifactId, MediaAsset> assets = new()
        {
            [originalA] = new(originalA, CreateMetadata(10_000_000)),
            [masterA] = new(masterA, CreateMetadata(10_000_000)),
            [originalB] = new(originalB, CreateMetadata(10_000_000)),
            [masterB] = new(masterB, CreateMetadata(10_000_000))
        };

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(analysis, assets);

        Assert.Empty(issues);
    }

    [Fact]
    public void MultiSourceAnalysisRejectsDuplicateOriginalMasterPair()
    {
        ArtifactId original = ArtifactId.New();
        ArtifactId master = ArtifactId.New();
        AnalysisSource source = CreateSource(original, master, SegmentId.New(), "first");
        MultiSourceAnalysisResult analysis = new(3, RevisionId.New(), [source, source], []);
        Dictionary<ArtifactId, MediaAsset> assets = new()
        {
            [original] = new(original, CreateMetadata(10_000_000)),
            [master] = new(master, CreateMetadata(10_000_000))
        };

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(analysis, assets);

        Assert.Contains(issues, issue => issue.Code == "analysis.source_duplicate");
    }

    [Fact]
    public void MultiSourceAnalysisRejectsCrossSourceMapping()
    {
        ArtifactId originalA = ArtifactId.New();
        ArtifactId masterA = ArtifactId.New();
        ArtifactId originalB = ArtifactId.New();
        ArtifactId masterB = ArtifactId.New();
        SegmentId segmentId = SegmentId.New();
        MultiSourceAnalysisResult analysis = new(
            3,
            RevisionId.New(),
            [
                CreateSource(originalA, masterA, SegmentId.New(), "first"),
                CreateSource(originalB, masterB, SegmentId.New(), "second")
            ],
            [CreateAllowedSegment(originalA, masterB, segmentId)]);
        Dictionary<ArtifactId, MediaAsset> assets = new()
        {
            [originalA] = new(originalA, CreateMetadata(10_000_000)),
            [masterA] = new(masterA, CreateMetadata(10_000_000)),
            [originalB] = new(originalB, CreateMetadata(10_000_000)),
            [masterB] = new(masterB, CreateMetadata(10_000_000))
        };

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(analysis, assets);

        Assert.Contains(issues, issue => issue.Code == "analysis.source_mapping_invalid");
    }

    [Fact]
    public void MultiSourceAnalysisRejectsDuplicateSegmentIdAcrossSources()
    {
        ArtifactId originalA = ArtifactId.New();
        ArtifactId masterA = ArtifactId.New();
        ArtifactId originalB = ArtifactId.New();
        ArtifactId masterB = ArtifactId.New();
        SegmentId shared = SegmentId.New();
        MultiSourceAnalysisResult analysis = new(
            3,
            RevisionId.New(),
            [
                CreateSource(originalA, masterA, shared, "first"),
                CreateSource(originalB, masterB, shared, "second")
            ],
            [
                CreateAllowedSegment(originalA, masterA, shared),
                CreateAllowedSegment(originalB, masterB, shared)
            ]);
        Dictionary<ArtifactId, MediaAsset> assets = new()
        {
            [originalA] = new(originalA, CreateMetadata(10_000_000)),
            [masterA] = new(masterA, CreateMetadata(10_000_000)),
            [originalB] = new(originalB, CreateMetadata(10_000_000)),
            [masterB] = new(masterB, CreateMetadata(10_000_000))
        };

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(analysis, assets);

        Assert.Contains(issues, issue => issue.Code == "analysis.segment_id_duplicate");
    }

    [Fact]
    public void LegacyAnalysisAdaptsToExactlyOneSource()
    {
        AnalysisFixture fixture = AnalysisFixture.Create();

        MultiSourceAnalysisResult adapted = AnalysisContractAdapter.ToMultiSource(fixture.Analysis);

        AnalysisSource source = Assert.Single(adapted.Sources);
        Assert.Equal(3, adapted.SchemaVersion);
        Assert.Equal(fixture.Analysis.RevisionId, adapted.RevisionId);
        Assert.Equal(fixture.Analysis.OriginalAssetId, source.OriginalAssetId);
        Assert.Equal(fixture.Analysis.MasterAssetId, source.MasterAssetId);
        Assert.Equal(fixture.Analysis.AllowedSegments, adapted.AllowedSegments);
    }

    [Fact]
    public void ValidAnalysisHasNoIssues()
    {
        AnalysisFixture fixture = AnalysisFixture.Create();

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(
            fixture.Analysis,
            fixture.Assets);

        Assert.Empty(issues);
    }

    [Fact]
    public void DuplicateSegmentIdIsReportedInsteadOfThrowing()
    {
        AnalysisFixture fixture = AnalysisFixture.Create();
        AllowedSegment segment = fixture.Analysis.AllowedSegments[0];
        AnalysisResult analysis = fixture.Analysis with
        {
            AllowedSegments = [segment, segment]
        };

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(analysis, fixture.Assets);

        Assert.Contains(issues, issue => issue.Code == "analysis.segment_id_duplicate");
    }

    [Fact]
    public void SegmentOutsideMasterDurationIsRejected()
    {
        AnalysisFixture fixture = AnalysisFixture.Create();
        AllowedSegment segment = fixture.Analysis.AllowedSegments[0] with
        {
            Mapping = fixture.Analysis.AllowedSegments[0].Mapping with
            {
                Master = new MasterRange(17_000_000, 19_000_000)
            }
        };
        AnalysisResult analysis = fixture.Analysis with { AllowedSegments = [segment] };

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(analysis, fixture.Assets);

        Assert.Contains(issues, issue => issue.Code == "timeline.master_out_of_bounds");
    }

    [Fact]
    public void MasterWithoutVideoStreamIsRejected()
    {
        AnalysisFixture fixture = AnalysisFixture.Create();
        Dictionary<ArtifactId, MediaAsset> assets = new(fixture.Assets)
        {
            [fixture.Analysis.MasterAssetId] = new MediaAsset(
                fixture.Analysis.MasterAssetId,
                new MediaMetadata(
                    "m4a",
                    18_000_000,
                    [],
                    [new AudioStreamMetadata(0, "aac", 48_000, 2)]))
        };

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(fixture.Analysis, assets);

        Assert.Contains(issues, issue => issue.Code == "asset.video_stream_missing");
    }

    [Fact]
    public void MappingWithoutTimeTransformationMustPreserveDuration()
    {
        AnalysisFixture fixture = AnalysisFixture.Create();
        AllowedSegment segment = fixture.Analysis.AllowedSegments[0] with
        {
            Mapping = fixture.Analysis.AllowedSegments[0].Mapping with
            {
                Master = new MasterRange(0, 2_000_000)
            }
        };
        AnalysisResult analysis = fixture.Analysis with { AllowedSegments = [segment] };

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(analysis, fixture.Assets);

        Assert.Contains(issues, issue => issue.Code == "timeline.mapping_duration_mismatch");
    }

    [Fact]
    public void EmptyAndOverlappingWordsAreRejected()
    {
        AnalysisFixture fixture = AnalysisFixture.Create();
        SpeechSegment speech = fixture.Analysis.Speech[0] with
        {
            Words =
            [
                new WordTiming("first", new MasterRange(0, 700_000)),
                new WordTiming(" ", new MasterRange(600_000, 900_000))
            ]
        };
        AnalysisResult analysis = fixture.Analysis with { Speech = [speech] };

        ImmutableArray<ValidationIssue> issues = AnalysisValidator.Validate(analysis, fixture.Assets);

        Assert.Contains(issues, issue => issue.Code == "analysis.word_text_empty");
        Assert.Contains(issues, issue => issue.Code == "analysis.words_not_ordered");
    }

    private sealed record AnalysisFixture(
        AnalysisResult Analysis,
        IReadOnlyDictionary<ArtifactId, MediaAsset> Assets)
    {
        public static AnalysisFixture Create()
        {
            ArtifactId originalId = ArtifactId.New();
            ArtifactId masterId = ArtifactId.New();
            MediaMetadata originalMetadata = CreateMetadata(20_000_000);
            MediaMetadata masterMetadata = CreateMetadata(18_000_000);
            Dictionary<ArtifactId, MediaAsset> assets = new()
            {
                [originalId] = new MediaAsset(originalId, originalMetadata),
                [masterId] = new MediaAsset(masterId, masterMetadata)
            };
            SourceMapping mapping = new(
                originalId,
                new OriginalRange(1_000_000, 4_000_000),
                masterId,
                new MasterRange(0, 3_000_000));
            AnalysisResult analysis = new(
                1,
                RevisionId.New(),
                originalId,
                masterId,
                [new SpeechSegment("hello", new MasterRange(0, 1_000_000), [new WordTiming("hello", new MasterRange(0, 1_000_000))])],
                [new MasterRange(1_000_000, 1_500_000)],
                [new Scene(new MasterRange(0, 3_000_000))],
                [new AllowedSegment(SegmentId.New(), mapping)]);

            return new AnalysisFixture(analysis, assets);
        }

        private static MediaMetadata CreateMetadata(long durationMicroseconds) => new(
            "mp4",
            durationMicroseconds,
            [new VideoStreamMetadata(0, "h264", 1080, 1920, new FrameRate(30, 1), false, 0)],
            [new AudioStreamMetadata(1, "aac", 48_000, 2)]);
    }

    private static AnalysisSource CreateSource(
        ArtifactId originalId,
        ArtifactId masterId,
        SegmentId segmentId,
        string text) =>
        new(
            originalId,
            masterId,
            [new SpeechSegment(text, new MasterRange(0, 1_000_000), [new WordTiming(text, new MasterRange(0, 1_000_000))]) { SegmentId = segmentId }],
            [new MasterRange(1_000_000, 2_000_000)],
            [new Scene(new MasterRange(0, 5_000_000))],
            AnalysisFeatureSet.Empty);

    private static AllowedSegment CreateAllowedSegment(
        ArtifactId originalId,
        ArtifactId masterId,
        SegmentId segmentId) =>
        new(
            segmentId,
            new SourceMapping(
                originalId,
                new OriginalRange(0, 5_000_000),
                masterId,
                new MasterRange(0, 5_000_000)))
        {
            SpeechSegmentIds = [segmentId],
            SceneIndexes = [0],
            IsApproved = true,
            Confidence = 1m
        };

    private static MediaMetadata CreateMetadata(long durationMicroseconds) => new(
        "mp4",
        durationMicroseconds,
        [new VideoStreamMetadata(0, "h264", 1080, 1920, new FrameRate(30, 1), false, 0)],
        [new AudioStreamMetadata(1, "aac", 48_000, 2)]);
}
