using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class CaptionDirectorSkillTests
{
    [Fact]
    public void MultiSourceCaptionDoesNotUseWordFromAnotherMasterAtSameTime()
    {
        ArtifactId originalA = ArtifactId.New();
        ArtifactId masterA = ArtifactId.New();
        ArtifactId originalB = ArtifactId.New();
        ArtifactId masterB = ArtifactId.New();
        SegmentId segmentB = SegmentId.New();
        MultiSourceAnalysisResult analysis = new(
            3,
            RevisionId.New(),
            [
                new AnalysisSource(
                    originalA,
                    masterA,
                    [new SpeechSegment("Только A", new MasterRange(0, 1_000_000), [new WordTiming("Только", new MasterRange(0, 500_000)), new WordTiming("A", new MasterRange(500_000, 1_000_000))])],
                    [], [], AnalysisFeatureSet.Empty),
                new AnalysisSource(originalB, masterB, [], [], [], AnalysisFeatureSet.Empty)
            ],
            [new AllowedSegment(segmentB, new SourceMapping(originalB, new OriginalRange(0, 1_000_000), masterB, new MasterRange(0, 1_000_000))) { IsApproved = true }]);
        PacingDecision pacing = new(
            "pacing-director", "1.0.0", EditingDecisionStatus.Applied, [],
            [new PacingShot(segmentB, new OutputRange(0, 30), true, false)], 0m);

        CaptionDirectorDecision decision = Assert.IsType<CaptionDirectorDecision>(
            new CaptionDirectorSkill().Evaluate(new EditingContext(
                null,
                new ProductionSettings(null, true, false),
                PreviousDecisions: new Dictionary<string, EditingDecision> { [pacing.SkillId] = pacing },
                MultiSourceAnalysis: analysis)));

        Assert.Equal(EditingDecisionStatus.Unavailable, decision.Status);
        Assert.Empty(decision.Groups);
    }

    [Fact]
    public void UsesSmartCutMappingSoFirstRetainedWordStartsAtFrameZero()
    {
        SegmentId segmentId = SegmentId.New();
        ArtifactId originalId = ArtifactId.New();
        ArtifactId masterId = ArtifactId.New();
        SourceMapping originalMapping = new(originalId, new OriginalRange(0, 2_000_000), masterId, new MasterRange(0, 2_000_000));
        SourceMapping cutMapping = new(originalId, new OriginalRange(500_000, 2_000_000), masterId, new MasterRange(500_000, 2_000_000));
        AnalysisResult analysis = new(
            2, RevisionId.New(), originalId, masterId,
            [new SpeechSegment("Первое слово", new MasterRange(500_000, 2_000_000),
                [new WordTiming("Первое", new MasterRange(500_000, 1_000_000)), new WordTiming("слово", new MasterRange(1_000_000, 2_000_000))])],
            [new MasterRange(0, 500_000)], [],
            [new AllowedSegment(segmentId, originalMapping) { IsApproved = true }]);
        SmartCutDecision cut = new("smart-cut", "1.0.0", EditingDecisionStatus.Applied, [],
            [new SmartCutEdit(segmentId, cutMapping, "pause")]);
        PacingDecision pacing = new("pacing-director", "1.0.0", EditingDecisionStatus.Applied, [],
            [new PacingShot(segmentId, new OutputRange(0, 45), true, false)], 0m);

        CaptionDirectorDecision decision = Assert.IsType<CaptionDirectorDecision>(new CaptionDirectorSkill().Evaluate(
            new EditingContext(analysis, new ProductionSettings(null, true, false), PreviousDecisions:
                new Dictionary<string, EditingDecision> { [cut.SkillId] = cut, [pacing.SkillId] = pacing })));

        Assert.Equal(0, decision.Groups[0].Words[0].Output.StartFrame);
    }

    [Fact]
    public void GroupsCyrillicWordsAtSemanticBoundaryAndMapsThemToOutputFrames()
    {
        SegmentId segmentId = SegmentId.New();
        ArtifactId originalId = ArtifactId.New();
        ArtifactId masterId = ArtifactId.New();
        AnalysisResult analysis = new(
            2,
            RevisionId.New(),
            originalId,
            masterId,
            [new SpeechSegment(
                "Привет мир. Это тест",
                new MasterRange(0, 2_000_000),
                [
                    new WordTiming("Привет", new MasterRange(0, 500_000)),
                    new WordTiming("мир.", new MasterRange(500_000, 1_000_000)),
                    new WordTiming("Это", new MasterRange(1_000_000, 1_500_000)),
                    new WordTiming("тест", new MasterRange(1_500_000, 2_000_000)),
                ])],
            [],
            [new Scene(new MasterRange(0, 2_000_000))],
            [new AllowedSegment(
                segmentId,
                new SourceMapping(
                    originalId,
                    new OriginalRange(0, 2_000_000),
                    masterId,
                    new MasterRange(0, 2_000_000))) { IsApproved = true }]);
        PacingDecision pacing = new(
            "pacing-director",
            "1.0.0",
            EditingDecisionStatus.Applied,
            [],
            [new PacingShot(segmentId, new OutputRange(0, 60), Accent: true, RestPoint: false)],
            0m);

        CaptionDirectorDecision decision = Assert.IsType<CaptionDirectorDecision>(
            new CaptionDirectorSkill().Evaluate(new EditingContext(
                analysis,
                new ProductionSettings(null, captionsEnabled: true, musicEnabled: false),
                PreviousDecisions: new Dictionary<string, EditingDecision> { [pacing.SkillId] = pacing })));

        Assert.Equal(EditingDecisionStatus.Applied, decision.Status);
        Assert.Collection(
            decision.Groups,
            first =>
            {
                Assert.Equal("Привет мир.", first.Text);
                Assert.Equal(new OutputRange(0, 30), first.Output);
                Assert.Equal(["Привет", "мир."], first.Words.Select(word => word.Text));
            },
            second =>
            {
                Assert.Equal("Это тест", second.Text);
                Assert.Equal(new OutputRange(30, 60), second.Output);
                Assert.Equal(2, second.MaxLines);
            });
    }
}
