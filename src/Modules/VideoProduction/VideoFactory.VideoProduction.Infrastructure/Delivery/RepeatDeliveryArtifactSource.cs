using System.Security.Cryptography;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Delivery;

/// <summary>Проверяет опубликованный MP4 перед повторной доставкой.</summary>
public sealed class RepeatDeliveryArtifactSource : IRepeatDeliveryArtifactSource
{
    private readonly IArtifactStore artifactStore;
    private readonly string workspaceRoot;

    /// <summary>Создаёт источник готового MP4.</summary>
    public RepeatDeliveryArtifactSource(IArtifactStore artifactStore, string workspaceRoot)
    {
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    }

    /// <inheritdoc />
    public async Task<RepeatDeliveryArtifactResult> GetVerifiedAsync(
        JobCreationRecord job,
        CancellationToken cancellationToken)
    {
        ArtifactRecord? finalArtifact = await artifactStore.FindLatestAsync(job.JobId, "final-mp4", cancellationToken);
        ArtifactRecord? manifest = await artifactStore.FindLatestAsync(job.JobId, "manifest", cancellationToken);
        if (finalArtifact is null || manifest is null || manifest.ParentArtifactId != finalArtifact.ArtifactId)
        {
            return new RepeatDeliveryArtifactResult.Rejected("delivery.final_missing", "The verified MP4 or its manifest is unavailable.");
        }

        string finalPath;
        string manifestPath;
        try
        {
            finalPath = SafeFileTree.Resolve(workspaceRoot, finalArtifact.RelativePath);
            manifestPath = SafeFileTree.Resolve(workspaceRoot, manifest.RelativePath);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new RepeatDeliveryArtifactResult.Rejected("delivery.invalid_path", "The stored delivery path is invalid.");
        }

        if (!File.Exists(finalPath) || !File.Exists(manifestPath) || finalArtifact.SizeBytes is not long expectedSize || finalArtifact.Sha256 is null)
        {
            return new RepeatDeliveryArtifactResult.Rejected("delivery.file_missing", "The verified MP4 or its manifest is unavailable.");
        }

        FileInfo info = new(finalPath);
        if (info.Length != expectedSize)
        {
            return new RepeatDeliveryArtifactResult.Rejected("delivery.file_changed", "The packaged MP4 changed after verification.");
        }

        await using FileStream stream = new(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return !string.Equals(actualHash, finalArtifact.Sha256, StringComparison.OrdinalIgnoreCase)
            ? new RepeatDeliveryArtifactResult.Rejected("delivery.file_changed", "The packaged MP4 changed after verification.")
            : new RepeatDeliveryArtifactResult.Succeeded(new RepeatDeliveryArtifact(finalArtifact.ArtifactId, finalPath));
    }
}
