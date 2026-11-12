using System.Security.Cryptography;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>
/// Хранит файлы артефактов в локальной структуре каталогов Job.
/// </summary>
public sealed class LocalArtifactStorage : ILocalArtifactStorage
{
    private readonly string workspaceRoot;

    /// <summary>
    /// Создаёт локальное файловое хранилище.
    /// </summary>
    /// <param name="workspaceRoot">Корневой каталог workspace.</param>
    public LocalArtifactStorage(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot));
        }

        this.workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <inheritdoc />
    public async Task<ArtifactPublishResult> PublishAsync(
        JobId jobId,
        ArtifactId artifactId,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (jobId.IsEmpty)
        {
            return new ArtifactPublishResult.Rejected("Job ID cannot be empty.");
        }

        if (artifactId.IsEmpty)
        {
            return new ArtifactPublishResult.Rejected("Artifact ID cannot be empty.");
        }

        ArgumentNullException.ThrowIfNull(content);

        string relativePath = $"jobs/{jobId}/assets/{artifactId}.bin";
        string absolutePath = ResolvePath(relativePath);
        string? directory = Path.GetDirectoryName(absolutePath);
        if (directory is null)
        {
            return new ArtifactPublishResult.Rejected("Artifact directory cannot be resolved.");
        }

        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(directory, $".{artifactId}.{Guid.NewGuid():N}.tmp");
        try
        {
            StoredArtifactFile stored = await WriteTemporaryAsync(
                artifactId,
                relativePath,
                temporaryPath,
                content,
                cancellationToken);

            try
            {
                if (File.Exists(absolutePath))
                {
                    StoredArtifactFile existing = Describe(
                        artifactId,
                        relativePath,
                        absolutePath);
                    string existingHash = await ComputeHashAsync(absolutePath, cancellationToken);
                    if (existing.SizeBytes != stored.SizeBytes || existingHash != stored.Sha256)
                    {
                        return new ArtifactPublishResult.Rejected(
                            "Artifact ID already belongs to different content.");
                    }

                    return new ArtifactPublishResult.Succeeded(
                        existing with { Sha256 = existingHash },
                        WasPublished: false);
                }

                File.Move(temporaryPath, absolutePath);
            }
            catch (IOException) when (File.Exists(absolutePath))
            {
                StoredArtifactFile existing = Describe(
                    artifactId,
                    relativePath,
                    absolutePath);
                string existingHash = await ComputeHashAsync(absolutePath, cancellationToken);
                if (existing.SizeBytes != stored.SizeBytes || existingHash != stored.Sha256)
                {
                    return new ArtifactPublishResult.Rejected(
                        "Artifact ID already belongs to different content.");
                }

                return new ArtifactPublishResult.Succeeded(
                    existing with { Sha256 = existingHash },
                    WasPublished: false);
            }

            return new ArtifactPublishResult.Succeeded(stored, WasPublished: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string ResolvePath(string relativePath)
    {
        string fullPath = Path.GetFullPath(
            Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootWithSeparator = workspaceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? workspaceRoot
            : workspaceRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Artifact path escaped workspace root.");
        }

        return fullPath;
    }

    private static async Task<StoredArtifactFile> WriteTemporaryAsync(
        ArtifactId artifactId,
        string relativePath,
        string temporaryPath,
        Stream content,
        CancellationToken cancellationToken)
    {
        long size = 0;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using FileStream output = new(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[64 * 1024];
        int bytesRead;
        while ((bytesRead = await content.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            hash.AppendData(buffer, 0, bytesRead);
            size += bytesRead;
        }

        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        return new StoredArtifactFile(
            artifactId,
            relativePath,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            size);
    }

    private static StoredArtifactFile Describe(
        ArtifactId artifactId,
        string relativePath,
        string absolutePath)
    {
        FileInfo file = new(absolutePath);
        return new StoredArtifactFile(
            artifactId,
            relativePath,
            string.Empty,
            file.Length);
    }

    private static async Task<string> ComputeHashAsync(
        string absolutePath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
