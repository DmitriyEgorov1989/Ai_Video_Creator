using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class ReelsProfileCatalogTests
{
    [Fact]
    public void InstagramProfileFixesTechnicalOutputAndEveryStyleHasDistinctParameters()
    {
        ReelsProfileCatalog catalog = new();

        ReelsProfile profile = catalog.InstagramReels;

        Assert.Equal("instagram-reels", profile.Id);
        Assert.Equal(1080, profile.Width);
        Assert.Equal(1920, profile.Height);
        Assert.Equal(30, profile.FramesPerSecond);
        Assert.Equal("h264", profile.VideoCodec);
        Assert.Equal("aac", profile.AudioCodec);
        Assert.Equal(30, profile.TargetDurationSeconds);
        Assert.Equal((27, 33), (profile.MinimumSemanticDurationSeconds, profile.MaximumSemanticDurationSeconds));
        ReelsStyleConfiguration[] styles =
        [
            catalog.Resolve(ReelsStyle.ExpertClean, ValidPlanFixture.Create().Analysis),
            catalog.Resolve(ReelsStyle.DynamicViral, ValidPlanFixture.Create().Analysis),
            catalog.Resolve(ReelsStyle.Cinematic, ValidPlanFixture.Create().Analysis),
            catalog.Resolve(ReelsStyle.PremiumAds, ValidPlanFixture.Create().Analysis),
        ];
        Assert.Equal(4, styles.Select(style => style.ParameterFingerprint).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void AutoResolvesDeterministicallyFromKnownAnalysisFeatures()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AnalysisResult analysis = fixture.Analysis with
        {
            Features = new AnalysisFeatureSet(
            [new SegmentAnalysisFeatures(fixture.Analysis.AllowedSegments[0].Id, 0.8m, 0.7m, 0.6m, 0.8m, 0.5m, 0.9m)])
        };
        ReelsProfileCatalog catalog = new();

        ReelsStyleConfiguration first = catalog.Resolve(ReelsStyle.Auto, analysis);
        ReelsStyleConfiguration second = catalog.Resolve(ReelsStyle.Auto, analysis);

        Assert.Equal(ReelsStyle.Auto, first.RequestedStyle);
        Assert.Equal(ReelsStyle.DynamicViral, first.ResolvedStyle);
        Assert.Equal(first, second);
    }

    [Fact]
    public void SelectsNearestCompleteSpeechBoundaryInsteadOfHardThirtySecondCut()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AnalysisResult analysis = fixture.Analysis with
        {
            AllowedSegments =
            [
                fixture.Analysis.AllowedSegments[0] with
                {
                    Mapping = fixture.Analysis.AllowedSegments[0].Mapping with
                    {
                        Master = new VideoFactory.VideoProduction.Domain.Timeline.MasterRange(40_000_000, 60_000_000)
                    }
                },
                fixture.Analysis.AllowedSegments[1] with
                {
                    Mapping = fixture.Analysis.AllowedSegments[1].Mapping with
                    {
                        Master = new VideoFactory.VideoProduction.Domain.Timeline.MasterRange(70_000_000, 81_500_000)
                    }
                },
            ],
        };

        long selected = new ReelsProfileCatalog().SelectSemanticDurationMicroseconds(analysis);

        Assert.Equal(31_500_000, selected);
    }

    [Fact]
    public void SelectsSemanticBoundaryFromEffectiveSmartCutDurations()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AnalysisResult analysis = fixture.Analysis with
        {
            AllowedSegments =
            [
                WithDuration(fixture.Analysis.AllowedSegments[0], 20_000_000),
                WithDuration(fixture.Analysis.AllowedSegments[1], 20_000_000),
                WithDuration(fixture.Analysis.AllowedSegments[0] with { Id = SegmentId.New() }, 20_000_000),
            ],
        };
        IReadOnlyDictionary<SegmentId, SourceMapping> effectiveMappings = analysis.AllowedSegments
            .ToDictionary(segment => segment.Id, segment => segment.Mapping with
            {
                Master = new MasterRange(segment.Mapping.Master.StartMicroseconds, segment.Mapping.Master.StartMicroseconds + 10_000_000)
            });

        long selected = new ReelsProfileCatalog().SelectSemanticDurationMicroseconds(analysis, effectiveMappings);

        Assert.Equal(30_000_000, selected);
    }

    private static AllowedSegment WithDuration(AllowedSegment segment, long durationMicroseconds) => segment with
    {
        Mapping = segment.Mapping with
        {
            Master = new MasterRange(segment.Mapping.Master.StartMicroseconds, segment.Mapping.Master.StartMicroseconds + durationMicroseconds)
        }
    };
}
