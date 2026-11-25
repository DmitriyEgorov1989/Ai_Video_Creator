using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class MultiClipDirectorSkillTests
{
    [Fact]
    public void SelectsKnownSegmentsFromTwoDistinctSources()
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
                Source(originalA, masterA, Feature(segmentA, 0.8m)),
                Source(originalB, masterB, Feature(segmentB, 0.7m))
            ],
            [Segment(segmentA, originalA, masterA), Segment(segmentB, originalB, masterB)]);

        MultiClipDecision decision = Assert.IsType<MultiClipDecision>(
            new MultiClipDirectorSkill().Evaluate(new EditingContext(
                null,
                new ProductionSettings(null, true, false),
                MultiSourceAnalysis: analysis)));

        Assert.Equal(EditingDecisionStatus.Applied, decision.Status);
        Assert.Equal(2, decision.Selections.Length);
        Assert.Equal([0, 1], decision.Selections.Select(selection => selection.SourceOrder).Order().ToArray());
        Assert.All(decision.Selections, selection =>
            Assert.Contains(analysis.AllowedSegments, segment =>
                segment.Id == selection.SegmentId && segment.Mapping == selection.Mapping));
    }

    [Fact]
    public void ConfirmedDuplicateGroupKeepsHigherScoredSegment()
    {
        ArtifactId originalA = ArtifactId.New();
        ArtifactId masterA = ArtifactId.New();
        ArtifactId originalB = ArtifactId.New();
        ArtifactId masterB = ArtifactId.New();
        SegmentId weaker = SegmentId.New();
        SegmentId stronger = SegmentId.New();
        MultiSourceAnalysisResult analysis = new(
            3,
            RevisionId.New(),
            [
                Source(originalA, masterA, Feature(weaker, 0.4m) with { DuplicateGroup = "same-shot" }),
                Source(originalB, masterB, Feature(stronger, 0.9m) with { DuplicateGroup = "same-shot" })
            ],
            [Segment(weaker, originalA, masterA), Segment(stronger, originalB, masterB)]);

        MultiClipDecision decision = Assert.IsType<MultiClipDecision>(
            new MultiClipDirectorSkill().Evaluate(new EditingContext(
                null, new ProductionSettings(null, true, false), MultiSourceAnalysis: analysis)));

        Assert.Equal(stronger, Assert.Single(decision.Selections).SegmentId);
    }

    [Fact]
    public void LegacySingleSourceReturnsCompatibleAppliedDecision()
    {
        ArtifactId original = ArtifactId.New();
        ArtifactId master = ArtifactId.New();
        SegmentId segmentId = SegmentId.New();
        AnalysisResult analysis = new(
            2, RevisionId.New(), original, master, [], [], [],
            [Segment(segmentId, original, master)])
        {
            Features = new AnalysisFeatureSet([Feature(segmentId, 0.7m)])
        };

        MultiClipDecision decision = Assert.IsType<MultiClipDecision>(
            new MultiClipDirectorSkill().Evaluate(new EditingContext(
                analysis, new ProductionSettings(null, true, false))));

        Assert.Equal(EditingDecisionStatus.Applied, decision.Status);
        Assert.Equal(segmentId, Assert.Single(decision.Selections).SegmentId);
        Assert.Equal(0, decision.Selections[0].SourceOrder);
    }

    [Fact]
    public void HigherScoreCanReorderSourcesAndEqualScoreUsesUploadOrder()
    {
        ArtifactId originalA = ArtifactId.New();
        ArtifactId masterA = ArtifactId.New();
        ArtifactId originalB = ArtifactId.New();
        ArtifactId masterB = ArtifactId.New();
        SegmentId segmentA = SegmentId.New();
        SegmentId segmentB = SegmentId.New();
        MultiSourceAnalysisResult reordered = new(
            3, RevisionId.New(),
            [Source(originalA, masterA, Feature(segmentA, 0.5m)), Source(originalB, masterB, Feature(segmentB, 0.9m))],
            [Segment(segmentA, originalA, masterA), Segment(segmentB, originalB, masterB)]);
        MultiSourceAnalysisResult tied = reordered with
        {
            Sources =
            [
                Source(originalA, masterA, Feature(segmentA, 0.7m)),
                Source(originalB, masterB, Feature(segmentB, 0.7m))
            ]
        };
        MultiClipDirectorSkill skill = new();

        MultiClipDecision reorderedDecision = Assert.IsType<MultiClipDecision>(skill.Evaluate(
            new EditingContext(null, new ProductionSettings(null, true, false), MultiSourceAnalysis: reordered)));
        MultiClipDecision tiedDecision = Assert.IsType<MultiClipDecision>(skill.Evaluate(
            new EditingContext(null, new ProductionSettings(null, true, false), MultiSourceAnalysis: tied)));

        Assert.Equal([1, 0], reorderedDecision.Selections.Select(selection => selection.SourceOrder).ToArray());
        Assert.Equal([0, 1], tiedDecision.Selections.Select(selection => selection.SourceOrder).ToArray());
        Assert.Equal([0, 1], tiedDecision.Selections.Select(selection => selection.OutputOrder).ToArray());
    }

    private static AnalysisSource Source(
        ArtifactId originalId,
        ArtifactId masterId,
        SegmentAnalysisFeatures feature) =>
        new(originalId, masterId, [], [], [], new AnalysisFeatureSet([feature]));

    private static SegmentAnalysisFeatures Feature(SegmentId id, decimal score) =>
        new(id, score, score, score, score, score, 1m);

    private static AllowedSegment Segment(SegmentId id, ArtifactId originalId, ArtifactId masterId) =>
        new(id, new SourceMapping(
            originalId,
            new OriginalRange(0, 5_000_000),
            masterId,
            new MasterRange(0, 5_000_000)))
        {
            IsApproved = true,
            Confidence = 1m
        };
}
