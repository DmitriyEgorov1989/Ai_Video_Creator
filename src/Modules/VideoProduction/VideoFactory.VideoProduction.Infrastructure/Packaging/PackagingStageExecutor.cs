using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using VideoFactory.VideoProduction.Infrastructure.QualityAssurance;

namespace VideoFactory.VideoProduction.Infrastructure.Packaging;

/// <summary>Атомарно публикует MP4 после успешного Verification.</summary>
public sealed class PackagingStageExecutor : IJobStageExecutor
{
    private readonly IArtifactStore artifactStore;
    private readonly string workspaceRoot;
    private readonly long maxTelegramBytes;
    private readonly IStorageBudget storageBudget;
    private readonly IControlledVideoEncoder? controlledEncoder;
    private readonly IEditPlanRevisionStore? planStore;
    private readonly IOutputQualityAssurance? qualityAssurance;
    private readonly OutputQualityOptionsProfile? qualityProfile;
    private readonly int maxDeliveryEncodeAttempts;
    private readonly long deliveryEncodeVideoBitrate;
    private readonly long deliveryEncodeAudioBitrate;

    /// <summary>Создаёт исполнитель Packaging.</summary>
    public PackagingStageExecutor(IArtifactStore artifactStore, string workspaceRoot, long maxTelegramBytes, IStorageBudget? storageBudget = null)
        : this(artifactStore, workspaceRoot, maxTelegramBytes, storageBudget, null, null, null, null, 0, 3_500_000, 96_000)
    {
    }

