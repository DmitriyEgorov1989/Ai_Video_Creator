using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Domain.Validation;

public static class EditPlanValidator
{
    public static ImmutableArray<ValidationIssue> Validate(
        EditPlan plan,
        AnalysisResult analysis,
        IReadOnlyDictionary<ArtifactId, MediaAsset> assets,
        IReadOnlyDictionary<TemplateId, TemplateDefinition> templates)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(templates);

        List<ValidationIssue> issues = [.. AnalysisValidator.Validate(analysis, assets)];
        ValidateIdentity(plan, analysis, issues);
        ValidateOutput(plan, issues);
        ValidateTemplate(plan, templates, issues);

        Dictionary<SegmentId, AllowedSegment> segments = BuildSegmentCatalog(analysis);
        ValidateVideo(plan, segments, issues);
        ValidateAudio(plan, analysis, segments, assets, issues);
        ValidateCaptions(plan, issues);
        ValidateOverlays(plan, assets, issues);

        return [.. issues];
    }

    private static void ValidateIdentity(
        EditPlan plan,
        AnalysisResult analysis,
        List<ValidationIssue> issues)
    {
        if (plan.SchemaVersion != 1)
            Add(issues, "plan.schema_version", "schemaVersion", "Only edit plan schema version 1 is supported.");
        if (plan.JobId.IsEmpty) Add(issues, "plan.job_id_empty", "jobId", "Job ID is required.");
        if (plan.VariantId.IsEmpty) Add(issues, "plan.variant_id_empty", "variantId", "Variant ID is required.");
        if (plan.RevisionId.IsEmpty) Add(issues, "plan.revision_id_empty", "revisionId", "Revision ID is required.");
        if (plan.AnalysisRevisionId.IsEmpty)
            Add(issues, "plan.analysis_revision_empty", "analysisRevisionId", "Analysis revision ID is required.");
        else if (plan.AnalysisRevisionId != analysis.RevisionId)
            Add(issues, "plan.analysis_revision_mismatch", "analysisRevisionId", "Plan must use the supplied analysis revision.");
    }

    private static void ValidateOutput(EditPlan plan, List<ValidationIssue> issues)
    {
        if (plan.Output is null)
        {
            Add(issues, "plan.output_missing", "output", "Output profile is required.");
            return;
        }

        if (plan.Output.Width <= 0 || plan.Output.Height <= 0)
            Add(issues, "plan.output_dimensions", "output", "Output dimensions must be positive.");
        if (!plan.Output.FrameRate.IsValid)
            Add(issues, "plan.output_frame_rate", "output.frameRate", "Output frame rate must be valid.");
        if (plan.Output.DurationFrames <= 0)
            Add(issues, "plan.output_duration", "output.durationFrames", "Output duration must be positive.");
    }

    private static void ValidateTemplate(
        EditPlan plan,
        IReadOnlyDictionary<TemplateId, TemplateDefinition> templates,
        List<ValidationIssue> issues)
    {
        if (!Enum.IsDefined(plan.Template) || !templates.TryGetValue(plan.Template, out TemplateDefinition? template))
        {
            Add(issues, "template.unknown", "template", "The plan refers to an unknown template.");
            return;
        }

        if (template.Id != plan.Template)
            Add(issues, "template.catalog_id_mismatch", "template", "Template catalog key does not match the template ID.");

        if (plan.TemplateProps is null)
        {
            Add(issues, "template.props_missing", "templateProps", "Template props are required.");
            return;
        }

        if (!Enum.IsDefined(plan.TemplateProps.Captions)
            || !template.AllowedCaptionPresets.Contains(plan.TemplateProps.Captions))
            Add(issues, "template.caption_preset", "templateProps.captions", "Caption preset is not supported by the template.");
        if (!Enum.IsDefined(plan.TemplateProps.Theme))
            Add(issues, "template.color_theme", "templateProps.theme", "Color theme is invalid.");
        if (!template.SupportsTitle && !string.IsNullOrWhiteSpace(plan.TemplateProps.Title))
            Add(issues, "template.title_unsupported", "templateProps.title", "The template does not support a title.");
    }

    private static Dictionary<SegmentId, AllowedSegment> BuildSegmentCatalog(AnalysisResult analysis)
    {
        Dictionary<SegmentId, AllowedSegment> segments = [];
        if (analysis.AllowedSegments.IsDefault) return segments;

        foreach (AllowedSegment segment in analysis.AllowedSegments)
        {
            if (!segment.Id.IsEmpty) segments.TryAdd(segment.Id, segment);
        }

        return segments;
    }

    private static void ValidateVideo(
        EditPlan plan,
        IReadOnlyDictionary<SegmentId, AllowedSegment> segments,
        List<ValidationIssue> issues)
    {
        if (plan.Video is null || plan.Video.Clips.IsDefault)
        {
            Add(issues, "video.clips_uninitialized", "video.clips", "Video clips must be initialized.");
            return;
        }

        if (plan.Video.Clips.Length == 0)
        {
            Add(issues, "video.clips_empty", "video.clips", "At least one video clip is required.");
            return;
        }

        int expectedStart = 0;
        for (int index = 0; index < plan.Video.Clips.Length; index++)
        {
            VideoClip clip = plan.Video.Clips[index];
            string path = $"video.clips[{index}]";
            if (clip.Output.StartFrame != expectedStart)
                Add(issues, "video.timeline_not_contiguous", $"{path}.output", "Video clips must be contiguous and start at frame zero.");
            CheckOutputBounds(clip.Output, plan.Output?.DurationFrames ?? 0, $"{path}.output", issues);
            expectedStart = clip.Output.EndFrame;

            if (!Enum.IsDefined(clip.Crop))
                Add(issues, "video.crop_invalid", $"{path}.crop", "Crop preset is invalid.");

            if (!segments.TryGetValue(clip.SegmentId, out AllowedSegment? segment))
            {
                Add(issues, "segment.unknown", $"{path}.segmentId", "The clip refers to an unknown segment.");
                continue;
            }

            if (clip.Mapping != segment.Mapping)
                Add(issues, "segment.mapping_mismatch", $"{path}.mapping", "The clip mapping differs from the approved analysis segment.");

            if (plan.Output is { FrameRate.IsValid: true }
                && clip.Mapping.Master.DurationMicroseconds > 0)
            {
                int expectedFrames = TimelineMath.ToNearestFrameCount(
                    clip.Mapping.Master.DurationMicroseconds,
                    plan.Output.FrameRate);
                if (clip.Output.DurationFrames != expectedFrames)
                    Add(issues, "video.duration_mismatch", $"{path}.output", "Output duration does not match the master interval at the configured frame rate.");
            }
        }

        if (plan.Output is not null && expectedStart != plan.Output.DurationFrames)
            Add(issues, "video.duration_mismatch", "video.clips", "Video track must end at output duration.");
    }

    private static void ValidateAudio(
        EditPlan plan,
        AnalysisResult analysis,
        IReadOnlyDictionary<SegmentId, AllowedSegment> segments,
        IReadOnlyDictionary<ArtifactId, MediaAsset> assets,
        List<ValidationIssue> issues)
    {
        if (plan.Audio is null)
        {
            Add(issues, "audio.missing", "audio", "Audio track is required.");
            return;
        }

        if (plan.Audio.SourceClips.IsDefault)
            Add(issues, "audio.source_uninitialized", "audio.sourceClips", "Source audio clips must be initialized.");
        else
        {
            bool masterHasAudio = assets.TryGetValue(analysis.MasterAssetId, out MediaAsset? master)
                && master.Metadata.AudioStreams.Length > 0;
            for (int index = 0; index < plan.Audio.SourceClips.Length; index++)
            {
                SourceAudioClip clip = plan.Audio.SourceClips[index];
                string path = $"audio.sourceClips[{index}]";
                if (!segments.ContainsKey(clip.SegmentId))
                    Add(issues, "segment.unknown", $"{path}.segmentId", "Source audio refers to an unknown segment.");
                if (!masterHasAudio)
                    Add(issues, "audio.source_stream_missing", path, "Master asset has no audio stream.");
                CheckGain(clip.Gain, $"{path}.gain", issues);
                CheckOutputBounds(clip.Output, plan.Output?.DurationFrames ?? 0, $"{path}.output", issues);

                bool matchingVideo = plan.Video is not null
                    && !plan.Video.Clips.IsDefault
                    && plan.Video.Clips.Any(video => video.SegmentId == clip.SegmentId && video.Output == clip.Output);
                if (!matchingVideo)
                    Add(issues, "audio.source_not_synchronized", path, "Source audio must match a video clip segment and output interval.");
            }
        }

        if (plan.Audio.MusicClips.IsDefault)
        {
            Add(issues, "audio.music_uninitialized", "audio.musicClips", "Music clips must be initialized.");
            return;
        }

        for (int index = 0; index < plan.Audio.MusicClips.Length; index++)
        {
            MusicClip clip = plan.Audio.MusicClips[index];
            string path = $"audio.musicClips[{index}]";
            if (!assets.TryGetValue(clip.AssetId, out MediaAsset? asset))
                Add(issues, "asset.unknown", $"{path}.assetId", "Music clip refers to an unknown asset.");
            else
            {
                if (asset.Metadata.AudioStreams.Length == 0)
                    Add(issues, "audio.music_stream_missing", path, "Music asset has no audio stream.");
                if (clip.Source.DurationMicroseconds <= 0 || clip.Source.EndMicroseconds > asset.Metadata.DurationMicroseconds)
                    Add(issues, "timeline.asset_out_of_bounds", $"{path}.source", "Music source interval exceeds asset duration.");
            }

            CheckGain(clip.Gain, $"{path}.gain", issues);
            CheckOutputBounds(clip.Output, plan.Output?.DurationFrames ?? 0, $"{path}.output", issues);
            if (plan.Output is { FrameRate.IsValid: true }
                && clip.Source.DurationMicroseconds > 0)
            {
                int expectedFrames = TimelineMath.ToNearestFrameCount(
                    clip.Source.DurationMicroseconds,
                    plan.Output.FrameRate);
                if (clip.Output.DurationFrames != expectedFrames)
                    Add(issues, "audio.music_duration_mismatch", $"{path}.output", "Music source and output durations must match when no loop or rate operation is defined.");
            }
        }
    }

    private static void ValidateCaptions(EditPlan plan, List<ValidationIssue> issues)
    {
        if (plan.Captions is null || plan.Captions.Items.IsDefault)
        {
            Add(issues, "captions.uninitialized", "captions.items", "Captions must be initialized.");
            return;
        }

        if (plan.TemplateProps?.Captions == CaptionPreset.None && plan.Captions.Items.Length > 0)
            Add(issues, "captions.preset_none", "captions.items", "Caption items require an enabled caption preset.");

        int previousEnd = 0;
        for (int index = 0; index < plan.Captions.Items.Length; index++)
        {
            Caption caption = plan.Captions.Items[index];
            string path = $"captions.items[{index}]";
            if (string.IsNullOrWhiteSpace(caption.Text))
                Add(issues, "captions.text_empty", $"{path}.text", "Caption text cannot be empty.");
            if (caption.Output.StartFrame < previousEnd)
                Add(issues, "captions.overlap", $"{path}.output", "Captions cannot overlap in the MVP contract.");
            CheckOutputBounds(caption.Output, plan.Output?.DurationFrames ?? 0, $"{path}.output", issues);
            previousEnd = caption.Output.EndFrame;
        }
    }

    private static void ValidateOverlays(
        EditPlan plan,
        IReadOnlyDictionary<ArtifactId, MediaAsset> assets,
        List<ValidationIssue> issues)
    {
        if (plan.Overlays is null || plan.Overlays.Items.IsDefault)
        {
            Add(issues, "overlays.uninitialized", "overlays.items", "Overlays must be initialized.");
            return;
        }

        for (int index = 0; index < plan.Overlays.Items.Length; index++)
        {
            Overlay overlay = plan.Overlays.Items[index];
            string path = $"overlays.items[{index}]";
            CheckOutputBounds(overlay.Output, plan.Output?.DurationFrames ?? 0, $"{path}.output", issues);
            switch (overlay)
            {
                case TextOverlay text when string.IsNullOrWhiteSpace(text.Text):
                    Add(issues, "overlay.text_empty", $"{path}.text", "Overlay text cannot be empty.");
                    break;
                case ImageOverlay image when !assets.ContainsKey(image.AssetId):
                    Add(issues, "asset.unknown", $"{path}.assetId", "Image overlay refers to an unknown asset.");
                    break;
                case ImageOverlay image when assets[image.AssetId].Metadata.VideoStreams.Length == 0:
                    Add(issues, "overlay.visual_stream_missing", $"{path}.assetId", "Image overlay asset has no visual stream.");
                    break;
                case TextOverlay or ImageOverlay:
                    break;
                default:
                    Add(issues, "overlay.type_unknown", path, "Overlay type is not supported.");
                    break;
            }
        }
    }

    private static void CheckOutputBounds(
        OutputRange range,
        int durationFrames,
        string path,
        List<ValidationIssue> issues)
    {
        if (range.DurationFrames <= 0 || range.EndFrame > durationFrames)
            Add(issues, "timeline.output_out_of_bounds", path, "Output interval exceeds plan duration.");
    }

    private static void CheckGain(decimal gain, string path, List<ValidationIssue> issues)
    {
        if (gain is < 0m or > 4m)
            Add(issues, "audio.gain_out_of_range", path, "Audio gain must be between 0 and 4.");
    }

    private static void Add(List<ValidationIssue> issues, string code, string path, string message) =>
        issues.Add(new ValidationIssue(code, path, message));
}
