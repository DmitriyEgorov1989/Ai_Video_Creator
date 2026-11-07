using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Domain.Validation;

namespace VideoFactory.VideoProduction.UnitTests.Analysis;

public sealed class AnalysisValidatorTests
{
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
}
