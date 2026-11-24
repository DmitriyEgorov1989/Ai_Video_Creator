using System.Collections.Immutable;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class EditingSkillEngineTests
{
    [Fact]
    public void DisabledSkillDoesNotExecuteAndEnabledSkillIsDeterministic()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        TestSkill skill = new();
        DisabledSkill disabledSkill = new();
        EditingSkillEngine engine = new([disabledSkill, skill]);
        EditingContext context = new(fixture.Analysis, new ProductionSettings(null, true, false));
        EditingSkillConfiguration configuration = new(
        [
            new EditingSkillConfigurationEntry(disabledSkill.Id, Enabled: false),
            new EditingSkillConfigurationEntry(skill.Id, Enabled: true)
        ]);

        EditingEvaluation first = engine.Evaluate(context, configuration);
        EditingEvaluation second = engine.Evaluate(context, configuration);

        Assert.Equal(
            first.Decisions.Select(decision => (decision.SkillId, decision.SkillVersion, decision.Status, Reasons: decision.Reasons.Select(reason => (reason.Code, reason.Message)).ToArray())),
            second.Decisions.Select(decision => (decision.SkillId, decision.SkillVersion, decision.Status, Reasons: decision.Reasons.Select(reason => (reason.Code, reason.Message)).ToArray())));
        Assert.Collection(
            first.Decisions,
            skipped => Assert.Equal(EditingDecisionStatus.Skipped, skipped.Status),
            applied =>
            {
                Assert.Equal(EditingDecisionStatus.Applied, applied.Status);
                Assert.IsType<TestDecision>(applied);
            });
        Assert.Equal(2, skill.ExecutionCount);
        Assert.Equal(0, disabledSkill.ExecutionCount);
    }

    [Fact]
    public void MissingAnalysisProducesTypedUnavailableDecision()
    {
        TestSkill skill = new();
        EditingSkillEngine engine = new([skill]);
        EditingContext context = new(null, new ProductionSettings(null, true, false));

        EditingEvaluation evaluation = engine.Evaluate(
            context,
            new EditingSkillConfiguration([new EditingSkillConfigurationEntry(skill.Id, Enabled: true)]));

        EditingDecision decision = Assert.Single(evaluation.Decisions);
        Assert.Equal(EditingDecisionStatus.Unavailable, decision.Status);
        Assert.Contains(decision.Reasons, reason => reason.Code == "skill.analysis_missing");
        Assert.Equal(0, skill.ExecutionCount);
    }

    [Fact]
    public void SpeechTighteningRemovesOnlyPauseBoundariesAndPreservesMapping()
    {
        SegmentId segmentId = SegmentId.New();
        ArtifactId originalId = ArtifactId.New();
        ArtifactId masterId = ArtifactId.New();
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
            [new SpeechSegment(
                "hello world",
                new MasterRange(500_000, 2_500_000),
                [
                    new WordTiming("hello", new MasterRange(500_000, 1_000_000)),
                    new WordTiming("world", new MasterRange(1_500_000, 2_500_000))
                ])],
            [new MasterRange(0, 500_000), new MasterRange(2_500_000, 3_000_000)],
            [new Scene(new MasterRange(0, 3_000_000))],
            [new AllowedSegment(segmentId, mapping)]);

        SpeechTighteningSkill skill = new();
        EditingEvaluation evaluation = new EditingSkillEngine([skill]).Evaluate(
            new EditingContext(analysis, new ProductionSettings(null, true, false)),
            new EditingSkillConfiguration([new EditingSkillConfigurationEntry(skill.Id, Enabled: true)]));

        SpeechTighteningDecision decision = Assert.IsType<SpeechTighteningDecision>(Assert.Single(evaluation.Decisions));
        SpeechTighteningEdit edit = Assert.Single(decision.Edits);
        Assert.Equal(segmentId, edit.SegmentId);
        Assert.Equal(new MasterRange(500_000, 2_500_000), edit.Mapping.Master);
        Assert.Equal(new OriginalRange(1_500_000, 3_500_000), edit.Mapping.Original);
    }

    [Fact]
    public void SpeechTighteningDoesNotRemoveBoundaryWhenPauseCoverageIsIncomplete()
    {
        SegmentId segmentId = SegmentId.New();
        ArtifactId originalId = ArtifactId.New();
        ArtifactId masterId = ArtifactId.New();
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
            [new SpeechSegment(
                "hello",
                new MasterRange(500_000, 1_000_000),
                [new WordTiming("hello", new MasterRange(500_000, 1_000_000))])],
            [new MasterRange(0, 250_000), new MasterRange(1_000_000, 3_000_000)],
            [new Scene(new MasterRange(0, 3_000_000))],
            [new AllowedSegment(segmentId, mapping)]);
        SpeechTighteningSkill skill = new();

        SpeechTighteningDecision decision = Assert.IsType<SpeechTighteningDecision>(
            Assert.Single(new EditingSkillEngine([skill]).Evaluate(
                new EditingContext(analysis, new ProductionSettings(null, true, false)),
                new EditingSkillConfiguration([new EditingSkillConfigurationEntry(skill.Id, Enabled: true)])).Decisions));

        SpeechTighteningEdit edit = Assert.Single(decision.Edits);
        Assert.Equal(new MasterRange(0, 1_000_000), edit.Mapping.Master);
    }

    [Fact]
    public void StoryAndHookUseKnownTranscriptSegmentsDeterministically()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AnalysisResult analysis = fixture.Analysis with
        {
            Speech =
            [
                new SpeechSegment(
                    "Useful opening",
                    new MasterRange(0, 1_000_000),
                    [new WordTiming("Useful", new MasterRange(100_000, 400_000)), new WordTiming("opening", new MasterRange(500_000, 900_000))]),
                new SpeechSegment(
                    "Useful conclusion",
                    new MasterRange(3_000_000, 4_000_000),
                    [new WordTiming("Useful", new MasterRange(3_100_000, 3_400_000)), new WordTiming("conclusion", new MasterRange(3_500_000, 3_900_000))])
            ],
            Features = new AnalysisFeatureSet(
            [
                new SegmentAnalysisFeatures(fixture.Analysis.AllowedSegments[0].Id, 0.9m, 0.7m, 0.8m, 0.5m, 0.6m, 0.95m),
                new SegmentAnalysisFeatures(fixture.Analysis.AllowedSegments[1].Id, 0.4m, 0.9m, 0.8m, 0.7m, 0.6m, 0.95m)
            ])
        };

        EditingEvaluation evaluation = new EditingSkillEngine(
        [new SemanticStorySkill(), new HookBuilderSkill()]).Evaluate(
            new EditingContext(analysis, new ProductionSettings(6, true, false), "Keep the useful point"),
            new EditingSkillConfiguration(
            [
                new("semantic-story", true),
                new("hook-builder", true)
            ]));

        SemanticStoryDecision story = Assert.IsType<SemanticStoryDecision>(evaluation.Decisions[0]);
        HookDecision hook = Assert.IsType<HookDecision>(evaluation.Decisions[1]);
        Assert.Equal(EditingDecisionStatus.Applied, story.Status);
        Assert.Equal(fixture.Analysis.AllowedSegments[0].Id, story.Segments[0].SegmentId);
        Assert.Equal("Useful opening", story.Segments[0].Text);
        Assert.Equal(fixture.Analysis.AllowedSegments[0].Id, hook.Candidates[0].SegmentId);
        Assert.Equal(0.9m, hook.Candidates[0].Score);
    }

    [Fact]
    public void SmartCutNeverCutsInsideAWordAndPacingIsContiguous()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AnalysisResult analysis = fixture.Analysis with
        {
            Speech =
            [new SpeechSegment(
                "hello world",
                new MasterRange(500_000, 2_500_000),
                [new WordTiming("hello", new MasterRange(500_000, 1_000_000)), new WordTiming("world", new MasterRange(1_500_000, 2_500_000))])],
            Pauses = [new MasterRange(0, 500_000), new MasterRange(2_500_000, 3_000_000)],
            Features = new AnalysisFeatureSet(
            [new SegmentAnalysisFeatures(fixture.Analysis.AllowedSegments[0].Id, 0.8m, 0.8m, 0.8m, 0.8m, 0.5m, 0.9m)])
        };

        EditingEvaluation evaluation = new EditingSkillEngine(
        [new SemanticStorySkill(), new SmartCutSkill(), new PacingDirectorSkill()]).Evaluate(
            new EditingContext(analysis, new ProductionSettings(6, true, false)),
            new EditingSkillConfiguration(
            [
                new("semantic-story", true),
                new("smart-cut", true),
                new("pacing-director", true)
            ]));

        SmartCutDecision cut = Assert.IsType<SmartCutDecision>(evaluation.Decisions[1]);
        SmartCutEdit edit = Assert.Single(cut.Edits);
        Assert.Equal(new MasterRange(500_000, 2_500_000), edit.Mapping.Master);
        PacingDecision pacing = Assert.IsType<PacingDecision>(evaluation.Decisions[2]);
        Assert.Equal(0, pacing.Shots[0].Output.StartFrame);
        Assert.Equal(pacing.Shots[0].Output.EndFrame, pacing.Shots[1].Output.StartFrame);
        Assert.Equal(150, pacing.Shots[^1].Output.EndFrame);
    }

    [Fact]
    public void BestShotUsesExplainableWeightedScore()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        AnalysisResult analysis = fixture.Analysis with
        {
            Features = new AnalysisFeatureSet(
            [
                new SegmentAnalysisFeatures(fixture.Analysis.AllowedSegments[0].Id, 1m, 0m, 0m, 0m, 0m, 1m),
                new SegmentAnalysisFeatures(fixture.Analysis.AllowedSegments[1].Id, 0m, 1m, 1m, 1m, 0m, 1m)
            ])
        };

        BestShotDecision decision = Assert.IsType<BestShotDecision>(
            new BestShotSelectorSkill().Evaluate(new EditingContext(analysis, new ProductionSettings(null, true, false))));

        Assert.Equal(0.6m, decision.Selections[0].Score);
        Assert.Equal(fixture.Analysis.AllowedSegments[1].Id, decision.Selections[0].SegmentId);
        Assert.Equal(EditingDecisionStatus.Applied, decision.Status);
    }

    private sealed class TestSkill : IEditingSkill
    {
        public string Id => "test-enabled";

        public string Version => "1.0.0";

        public ImmutableArray<string> Prerequisites => [];

        public int ExecutionCount { get; private set; }

        public EditingDecision Evaluate(EditingContext context)
        {
            ExecutionCount++;
            return new TestDecision(Id, Version, EditingDecisionStatus.Applied, [new EditingDecisionReason("test", "deterministic")]);
        }
    }

    private sealed class DisabledSkill : IEditingSkill
    {
        public string Id => "test-disabled";

        public string Version => "1.0.0";

        public ImmutableArray<string> Prerequisites => [];

        public int ExecutionCount { get; private set; }

        public EditingDecision Evaluate(EditingContext context)
        {
            ExecutionCount++;
            return new TestDecision(Id, Version, EditingDecisionStatus.Applied, []);
        }
    }

    private sealed record TestDecision(
        string SkillId,
        string SkillVersion,
        EditingDecisionStatus Status,
        ImmutableArray<EditingDecisionReason> Reasons)
        : EditingDecision(SkillId, SkillVersion, Status, Reasons);
}
