using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Packaging;

/// <summary>Атомарно публикует MP4 после успешного Verification.</summary>
public sealed class PackagingStageExecutor : IJobStageExecutor
{
    private readonly IArtifactStore artifactStore;
    private readonly string workspaceRoot;
    private readonly long maxTelegramBytes;
    private readonly IStorageBudget storageBudget;

    /// <summary>Создаёт исполнитель Packaging.</summary>
    public PackagingStageExecutor(IArtifactStore artifactStore, string workspaceRoot, long maxTelegramBytes, IStorageBudget? storageBudget = null)
    {
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (maxTelegramBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTelegramBytes));
        this.maxTelegramBytes = maxTelegramBytes;
        this.storageBudget = storageBudget ?? StorageBudgetRunner.Default;
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken)
    {
        if (job.Progress.Stage != ProductionStage.Packaging)
        {
            return StageExecutionResult.Permanent("packaging.invalid_stage", "Packaging executor received an unexpected stage.");
        }

        ArtifactRecord? existing = await artifactStore.FindLatestAsync(job.JobId, "final-mp4", cancellationToken);

        ArtifactRecord? render = await artifactStore.FindLatestAsync(job.JobId, "render-output", cancellationToken);
        ArtifactRecord? qa = await artifactStore.FindLatestAsync(job.JobId, "qa-report", cancellationToken);
        if (render is null || qa is null)
        {
            return StageExecutionResult.Retryable("packaging.input_missing", "A verified render output is not available yet.");
        }

        try
        {
            byte[] reportBytes = await File.ReadAllBytesAsync(ResolvePath(qa.RelativePath), cancellationToken);
            using JsonDocument report = JsonDocument.Parse(reportBytes);
            if (report.RootElement.ValueKind != JsonValueKind.Object
                || !report.RootElement.TryGetProperty("status", out JsonElement status)
                || status.ValueKind != JsonValueKind.String || status.GetString() != "passed")
            {
                return StageExecutionResult.Permanent("packaging.qa_not_passed", "Output QA has not passed.");
            }
            JsonElement root = report.RootElement;
            if (!MatchesId(root, "jobId", job.JobId.Value)
                || !MatchesId(root, "variantId", job.VariantId.Value)
                || job.CurrentApprovedEditPlanRevisionId is null
                || !MatchesId(root, "planRevisionId", job.CurrentApprovedEditPlanRevisionId.Value.Value)
                || !root.TryGetProperty("output", out JsonElement output)
                || !MatchesId(output, "artifactId", render.ArtifactId.Value)
                || output.ValueKind != JsonValueKind.Object
                || !output.TryGetProperty("sha256", out JsonElement hash)
                || hash.ValueKind != JsonValueKind.String || hash.GetString() != render.Sha256
                || !output.TryGetProperty("sizeBytes", out JsonElement size)
                || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes != render.SizeBytes
                || qa.ParentArtifactId != render.ArtifactId)
            {
                return StageExecutionResult.Permanent("packaging.qa_mismatch", "QA report does not match the approved render.");
            }
            if (qa.SizeBytes != reportBytes.LongLength
                || qa.Sha256 != Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant())
                return StageExecutionResult.Permanent("packaging.qa_changed", "QA report changed after registration.");
        }
        catch (JsonException)
        {
            return StageExecutionResult.Permanent("packaging.qa_invalid", "Output QA report is invalid.");
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
        await using (FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)).ToLowerInvariant();
            if (source.Length != render.SizeBytes || actualHash != render.Sha256)
                return StageExecutionResult.Permanent("packaging.render_changed", "Render changed after verification.");
        }

        string jobDirectory = SafeFileTree.Resolve(workspaceRoot, $"data/jobs/{job.JobId}");
        StorageBudgetFailure? capacity = await storageBudget.CheckAsync(jobDirectory, checked(render.SizeBytes!.Value + 1_048_576), cancellationToken);
        if (capacity is not null)
            return capacity.Code == "storage.job_quota_exceeded"
                ? StageExecutionResult.Permanent(capacity.Code, capacity.Message)
                : StageExecutionResult.Retryable(capacity.Code, capacity.Message);
        string directory = SafeFileTree.Resolve(workspaceRoot, $"data/jobs/{job.JobId}/final");
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

            if (await HashAsync(temporaryPath, cancellationToken) != render.Sha256)
                return StageExecutionResult.Permanent("packaging.render_changed", "Render changed during publication.");
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(finalPath))
            {
                if (await HashAsync(finalPath, cancellationToken) != render.Sha256)
                    return StageExecutionResult.Permanent("packaging.final_conflict", "An immutable final result already exists with different content.");
            }
            else File.Move(temporaryPath, finalPath, overwrite: false);
            FileInfo finalFile = new(finalPath);
            string hash;
            await using (FileStream stream = new(finalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            }

            ArtifactRecord finalArtifact = existing ?? new(
                ArtifactId.New(),
                "final-mp4",
                relativePath,
                hash,
                finalFile.Length,
                JsonSerializer.Serialize(new { jobId = job.JobId, variantId = job.VariantId, qaArtifactId = qa.ArtifactId }),
                render.ArtifactId,
                "packaging/1",
                job.JobId);
            ArtifactRegistrationResult registration = existing is not null
                ? new ArtifactRegistrationResult.Existing(existing)
                : await artifactStore.RegisterAsync(finalArtifact, cancellationToken);
            if (registration is ArtifactRegistrationResult.Rejected rejected)
            {
                return StageExecutionResult.Permanent("packaging.artifact_rejected", rejected.Message);
            }

            string manifestPath = Path.Combine(directory, $"{job.VariantId}.manifest.json");
            string manifestJson = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    jobId = job.JobId,
                    variantId = job.VariantId,
                    finalArtifactId = finalArtifact.ArtifactId,
                    qaArtifactId = qa.ArtifactId,
                    sha256 = hash,
                    sizeBytes = finalFile.Length
                });
            string manifestTemporary = temporaryPath + ".json";
            try
            {
                if (File.Exists(manifestPath))
                {
                    if (await File.ReadAllTextAsync(manifestPath, cancellationToken) != manifestJson)
                        return StageExecutionResult.Permanent("packaging.manifest_conflict", "An immutable manifest already exists with different content.");
                }
                else
                {
                    await File.WriteAllTextAsync(manifestTemporary, manifestJson, cancellationToken);
                    File.Move(manifestTemporary, manifestPath);
                }
            }
            finally { if (File.Exists(manifestTemporary)) File.Delete(manifestTemporary); }
            FileInfo manifestFile = new(manifestPath);
            string manifestHash;
            await using (FileStream manifestStream = new(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                manifestHash = Convert.ToHexString(await SHA256.HashDataAsync(manifestStream, cancellationToken)).ToLowerInvariant();
            }
            ArtifactRecord? existingManifest = await artifactStore.FindLatestAsync(job.JobId, "manifest", cancellationToken);
            ArtifactRegistrationResult manifestRegistration = existingManifest is not null
                ? new ArtifactRegistrationResult.Existing(existingManifest) : await artifactStore.RegisterAsync(
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
        return SafeFileTree.Resolve(workspaceRoot, relativePath);
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    private static bool MatchesId(JsonElement parent, string name, Guid expected) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement id)
        && id.ValueKind == JsonValueKind.Object
        && id.TryGetProperty("value", out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetGuid(out Guid actual) && actual == expected;
}
