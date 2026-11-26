using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.UnitTests.Critique;

public sealed class RemountPolicyTests
{
    [Fact]
    public void SubjectiveScoreWithoutObjectiveFindingsDoesNotRequireRemount()
    {
        RemountPolicy policy = new(maxRemounts: 2);

        bool requiresRemount = policy.RequiresRemount(
            new SubjectiveCritique(42),
            [],
            completedRemounts: 0);

        Assert.False(requiresRemount);
    }

    [Fact]
    public void RepairableObjectiveFindingRequiresRemountRegardlessOfSubjectiveScore()
    {
        RemountPolicy policy = new(maxRemounts: 2);
        CriticFinding repairableFinding = new(
            "black_frames",
            CriticFindingKind.BlackFrames,
            new OutputRange(0, 30),
            IsRepairable: true);

        bool requiresRemount = policy.RequiresRemount(
            new SubjectiveCritique(100),
            [repairableFinding],
            completedRemounts: 0);

        Assert.True(requiresRemount);
    }

    [Fact]
    public void RemountLimitBlocksRepairableObjectiveFinding()
    {
        RemountPolicy policy = new(maxRemounts: 2);
        CriticFinding repairableFinding = new(
            "black_frames",
            CriticFindingKind.BlackFrames,
            new OutputRange(0, 30),
            IsRepairable: true);

        bool requiresRemount = policy.RequiresRemount(
            new SubjectiveCritique(42),
            [repairableFinding],
            completedRemounts: 2);

        Assert.False(requiresRemount);
    }
}
