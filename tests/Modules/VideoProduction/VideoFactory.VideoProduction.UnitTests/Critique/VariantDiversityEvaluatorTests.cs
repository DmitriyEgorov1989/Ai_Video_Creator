using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Critique;

public sealed class VariantDiversityEvaluatorTests
{
    [Fact]
    public void IdenticalPlanIsRejected()
    {
        EditPlan plan = ValidPlanFixture.Create().Plan;

        VariantDiversityResult result = new VariantDiversityEvaluator().Evaluate(plan, plan);

        Assert.False(result.IsAccepted);
        Assert.Equal(0, result.DifferenceCount);
        Assert.False(result.Differences.Hook);
        Assert.False(result.Differences.SegmentSet);
        Assert.False(result.Differences.SegmentOrder);
        Assert.False(result.Differences.Duration);
        Assert.False(result.Differences.CropProfile);
    }

    [Fact]
    public void CropOnlyDifferenceIsRejected()
    {
        EditPlan existing = ValidPlanFixture.Create().Plan;
        VideoClip firstClip = existing.Video.Clips[0];
        EditPlan candidate = existing with
        {
            Video = new VideoTrack(existing.Video.Clips.SetItem(
                0,
                firstClip with { Crop = CropPreset.Fit }))
        };

        VariantDiversityResult result = new VariantDiversityEvaluator().Evaluate(candidate, existing);

        Assert.False(result.IsAccepted);
        Assert.Equal(1, result.DifferenceCount);
        Assert.True(result.Differences.CropProfile);
        Assert.False(result.Differences.Hook);
        Assert.False(result.Differences.SegmentSet);
        Assert.False(result.Differences.SegmentOrder);
        Assert.False(result.Differences.Duration);
    }

    [Fact]
    public void DifferentHookAndSegmentOrderIsAccepted()
    {
        EditPlan existing = ValidPlanFixture.Create().Plan;
        EditPlan candidate = existing with
        {
            Video = new VideoTrack([.. existing.Video.Clips.Reverse()])
        };

        VariantDiversityResult result = new VariantDiversityEvaluator().Evaluate(candidate, existing);

        Assert.True(result.IsAccepted);
        Assert.Equal(2, result.DifferenceCount);
        Assert.True(result.Differences.Hook);
        Assert.True(result.Differences.SegmentOrder);
        Assert.False(result.Differences.SegmentSet);
        Assert.False(result.Differences.Duration);
        Assert.False(result.Differences.CropProfile);
    }
}
