using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Packaging;

/// <summary>Атомарно публикует MP4 после успешного Verification.</summary>
public sealed class PackagingStageExecutor : IJobStageExecutor
{
    private readonly IArtifactStore artifactStore;
    private readonly string workspaceRoot;
    private readonly long maxTelegramBytes;

    /// <summary>Создаёт исполнитель Packaging.</summary>
    public PackagingStageExecutor(IArtifactStore artifactStore, string workspaceRoot, long maxTelegramBytes)
    {
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (maxTelegramBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTelegramBytes));
        this.maxTelegramBytes = maxTelegramBytes;
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken)
    {
        if (job.Progress.Stage != ProductionStage.Packaging)
        {
            return StageExecutionResult.Permanent("packaging.invalid_stage", "Packaging executor received an unexpected stage.");
        }

        ArtifactRecord? existing = await artifactStore.FindLatestAsync(job.JobId, "final-mp4", cancellationToken);
        if (existing is not null && File.Exists(ResolvePath(existing.RelativePath)))
        {
            return StageExecutionResult.Success();
        }

        ArtifactRecord? render = await artifactStore.FindLatestAsync(job.JobId, "render-output", cancellationToken);
        ArtifactRecord? qa = await artifactStore.FindLatestAsync(job.JobId, "qa-report", cancellationToken);
        if (render is null || qa is null)
        {
            return StageExecutionResult.Retryable("packaging.input_missing", "A verified render output is not available yet.");
        }

        string sourcePath = ResolvePath(render.RelativePath);
        if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length <= 0)
        {
            return StageExecutionResult.Permanent("packaging.render_missing", "The verified render file does not exist.");
        }
        if (new FileInfo(sourcePath).Length > maxTelegramBytes)
        {
            return StageExecutionResult.Permanent("packaging.telegram_size_exceeded", "The verified MP4 exceeds the configured Telegram delivery limit.");
        }

        string directory = Path.Combine(workspaceRoot, "data", "jobs", job.JobId.ToString(), "final");
        Directory.CreateDirectory(directory);
        string relativePath = Path.GetRelativePath(workspaceRoot, Path.Combine(directory, $"{job.VariantId}.mp4"))
            .Replace(Path.DirectorySeparatorChar, '/');
        string finalPath = ResolvePath(relativePath);
        string temporaryPath = finalPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream input = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (FileStream output = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath, overwrite: false);
            FileInfo finalFile = new(finalPath);
            string hash;
            await using (FileStream stream = new(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            }

            ArtifactRecord finalArtifact = new(
                ArtifactId.New(),
                "final-mp4",
                relativePath,
                hash,
                finalFile.Length,
                JsonSerializer.Serialize(new { jobId = job.JobId, variantId = job.VariantId, qaArtifactId = qa.ArtifactId }),
                render.ArtifactId,
                "packaging/1",
                job.JobId);
            ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(finalArtifact, cancellationToken);
            if (registration is ArtifactRegistrationResult.Rejected rejected)
            {
                return StageExecutionResult.Permanent("packaging.artifact_rejected", rejected.Message);
            }

            string manifestPath = Path.Combine(directory, $"{job.VariantId}.manifest.json");
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    jobId = job.JobId,
                    variantId = job.VariantId,
                    finalArtifactId = finalArtifact.ArtifactId,
                    qaArtifactId = qa.ArtifactId,
                    sha256 = hash,
                    sizeBytes = finalFile.Length
                }),
                cancellationToken);
            FileInfo manifestFile = new(manifestPath);
            string manifestHash;
            await using (FileStream manifestStream = new(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                manifestHash = Convert.ToHexString(await SHA256.HashDataAsync(manifestStream, cancellationToken)).ToLowerInvariant();
            }
            ArtifactRegistrationResult manifestRegistration = await artifactStore.RegisterAsync(
                new ArtifactRecord(
                    ArtifactId.New(),
                    "manifest",
                    Path.GetRelativePath(workspaceRoot, manifestPath).Replace(Path.DirectorySeparatorChar, '/'),
                    manifestHash,
                    manifestFile.Length,
                    null,
                    finalArtifact.ArtifactId,
                    "packaging/1",
                    job.JobId),
                cancellationToken);
            if (manifestRegistration is ArtifactRegistrationResult.Rejected manifestRejected)
            {
                return StageExecutionResult.Permanent("packaging.manifest_rejected", manifestRejected.Message);
            }
            return StageExecutionResult.Success();
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private string ResolvePath(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = workspaceRoot.EndsWith(Path.DirectorySeparatorChar) ? workspaceRoot : workspaceRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Artifact path escaped workspace root.");
        return path;
    }
}
