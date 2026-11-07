using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Domain.Jobs;

public sealed record JobRequest
{
    public ArtifactId SourceAssetId { get; }

    public string Comment { get; }

    public ProductionMode RequestedMode { get; }

    public ProductionSettings Settings { get; }

    public JobRequest(
        ArtifactId sourceAssetId,
        string comment,
        ProductionMode requestedMode,
        ProductionSettings settings)
    {
        if (sourceAssetId.IsEmpty)
        {
            throw new ArgumentException("Source asset ID cannot be empty.", nameof(sourceAssetId));
        }

        if (string.IsNullOrWhiteSpace(comment))
        {
            throw new ArgumentException("Comment cannot be empty.", nameof(comment));
        }

        if (!Enum.IsDefined(requestedMode))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        }

        ArgumentNullException.ThrowIfNull(settings);

        SourceAssetId = sourceAssetId;
        Comment = comment;
        RequestedMode = requestedMode;
        Settings = settings;
    }
}
