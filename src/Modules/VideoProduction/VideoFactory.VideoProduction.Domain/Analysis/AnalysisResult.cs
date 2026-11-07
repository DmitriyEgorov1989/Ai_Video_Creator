using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Domain.Analysis;

public sealed record SourceMapping(
    ArtifactId OriginalAssetId,
    OriginalRange Original,
    ArtifactId MasterAssetId,
    MasterRange Master);

public sealed record AllowedSegment(SegmentId Id, SourceMapping Mapping);

public sealed record WordTiming(string Text, MasterRange Interval);

public sealed record SpeechSegment(
    string Text,
    MasterRange Interval,
    ImmutableArray<WordTiming> Words);

public sealed record Scene(MasterRange Interval);

public sealed record AnalysisResult(
    int SchemaVersion,
    RevisionId RevisionId,
    ArtifactId OriginalAssetId,
    ArtifactId MasterAssetId,
    ImmutableArray<SpeechSegment> Speech,
    ImmutableArray<MasterRange> Pauses,
    ImmutableArray<Scene> Scenes,
    ImmutableArray<AllowedSegment> AllowedSegments);
