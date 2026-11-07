using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Domain.Editing;

public sealed record OutputProfile(
    int Width,
    int Height,
    FrameRate FrameRate,
    int DurationFrames);

public enum CropPreset
{
    Fit,
    CenterCrop
}

public sealed record VideoClip(
    SegmentId SegmentId,
    SourceMapping Mapping,
    OutputRange Output,
    CropPreset Crop);

public sealed record VideoTrack(ImmutableArray<VideoClip> Clips);

public sealed record SourceAudioClip(
    SegmentId SegmentId,
    OutputRange Output,
    decimal Gain);

public sealed record MusicClip(
    ArtifactId AssetId,
    AssetRange Source,
    OutputRange Output,
    decimal Gain);

public sealed record AudioTrack(
    ImmutableArray<SourceAudioClip> SourceClips,
    ImmutableArray<MusicClip> MusicClips);

public sealed record Caption(string Text, OutputRange Output);

public sealed record CaptionTrack(ImmutableArray<Caption> Items);

public abstract record Overlay(OutputRange Output);

public sealed record TextOverlay(string Text, OutputRange Output) : Overlay(Output);

public sealed record ImageOverlay(ArtifactId AssetId, OutputRange Output) : Overlay(Output);

public sealed record OverlayTrack(ImmutableArray<Overlay> Items);

public sealed record EditPlan(
    int SchemaVersion,
    JobId JobId,
    VariantId VariantId,
    RevisionId RevisionId,
    RevisionId AnalysisRevisionId,
    int Seed,
    OutputProfile Output,
    TemplateId Template,
    TemplateProps TemplateProps,
    VideoTrack Video,
    AudioTrack Audio,
    CaptionTrack Captions,
    OverlayTrack Overlays);
