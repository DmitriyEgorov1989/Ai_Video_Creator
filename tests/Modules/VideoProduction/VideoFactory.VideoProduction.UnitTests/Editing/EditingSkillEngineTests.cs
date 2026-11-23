using System.Collections.Immutable;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Jobs;
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
