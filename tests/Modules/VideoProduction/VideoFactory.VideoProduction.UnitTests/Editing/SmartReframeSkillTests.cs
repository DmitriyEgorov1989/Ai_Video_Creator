using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class SmartReframeSkillTests
{
    [Fact]
    public void WeightsMultipleSubjectsAndBoundsMovementBetweenKeyframes()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AllowedSegment segment = fixture.Analysis.AllowedSegments[0];
        AnalysisResult analysis = fixture.Analysis with
        {
            Features = fixture.Analysis.Features with
            {
                FocalObservations =
                [
                    new FocalObservation(segment.Id, 0, 0.15m, 0.4m, 0.2m, 0.2m, 1m),
                    new FocalObservation(segment.Id, 0, 0.65m, 0.4m, 0.2m, 0.2m, 1m),
                    new FocalObservation(segment.Id, 1_500_000, 0.85m, 0.4m, 0.1m, 0.2m, 1m),
                ],
            },
        };
        PacingDecision pacing = new(
            "pacing-director",
            "1.0.0",
            EditingDecisionStatus.Applied,
            [],
            [new PacingShot(segment.Id, new OutputRange(0, 60), Accent: false, RestPoint: false)],
            0m);

        SmartReframeDecision decision = Assert.IsType<SmartReframeDecision>(
            new SmartReframeSkill().Evaluate(new EditingContext(
                analysis,
                new ProductionSettings(null, true, false),
                PreviousDecisions: new Dictionary<string, EditingDecision> { [pacing.SkillId] = pacing })));

        ReframeTrack track = Assert.Single(decision.Tracks);
        Assert.Equal(ReframeFallback.Center, track.Fallback);
        Assert.Collection(
            track.Keyframes,
            first =>
            {
                Assert.Equal(0, first.OutputFrame);
                Assert.Equal(0.5m, first.FocalX);
                Assert.Equal(0.5m, first.FocalY);
            },
            second =>
            {
                Assert.Equal(30, second.OutputFrame);
                Assert.Equal(0.65m, second.FocalX);
                Assert.Equal(0.5m, second.FocalY);
            });
    }

    [Fact]
    public void UsesFitFallbackWhenTrackingObservationsAreMissing()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AllowedSegment segment = fixture.Analysis.AllowedSegments[0];
        PacingDecision pacing = new(
            "pacing-director",
            "1.0.0",
            EditingDecisionStatus.Applied,
            [],
            [new PacingShot(segment.Id, new OutputRange(0, 60), Accent: false, RestPoint: false)],
            0m);

        SmartReframeDecision decision = Assert.IsType<SmartReframeDecision>(
            new SmartReframeSkill().Evaluate(new EditingContext(
                fixture.Analysis,
                new ProductionSettings(null, true, false),
                PreviousDecisions: new Dictionary<string, EditingDecision> { [pacing.SkillId] = pacing })));

        ReframeTrack track = Assert.Single(decision.Tracks);
        Assert.Equal(ReframeFallback.Fit, track.Fallback);
        Assert.Empty(track.Keyframes);
        Assert.Contains(decision.Reasons, reason => reason.Code == "reframe.tracking_unavailable");
    }
}
