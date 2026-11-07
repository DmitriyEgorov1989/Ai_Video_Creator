using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Domain.Validation;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Editing;

public sealed class EditPlanValidatorTests
{
    [Fact]
    public void ValidPlanHasNoValidationIssues()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();

        ImmutableArray<ValidationIssue> issues = EditPlanValidator.Validate(
            fixture.Plan,
            fixture.Analysis,
            fixture.Assets,
            fixture.Templates);

        Assert.Empty(issues);
    }

    [Fact]
    public void UnknownSegmentIsRejected()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        VideoClip invalid = fixture.Plan.Video.Clips[0] with { SegmentId = SegmentId.New() };
        EditPlan plan = fixture.Plan with
        {
            Video = new VideoTrack([invalid, fixture.Plan.Video.Clips[1]])
        };

        ImmutableArray<ValidationIssue> issues = Validate(plan, fixture);

        Assert.Contains(issues, issue =>
            issue.Code == "segment.unknown"
            && issue.Path == "video.clips[0].segmentId");
    }

    [Fact]
    public void ChangedApprovedMappingIsRejected()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        VideoClip first = fixture.Plan.Video.Clips[0];
        VideoClip invalid = first with
        {
            Mapping = first.Mapping with { Master = new MasterRange(0, 2_000_000) }
        };
        EditPlan plan = fixture.Plan with
        {
            Video = new VideoTrack([invalid, fixture.Plan.Video.Clips[1]])
        };

        ImmutableArray<ValidationIssue> issues = Validate(plan, fixture);

        Assert.Contains(issues, issue => issue.Code == "segment.mapping_mismatch");
    }

    [Fact]
    public void GapBetweenVideoClipsIsRejected()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        VideoClip second = fixture.Plan.Video.Clips[1] with { Output = new OutputRange(91, 181) };
        EditPlan plan = fixture.Plan with
        {
            Video = new VideoTrack([fixture.Plan.Video.Clips[0], second])
        };

        ImmutableArray<ValidationIssue> issues = Validate(plan, fixture);

        Assert.Contains(issues, issue => issue.Code == "video.timeline_not_contiguous");
        Assert.Contains(issues, issue => issue.Code == "timeline.output_out_of_bounds");
    }

    [Fact]
    public void UnknownOverlayAssetAndCaptionOutsideOutputAreBothReported()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        EditPlan plan = fixture.Plan with
        {
            Captions = new CaptionTrack([new Caption("Late", new OutputRange(179, 181))]),
            Overlays = new OverlayTrack(
                [new ImageOverlay(ArtifactId.New(), new OutputRange(0, 10))])
        };

        ImmutableArray<ValidationIssue> issues = Validate(plan, fixture);

        Assert.Contains(issues, issue => issue.Code == "timeline.output_out_of_bounds");
        Assert.Contains(issues, issue => issue.Code == "asset.unknown");
    }

    [Fact]
    public void AnalysisRevisionMismatchIsRejected()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        EditPlan plan = fixture.Plan with { AnalysisRevisionId = RevisionId.New() };

        ImmutableArray<ValidationIssue> issues = Validate(plan, fixture);

        Assert.Contains(issues, issue => issue.Code == "plan.analysis_revision_mismatch");
    }

    [Fact]
    public void MusicSourceAndOutputDurationMustMatch()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        MusicClip music = fixture.Plan.Audio.MusicClips[0] with
        {
            Source = new AssetRange(0, 1_000_000)
        };
        EditPlan plan = fixture.Plan with
        {
            Audio = fixture.Plan.Audio with { MusicClips = [music] }
        };

        ImmutableArray<ValidationIssue> issues = Validate(plan, fixture);

        Assert.Contains(issues, issue => issue.Code == "audio.music_duration_mismatch");
    }

    [Fact]
    public void ImageOverlayRequiresVisualAsset()
    {
        ValidPlanFixture fixture = ValidPlanFixture.Create();
        ArtifactId audioOnlyAssetId = fixture.Plan.Audio.MusicClips[0].AssetId;
        EditPlan plan = fixture.Plan with
        {
            Overlays = new OverlayTrack(
                [new ImageOverlay(audioOnlyAssetId, new OutputRange(0, 10))])
        };

        ImmutableArray<ValidationIssue> issues = Validate(plan, fixture);

        Assert.Contains(issues, issue => issue.Code == "overlay.visual_stream_missing");
    }

    private static ImmutableArray<ValidationIssue> Validate(EditPlan plan, ValidPlanFixture fixture) =>
        EditPlanValidator.Validate(plan, fixture.Analysis, fixture.Assets, fixture.Templates);
}
