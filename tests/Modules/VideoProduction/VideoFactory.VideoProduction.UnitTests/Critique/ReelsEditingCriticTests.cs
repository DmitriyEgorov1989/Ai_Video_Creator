using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Critique;

public sealed class ReelsEditingCriticTests
{
    [Fact]
    public void ConfirmedBlackIntervalWithApprovedReplacementRequiresRemount()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AnalysisResult approvedAnalysis = fixture.Analysis with
        {
            AllowedSegments =
            [
                .. fixture.Analysis.AllowedSegments.Select(
                    segment => segment with { IsApproved = true })
            ]
        };
        MultiSourceAnalysisResult analysis = AnalysisContractAdapter.ToMultiSource(approvedAnalysis);
        EditPlan plan = fixture.Plan with
        {
            SchemaVersion = 3,
            AnalysisRevisionId = analysis.RevisionId,
            Sources =
            [
                new EditPlanSource(
                    approvedAnalysis.OriginalAssetId,
                    approvedAnalysis.MasterAssetId,
                    fixture.Assets[approvedAnalysis.MasterAssetId].Metadata.DurationMicroseconds)
            ]
        };
        OutputRange blackInterval = new(30, 60);

        ObjectiveCritiqueResult result = new ReelsEditingCritic().Critique(
            new ObjectiveCritiqueRequest(plan, analysis, [blackInterval]));

        Assert.Equal(CriticDecision.RemountRequired, result.Decision);
        CriticFinding finding = Assert.Single(result.Findings);
        Assert.Equal(CriticFindingKind.BlackFrames, finding.Kind);
        Assert.Equal(blackInterval, finding.Output);
        ReplaceVideoClipCorrection correction = Assert.IsType<ReplaceVideoClipCorrection>(
            Assert.Single(result.Corrections));
        Assert.Equal(plan.Video.Clips[0].SegmentId, correction.ExpectedSegmentId);
        Assert.Equal(analysis.AllowedSegments[1].Id, correction.ReplacementSegmentId);
        Assert.Equal(plan.Video.Clips[0].Output, correction.ClipOutput);
    }

    [Fact]
    public void ApplyingReplacementCreatesNewRevisionAndKeepsTracksSynchronized()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AnalysisResult approvedAnalysis = fixture.Analysis with
        {
            AllowedSegments =
            [
                .. fixture.Analysis.AllowedSegments.Select(
                    segment => segment with { IsApproved = true })
            ]
        };
        MultiSourceAnalysisResult analysis = AnalysisContractAdapter.ToMultiSource(approvedAnalysis);
        EditPlan plan = fixture.Plan with { SchemaVersion = 3 };
        ReplaceVideoClipCorrection correction = new(
            new OutputRange(30, 60),
            plan.Video.Clips[0].Output,
            plan.Video.Clips[0].SegmentId,
            analysis.AllowedSegments[1].Id,
            analysis.AllowedSegments[1].Mapping);

        EditPlanCorrectionResult result = new EditPlanCorrectionService().Apply(
            plan,
            analysis,
            correction);

        EditPlan corrected = Assert.IsType<EditPlanCorrectionResult.Applied>(result).Plan;
        Assert.NotEqual(plan.RevisionId, corrected.RevisionId);
        Assert.Equal(correction.ReplacementSegmentId, corrected.Video.Clips[0].SegmentId);
        Assert.Equal(correction.ReplacementMapping, corrected.Video.Clips[0].Mapping);
        Assert.Equal(correction.ReplacementSegmentId, corrected.Audio.SourceClips[0].SegmentId);
        Assert.Equal(plan.Video.Clips[0].SegmentId, plan.Audio.SourceClips[0].SegmentId);
        Assert.Equal(plan.Video.Clips[0].Output, corrected.Video.Clips[0].Output);
    }

    [Fact]
    public void ConfirmedFrozenIntervalWithApprovedReplacementRequiresRemount()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        MultiSourceAnalysisResult analysis = AnalysisContractAdapter.ToMultiSource(
            fixture.Analysis with
            {
                AllowedSegments =
                [
                    .. fixture.Analysis.AllowedSegments.Select(
                        segment => segment with { IsApproved = true })
                ]
            });
        OutputRange frozenInterval = new(30, 60);
        ObjectiveCritiqueRequest request = new(fixture.Plan, analysis, [])
        {
            FrozenIntervals = [frozenInterval]
        };

        ObjectiveCritiqueResult result = new ReelsEditingCritic().Critique(request);

        Assert.Equal(CriticDecision.RemountRequired, result.Decision);
        CriticFinding finding = Assert.Single(result.Findings);
        Assert.Equal(CriticFindingKind.FrozenFrames, finding.Kind);
        Assert.IsType<ReplaceVideoClipCorrection>(Assert.Single(result.Corrections));
    }

    [Fact]
    public void AudioPeakProducesBoundedGainCorrection()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        MultiSourceAnalysisResult analysis = AnalysisContractAdapter.ToMultiSource(fixture.Analysis);
        ObjectiveCritiqueRequest request = new(fixture.Plan, analysis, [])
        {
            MeasuredTruePeakDbtp = 0m,
            MaximumTruePeakDbtp = -1m
        };

        ObjectiveCritiqueResult critique = new ReelsEditingCritic().Critique(request);

        Assert.Equal(CriticDecision.RemountRequired, critique.Decision);
        ScaleAudioGainCorrection correction = Assert.IsType<ScaleAudioGainCorrection>(
            Assert.Single(critique.Corrections));
        Assert.InRange(correction.Factor, 0.89m, 0.90m);

        EditPlan corrected = Assert.IsType<EditPlanCorrectionResult.Applied>(
            new EditPlanCorrectionService().Apply(fixture.Plan, analysis, correction)).Plan;
        EditPlan repeated = Assert.IsType<EditPlanCorrectionResult.Applied>(
            new EditPlanCorrectionService().Apply(fixture.Plan, analysis, correction)).Plan;
        Assert.All(corrected.Audio.SourceClips, clip => Assert.True(clip.Gain < 1m));
        Assert.All(corrected.Audio.MusicClips, clip => Assert.True(clip.Gain < 0.25m));
        Assert.Equal(corrected.RevisionId, repeated.RevisionId);
    }

    [Fact]
    public void PlannedStaticSegmentDoesNotProduceFrozenFrameFinding()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AllowedSegment staticSegment = fixture.Analysis.AllowedSegments[0] with
        {
            IsApproved = true,
            Confidence = 1m,
            Features = new SegmentQualitySnapshot(0m, 0.8m, 0.5m, 0m, true, [])
        };
        MultiSourceAnalysisResult analysis = AnalysisContractAdapter.ToMultiSource(
            fixture.Analysis with
            {
                AllowedSegments = [staticSegment, fixture.Analysis.AllowedSegments[1]]
            });
        ObjectiveCritiqueRequest request = new(fixture.Plan, analysis, [])
        {
            FrozenIntervals = [new OutputRange(30, 60)]
        };

        ObjectiveCritiqueResult result = new ReelsEditingCritic().Critique(request);

        Assert.Equal(CriticDecision.Accepted, result.Decision);
        Assert.Empty(result.Findings);
    }
}
