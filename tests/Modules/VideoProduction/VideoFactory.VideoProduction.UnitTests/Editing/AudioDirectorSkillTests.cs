using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class AudioDirectorSkillTests
{
    [Fact]
    public void ProducesSpeechPolicyAndDucksOnlyKnownMusicAsset()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();

        AudioDirectorDecision decision = Assert.IsType<AudioDirectorDecision>(
            new AudioDirectorSkill().Evaluate(new EditingContext(
                fixture.Analysis,
                new ProductionSettings(null, captionsEnabled: true, musicEnabled: true),
                MusicAssetId: "music-1",
                KnownAssetIds: new HashSet<string>(StringComparer.Ordinal) { "music-1" })));

        Assert.Equal(EditingDecisionStatus.Applied, decision.Status);
        Assert.Equal(-16m, decision.TargetLoudnessLufs);
        Assert.Equal(-1m, decision.TruePeakDbtp);
        Assert.Equal(6, decision.FadeInFrames);
        Assert.Equal(6, decision.FadeOutFrames);
        Assert.False(decision.Denoise);
        Assert.Equal("music-1", decision.MusicAssetId);
        Assert.Equal(0.5m, decision.Ducking);
    }

    [Fact]
    public void NoSpeechAndNoMusicProduceNoAudioPresentationPolicy()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        EditingContext context = new(
            fixture.Analysis with { Speech = [] },
            new ProductionSettings(null, captionsEnabled: true, musicEnabled: false));
        AudioDirectorDecision decision = Assert.IsType<AudioDirectorDecision>(
            new AudioDirectorSkill().Evaluate(context));
        EditingEvaluation evaluation = new([decision]);

        ReelsPresentationPlan presentation = ReelsPresentationPlan.FromEvaluation(
            new ReelsProfileCatalog().Resolve(ReelsStyle.Auto, context.Analysis!),
            evaluation);

        Assert.Equal(EditingDecisionStatus.Unavailable, decision.Status);
        Assert.Null(presentation.Audio);
    }
}
