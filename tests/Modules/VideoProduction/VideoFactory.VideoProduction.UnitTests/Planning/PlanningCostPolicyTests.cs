using VideoFactory.VideoProduction.Application.Planning;

namespace VideoFactory.VideoProduction.UnitTests.Planning;

public sealed class PlanningCostPolicyTests
{
    [Fact]
    public void CostAtLimitIsAllowed()
    {
        PlanningCostPolicy policy = new(new PlanningCostPolicyOptions(0.05m));

        PlanningCostDecision decision = policy.Decide(
            new PlanningCostEstimate(0.02m, "fake", "test"),
            0.03m);

        Assert.True(decision.IsAllowed);
        Assert.False(decision.RequiresApproval);
    }

    [Fact]
    public void CostAboveLimitRequiresApprovalBeforePaidCall()
    {
        PlanningCostPolicy policy = new(new PlanningCostPolicyOptions(0.05m));

        PlanningCostDecision decision = policy.Decide(
            new PlanningCostEstimate(0.06m, "fake", "test"),
            0m);

        Assert.False(decision.IsAllowed);
        Assert.True(decision.RequiresApproval);
        Assert.Contains("0.05", decision.Message);
    }
}
