namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>Описывает соответствие интервала Original интервалу Master.</summary>
public sealed record TimelineMapping(
    string OriginalArtifactId,
    string MasterArtifactId,
    long OriginalStartMicroseconds,
    long OriginalDurationMicroseconds,
    long MasterStartMicroseconds,
    long MasterDurationMicroseconds);
