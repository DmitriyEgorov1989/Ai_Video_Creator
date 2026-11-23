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