    /// <summary>Создаёт Packaging с одним контролируемым encode и повторным QA.</summary>
    public PackagingStageExecutor(
        IArtifactStore artifactStore,
        string workspaceRoot,
        long maxTelegramBytes,
        IStorageBudget? storageBudget,
        IControlledVideoEncoder? controlledEncoder,
        IEditPlanRevisionStore? planStore,
        IOutputQualityAssurance? qualityAssurance,
        OutputQualityOptionsProfile? qualityProfile,
        int maxDeliveryEncodeAttempts,
        long deliveryEncodeVideoBitrate = 3_500_000,
        long deliveryEncodeAudioBitrate = 96_000)
    {
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (maxTelegramBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTelegramBytes));
        this.maxTelegramBytes = maxTelegramBytes;
        this.storageBudget = storageBudget ?? StorageBudgetRunner.Default;
        this.controlledEncoder = controlledEncoder;
        this.planStore = planStore;
        this.qualityAssurance = qualityAssurance;
        this.qualityProfile = qualityProfile;
        if (maxDeliveryEncodeAttempts is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(maxDeliveryEncodeAttempts));
        this.maxDeliveryEncodeAttempts = maxDeliveryEncodeAttempts;
        if (deliveryEncodeVideoBitrate <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryEncodeVideoBitrate));
        if (deliveryEncodeAudioBitrate <= 0) throw new ArgumentOutOfRangeException(nameof(deliveryEncodeAudioBitrate));
        this.deliveryEncodeVideoBitrate = deliveryEncodeVideoBitrate;
        this.deliveryEncodeAudioBitrate = deliveryEncodeAudioBitrate;
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

        EditPlanRevision? approvedRevision = null;
        try
        {
            if (planStore is not null)
            {
                approvedRevision = job.CurrentApprovedEditPlanRevisionId is null
                    ? null
                    : await planStore.GetAsync(job.CurrentApprovedEditPlanRevisionId.Value, cancellationToken);
                if (approvedRevision is null
                    || approvedRevision.JobId != job.JobId
                    || approvedRevision.VariantId != job.VariantId)
                {
                    return StageExecutionResult.Permanent("packaging.plan_missing", "The approved edit plan was not found for this Job.");
                }
            }
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
                 || (approvedRevision is not null
                     && (!root.TryGetProperty("planHash", out JsonElement planHash)
                         || planHash.ValueKind != JsonValueKind.String
                         || planHash.GetString() != approvedRevision.PlanHash))
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
            if (controlledEncoder is null || planStore is null || qualityAssurance is null || qualityProfile is null || maxDeliveryEncodeAttempts == 0)
                return StageExecutionResult.Permanent("packaging.telegram_size_exceeded", "The verified MP4 exceeds the configured Telegram delivery limit.");

            ArtifactRecord? encoded = await artifactStore.FindLatestAsync(job.JobId, "delivery-encode-output", cancellationToken);
            if (encoded is null)
            {
                EditPlanRevision? revision = job.CurrentApprovedEditPlanRevisionId is null
                    ? null : await planStore.GetAsync(job.CurrentApprovedEditPlanRevisionId.Value, cancellationToken);
                if (revision is null)
                    return StageExecutionResult.Permanent("packaging.encode_plan_missing", "Controlled encode requires the approved edit plan.");

                string encodeDirectory = SafeFileTree.Resolve(workspaceRoot,
                    $"data/jobs/{job.JobId}/renders/{job.VariantId}/delivery-encode");
                Directory.CreateDirectory(encodeDirectory);
                string encodedPath = Path.Combine(encodeDirectory, "output.mp4");
                ControlledEncodeResult encode = await controlledEncoder.EncodeAsync(
                    new ControlledEncodeRequest(sourcePath, encodedPath, deliveryEncodeVideoBitrate, deliveryEncodeAudioBitrate), cancellationToken);
                if (!encode.IsSuccess)
                    return StageExecutionResult.Permanent(encode.ErrorCode ?? "packaging.encode_failed", encode.ErrorMessage ?? "Controlled encode failed.");

                OutputQualityResult encodedQa = await qualityAssurance.VerifyAsync(
                    new OutputQualityRequest(encodedPath, qualityProfile.ExpectedWidth, qualityProfile.ExpectedHeight,
                        revision.Plan.Output.FrameRate, revision.Plan.Output.DurationFrames, qualityProfile.DurationToleranceFrames,
                        qualityProfile.RequireAudio, qualityProfile.ExpectedPixelFormat,
                        ExpectedVideoCodec: "h264", ExpectedAudioCodec: "aac",
                        ExpectedLoudnessLufs: revision.Presentation?.Audio?.TargetLoudnessLufs,
                        LoudnessToleranceLufs: 1m,
                        MaximumTruePeakDbtp: revision.Presentation?.Audio?.TruePeakDbtp), cancellationToken);
                long encodedSize = new FileInfo(encodedPath).Length;
                if (!encodedQa.IsSuccess)
                    return StageExecutionResult.Permanent("packaging.encode_qa_failed", encodedQa.ErrorMessage ?? "Controlled encode did not pass QA.");
                if (encodedSize > maxTelegramBytes)
                    return StageExecutionResult.Permanent("packaging.telegram_size_exceeded_after_encode", "The controlled encode still exceeds the configured Telegram delivery limit.");

                string encodedHash = await HashAsync(encodedPath, cancellationToken);
                encoded = new ArtifactRecord(ArtifactId.New(), "delivery-encode-output",
                    Path.GetRelativePath(workspaceRoot, encodedPath).Replace(Path.DirectorySeparatorChar, '/'), encodedHash,
                    encodedSize, JsonSerializer.Serialize(new { jobId = job.JobId, variantId = job.VariantId,
                        parentArtifactId = render.ArtifactId, encodeAttempts = 1, videoBitrateBitsPerSecond = deliveryEncodeVideoBitrate,
                        audioBitrateBitsPerSecond = deliveryEncodeAudioBitrate }), render.ArtifactId, "ffmpeg-delivery-encode/1", job.JobId);
                ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(encoded, cancellationToken);
                if (registration is ArtifactRegistrationResult.Rejected rejected)
                    return StageExecutionResult.Permanent("packaging.encode_artifact_rejected", rejected.Message);

                ArtifactId qaId = ArtifactId.New();
                string qaPath = Path.Combine(encodeDirectory, $"qa-report-{qaId}.json");
                string qaJson = JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    jobId = job.JobId,
                    variantId = job.VariantId,
                    planRevisionId = revision.RevisionId,
                    planHash = revision.PlanHash,
                    status = "passed",
                    errorCode = (string?)null,
                    errorMessage = (string?)null,
                    output = new { artifactId = encoded.ArtifactId, path = encoded.RelativePath,
                        sha256 = encoded.Sha256, sizeBytes = encoded.SizeBytes,
                        width = encodedQa.Width, height = encodedQa.Height, durationFrames = encodedQa.DurationFrames }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await File.WriteAllTextAsync(qaPath, qaJson, cancellationToken);
                string qaHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(qaJson))).ToLowerInvariant();
                qa = new ArtifactRecord(qaId, "qa-report",
                    Path.GetRelativePath(workspaceRoot, qaPath).Replace(Path.DirectorySeparatorChar, '/'), qaHash,
                    new FileInfo(qaPath).Length, qaJson, encoded.ArtifactId, "video-qa/1", job.JobId);
                ArtifactRegistrationResult qaRegistration = await artifactStore.RegisterAsync(qa, cancellationToken);
                if (qaRegistration is ArtifactRegistrationResult.Rejected qaRejected)
                    return StageExecutionResult.Permanent("packaging.encode_qa_report_rejected", qaRejected.Message);
            }

            render = encoded;
            sourcePath = ResolvePath(render.RelativePath);
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
                    planRevisionId = job.CurrentApprovedEditPlanRevisionId,
                    planHash = approvedRevision?.PlanHash,
                    render = new { artifactId = render.ArtifactId, sha256 = render.Sha256, sizeBytes = render.SizeBytes },
                    qa = new { artifactId = qa.ArtifactId, sha256 = qa.Sha256, sizeBytes = qa.SizeBytes },
                    final = new { artifactId = finalArtifact.ArtifactId, sha256 = hash, sizeBytes = finalFile.Length },
                    finalArtifactId = finalArtifact.ArtifactId,
                    qaArtifactId = qa.ArtifactId,
                    sha256 = hash,
                    sizeBytes = finalFile.Length
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
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
                    manifestJson,
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
