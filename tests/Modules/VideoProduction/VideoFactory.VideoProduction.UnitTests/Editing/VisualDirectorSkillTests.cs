using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class VisualDirectorSkillTests
{
    [Fact]
    public void KeepsZoomAndNonOverlappingTransitionsWhenCaptionsAreUnavailable()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        PacingDecision pacing = new("pacing-director", "1.0.0", EditingDecisionStatus.Applied, [],
            [
                new PacingShot(fixture.Analysis.AllowedSegments[0].Id, new OutputRange(0, 2), true, false),
                new PacingShot(fixture.Analysis.AllowedSegments[1].Id, new OutputRange(2, 4), false, false),
                new PacingShot(fixture.Analysis.AllowedSegments[0].Id, new OutputRange(4, 6), false, false),
            ], 0m);

        VisualDirectorDecision decision = Assert.IsType<VisualDirectorDecision>(new VisualDirectorSkill().Evaluate(
            new EditingContext(fixture.Analysis, new ProductionSettings(null, false, false), PreviousDecisions:
                new Dictionary<string, EditingDecision> { [pacing.SkillId] = pacing })));

        Assert.Contains(decision.Effects, effect => effect.Kind == VisualEffectKind.DynamicZoom);
        VisualEffectInstruction[] transitions = decision.Effects.Where(effect => effect.Kind == VisualEffectKind.VisualTransition).ToArray();
        Assert.True(transitions.Zip(transitions.Skip(1)).All(pair => pair.First.Output.EndFrame <= pair.Second.Output.StartFrame));
    }

    [Fact]
    public void EmitsClosedEffectsWithStableIdentityAndSkillProvenance()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        PacingDecision pacing = new(
            "pacing-director",
            "1.0.0",
            EditingDecisionStatus.Applied,
            [],
            [
                new PacingShot(fixture.Analysis.AllowedSegments[0].Id, new OutputRange(0, 30), Accent: true, RestPoint: false),
                new PacingShot(fixture.Analysis.AllowedSegments[1].Id, new OutputRange(30, 60), Accent: false, RestPoint: true),
            ],
            1m / 60m);
        CaptionDirectorDecision captions = new(
            "caption-director",
            "1.0.0",
            EditingDecisionStatus.Applied,
            [],
            [new CaptionGroup(
                "Known words",
                [new CaptionWordCue("Known", new OutputRange(0, 10)), new CaptionWordCue("words", new OutputRange(10, 20))],
                new OutputRange(0, 20),
                2,
                CaptionHighlightMode.ActiveWord,
                CaptionAnimation.Fade,
                CaptionPlacement.BottomSafe)]);

        VisualDirectorDecision decision = Assert.IsType<VisualDirectorDecision>(
            new VisualDirectorSkill().Evaluate(new EditingContext(
                fixture.Analysis,
                new ProductionSettings(null, true, false),
                PreviousDecisions: new Dictionary<string, EditingDecision>
                {
                    [pacing.SkillId] = pacing,
                    [captions.SkillId] = captions,
                })));

        Assert.Equal(EditingDecisionStatus.Applied, decision.Status);
        Assert.Collection(
            decision.Effects,
            zoom =>
            {
                Assert.Equal("visual-director-zoom-0", zoom.Id);
                Assert.Equal(VisualEffectKind.DynamicZoom, zoom.Kind);
                Assert.Equal(new OutputRange(0, 30), zoom.Output);
                Assert.Equal("visual-director", zoom.SourceSkillId);
            },
            highlight =>
            {
                Assert.Equal(VisualEffectKind.TextHighlight, highlight.Kind);
                Assert.Equal(new OutputRange(0, 20), highlight.Output);
            },
            transition =>
            {
                Assert.Equal(VisualEffectKind.VisualTransition, transition.Kind);
                Assert.Equal(new OutputRange(29, 31), transition.Output);
            });
    }
}
