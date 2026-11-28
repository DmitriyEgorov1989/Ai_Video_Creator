using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Packaging;
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
    private readonly ICritiqueAttemptStore? critiqueAttemptStore;

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
        long deliveryEncodeAudioBitrate = 96_000,
        ICritiqueAttemptStore? critiqueAttemptStore = null)
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
        this.critiqueAttemptStore = critiqueAttemptStore;
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken)
    {
        if (job.Progress.Stage != ProductionStage.Packaging)
        {
            return StageExecutionResult.Permanent("packaging.invalid_stage", "Packaging executor received an unexpected stage.");
        }

        ArtifactRecord? existing = await artifactStore.FindLatestAsync(job.JobId, job.VariantId, "final-mp4", cancellationToken);

        ArtifactRecord? render = await artifactStore.FindLatestAsync(job.JobId, job.VariantId, "render-output", cancellationToken);
        ArtifactRecord? qa = await artifactStore.FindLatestAsync(job.JobId, job.VariantId, "qa-report", cancellationToken);
        if (render is null || qa is null)
        {
            return StageExecutionResult.Retryable("packaging.input_missing", "A verified render output is not available yet.");
        }
        if (qa.ParentArtifactId != render.ArtifactId && qa.ParentArtifactId is ArtifactId qaParentId)
        {
            ArtifactRecord? encodedParent = await artifactStore.GetAsync(qaParentId, cancellationToken);
            if (encodedParent?.ArtifactType == "delivery-encode-output"
                && encodedParent.ParentArtifactId == render.ArtifactId)
                render = encodedParent;
        }

        EditPlanRevision? approvedRevision = null;
        List<object>? criticChain = null;
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
            (qa, StageExecutionResult? recoveryError) = await RecoverLegacyQaAsync(
                job, approvedRevision, render, qa, cancellationToken);
            if (recoveryError is not null) return recoveryError;
            StageExecutionResult? qaError = await ValidateQaAsync(
                job, approvedRevision, render, qa, cancellationToken);
            if (qaError is not null) return qaError;

            if (approvedRevision?.Audit is not null)
            {
                if (critiqueAttemptStore is null || planStore is null)
                    return StageExecutionResult.Permanent("packaging.critic_store_missing", "Manifest V2 requires the critic attempt store.");
                (string? error, List<object> chain) = await BuildCriticChainAsync(
                    job,
                    approvedRevision,
                    cancellationToken);
                if (error is not null)
                    return StageExecutionResult.Permanent("packaging.critic_chain_invalid", error);
                criticChain = chain;
            }
        }
        catch (JsonException) { return StageExecutionResult.Permanent("packaging.qa_invalid", "Output QA report is invalid."); }

        string sourcePath = ResolvePath(render.RelativePath);
        if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length <= 0)
        {
            return StageExecutionResult.Permanent("packaging.render_missing", "The verified render file does not exist.");
        }
        if (new FileInfo(sourcePath).Length > maxTelegramBytes)
        {
            if (controlledEncoder is null || planStore is null || qualityAssurance is null || qualityProfile is null || maxDeliveryEncodeAttempts == 0)
                return StageExecutionResult.Permanent("packaging.telegram_size_exceeded", "The verified MP4 exceeds the configured Telegram delivery limit.");

            EditPlanRevision? revision = job.CurrentApprovedEditPlanRevisionId is null
                ? null : await planStore.GetAsync(job.CurrentApprovedEditPlanRevisionId.Value, cancellationToken);
            if (revision is null)
                return StageExecutionResult.Permanent("packaging.encode_plan_missing", "Controlled encode requires the approved edit plan.");
            ArtifactRecord sourceRender = render;
            string encodeDirectory = SafeFileTree.Resolve(workspaceRoot,
                $"data/jobs/{job.JobId}/renders/{job.VariantId}/delivery-encode");
            Directory.CreateDirectory(encodeDirectory);
            string encodedPath = Path.Combine(encodeDirectory, "output.mp4");
            ArtifactRecord? encoded = await artifactStore.FindLatestAsync(job.JobId, job.VariantId, "delivery-encode-output", cancellationToken);
            OutputQualityResult? recoveredQa = null;
            if (encoded is null)
            {
                if (File.Exists(encodedPath))
                {
                    recoveredQa = await qualityAssurance.VerifyAsync(
                        CreateEncodedQualityRequest(encodedPath, qualityProfile, revision), cancellationToken);
                    if (!recoveredQa.IsSuccess)
                    {
                        File.Delete(encodedPath);
                        recoveredQa = null;
                    }
                }
                if (recoveredQa is null)
                {
                    ControlledEncodeResult encode = await controlledEncoder.EncodeAsync(
                        new ControlledEncodeRequest(sourcePath, encodedPath, deliveryEncodeVideoBitrate, deliveryEncodeAudioBitrate), cancellationToken);
                    if (!encode.IsSuccess)
                        return StageExecutionResult.Permanent(encode.ErrorCode ?? "packaging.encode_failed", encode.ErrorMessage ?? "Controlled encode failed.");
                }
                long encodedSize = new FileInfo(encodedPath).Length;
                if (encodedSize > maxTelegramBytes)
                    return StageExecutionResult.Permanent("packaging.telegram_size_exceeded_after_encode", "The controlled encode still exceeds the configured Telegram delivery limit.");

                string encodedHash = await HashAsync(encodedPath, cancellationToken);
                encoded = new ArtifactRecord(ArtifactId.New(), "delivery-encode-output",
                    Path.GetRelativePath(workspaceRoot, encodedPath).Replace(Path.DirectorySeparatorChar, '/'), encodedHash,
                    encodedSize, JsonSerializer.Serialize(new { jobId = job.JobId, variantId = job.VariantId,
                        parentArtifactId = render.ArtifactId, encodeAttempts = 1, videoBitrateBitsPerSecond = deliveryEncodeVideoBitrate,
                        audioBitrateBitsPerSecond = deliveryEncodeAudioBitrate }), render.ArtifactId, "ffmpeg-delivery-encode/1", job.JobId, job.VariantId);
                ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(encoded, cancellationToken);
                if (registration is ArtifactRegistrationResult.Rejected rejected)
                    return StageExecutionResult.Permanent("packaging.encode_artifact_rejected", rejected.Message);
            }
            else if (encoded.ParentArtifactId != sourceRender.ArtifactId
                || !await ArtifactFileMatchesAsync(encoded, cancellationToken))
            {
                return StageExecutionResult.Permanent("packaging.encode_mismatch", "The existing controlled encode does not match its verified source render.");
            }

            ArtifactRecord? encodedQaArtifact = (await artifactStore.ListAsync(
                job.JobId, job.VariantId, "qa-report", cancellationToken))
                .LastOrDefault(candidate => candidate.ParentArtifactId == encoded.ArtifactId);
            if (encodedQaArtifact is null)
            {
                OutputQualityResult encodedQa = recoveredQa ?? await qualityAssurance.VerifyAsync(
                    CreateEncodedQualityRequest(encodedPath, qualityProfile, revision), cancellationToken);
                if (!encodedQa.IsSuccess)
                    return StageExecutionResult.Permanent("packaging.encode_qa_failed", encodedQa.ErrorMessage ?? "Controlled encode did not pass QA.");
                ArtifactId qaId = ArtifactId.New();
                string qaPath = Path.Combine(encodeDirectory, $"qa-report-{qaId}.json");
                string qaJson = JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    jobId = job.JobId,
                    variantId = job.VariantId,
                    planRevisionId = revision.RevisionId,
                    planHash = revision.PlanHash,
                    status = "passed",
                    errorCode = (string?)null,
                    errorMessage = (string?)null,
                    checks = new
                    {
                        ffprobePassed = true,
                        fullDecodePassed = true,
                        audioAssertionsPassed = true
                    },
                    output = new { artifactId = encoded.ArtifactId, path = encoded.RelativePath,
                        sha256 = encoded.Sha256, sizeBytes = encoded.SizeBytes,
                        width = encodedQa.Width, height = encodedQa.Height, durationFrames = encodedQa.DurationFrames }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                await File.WriteAllTextAsync(qaPath, qaJson, cancellationToken);
                string qaHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(qaJson))).ToLowerInvariant();
                qa = new ArtifactRecord(qaId, "qa-report",
                    Path.GetRelativePath(workspaceRoot, qaPath).Replace(Path.DirectorySeparatorChar, '/'), qaHash,
                    new FileInfo(qaPath).Length, qaJson, encoded.ArtifactId, "video-qa/2", job.JobId, job.VariantId);
                ArtifactRegistrationResult qaRegistration = await artifactStore.RegisterAsync(qa, cancellationToken);
                if (qaRegistration is ArtifactRegistrationResult.Rejected qaRejected)
                    return StageExecutionResult.Permanent("packaging.encode_qa_report_rejected", qaRejected.Message);
            }
            else
            {
                qa = encodedQaArtifact;
            }

            render = encoded;
            sourcePath = ResolvePath(render.RelativePath);
            StageExecutionResult? encodedQaError = await ValidateQaAsync(
                job, revision, render, qa, cancellationToken);
            if (encodedQaError is not null) return encodedQaError;
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
                job.JobId,
                job.VariantId);
            ArtifactRegistrationResult registration = existing is not null
                ? new ArtifactRegistrationResult.Existing(existing)
                : await artifactStore.RegisterAsync(finalArtifact, cancellationToken);
            if (registration is ArtifactRegistrationResult.Rejected rejected)
            {
                return StageExecutionResult.Permanent("packaging.artifact_rejected", rejected.Message);
            }

            string manifestPath = Path.Combine(directory, $"{job.VariantId}.manifest.json");
            string manifestJson = approvedRevision?.Audit is EditPlanAudit audit
                ? JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    jobId = job.JobId.ToString(),
                    variantId = job.VariantId.ToString(),
                    planRevisionId = approvedRevision.RevisionId.ToString(),
                    planHash = approvedRevision.PlanHash,
                    audit = new
                    {
                        schemaVersion = audit.SchemaVersion,
                        skillCount = audit.Skills.Length,
                        sha256 = EditPlanRevisionIdentity.CalculateAuditHash(audit)
                    },
                    criticChain,
                    render = ArtifactReference(render),
                    qa = ArtifactReference(qa),
                    final = new { artifactId = finalArtifact.ArtifactId.ToString(), sha256 = hash, sizeBytes = finalFile.Length }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : JsonSerializer.Serialize(new
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
            if (approvedRevision?.Audit is not null)
            {
                using JsonDocument manifestDocument = JsonDocument.Parse(manifestJson);
                FinalManifestContractDispatch dispatch =
                    FinalManifestContractDispatcher.Dispatch(manifestDocument.RootElement);
                if (dispatch.Status != FinalManifestContractStatus.Accepted)
                    return StageExecutionResult.Permanent("packaging.manifest_invalid", "Generated Manifest V2 failed contract validation.");
            }
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
            ArtifactRecord? existingManifest = await artifactStore.FindLatestAsync(job.JobId, job.VariantId, "manifest", cancellationToken);
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
                    job.JobId,
                    job.VariantId),
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

    private async Task<(ArtifactRecord Qa, StageExecutionResult? Error)> RecoverLegacyQaAsync(
        JobCreationRecord job,
        EditPlanRevision? approvedRevision,
        ArtifactRecord render,
        ArtifactRecord qa,
        CancellationToken cancellationToken)
    {
        byte[] reportBytes = await File.ReadAllBytesAsync(ResolvePath(qa.RelativePath), cancellationToken);
        using JsonDocument report = JsonDocument.Parse(reportBytes);
        JsonElement root = report.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
            || !schemaVersion.TryGetInt32(out int version)
            || version != 1)
            return (qa, null);

        if (!root.TryGetProperty("status", out JsonElement status)
            || status.ValueKind != JsonValueKind.String
            || status.GetString() != "passed")
            return (qa, StageExecutionResult.Permanent("packaging.qa_not_passed", "Output QA has not passed."));
        if (approvedRevision is null
            || !MatchesId(root, "jobId", job.JobId.Value)
            || !MatchesId(root, "variantId", job.VariantId.Value)
            || !MatchesId(root, "planRevisionId", approvedRevision.RevisionId.Value)
            || !root.TryGetProperty("planHash", out JsonElement planHash)
            || planHash.ValueKind != JsonValueKind.String
            || planHash.GetString() != approvedRevision.PlanHash
            || !root.TryGetProperty("output", out JsonElement output)
            || output.ValueKind != JsonValueKind.Object
            || !MatchesId(output, "artifactId", render.ArtifactId.Value)
            || !output.TryGetProperty("sha256", out JsonElement hash)
            || hash.ValueKind != JsonValueKind.String || hash.GetString() != render.Sha256
            || !output.TryGetProperty("sizeBytes", out JsonElement size)
            || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes != render.SizeBytes
            || qa.ParentArtifactId != render.ArtifactId)
            return (qa, StageExecutionResult.Permanent("packaging.qa_mismatch", "QA report does not match the approved render."));
        if (qa.SizeBytes != reportBytes.LongLength
            || qa.Sha256 != Convert.ToHexStringLower(SHA256.HashData(reportBytes)))
            return (qa, StageExecutionResult.Permanent("packaging.qa_changed", "QA report changed after registration."));
        if (qualityAssurance is null || qualityProfile is null)
            return (qa, StageExecutionResult.Permanent("packaging.qa_upgrade_unavailable", "Output QA V1 cannot be upgraded without current QA configuration."));
        if (!await ArtifactFileMatchesAsync(render, cancellationToken))
            return (qa, StageExecutionResult.Permanent("packaging.render_changed", "Render changed after verification."));

        string renderPath = ResolvePath(render.RelativePath);
        OutputQualityResult result = await qualityAssurance.VerifyAsync(
            CreateEncodedQualityRequest(renderPath, qualityProfile, approvedRevision), cancellationToken);
        ArtifactId reportId = ArtifactId.New();
        string reportPath = Path.Combine(Path.GetDirectoryName(renderPath)!, $"qa-report-{reportId}.json");
        string reportJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            jobId = job.JobId,
            variantId = job.VariantId,
            planRevisionId = approvedRevision.RevisionId,
            planHash = approvedRevision.PlanHash,
            status = result.IsSuccess ? "passed" : "failed",
            errorCode = result.IsSuccess ? null : "verification.failed",
            errorMessage = result.IsSuccess ? null : "Output verification failed. See the stored stage attempt for diagnostics.",
            checks = new
            {
                ffprobePassed = result.IsSuccess,
                fullDecodePassed = result.IsSuccess,
                audioAssertionsPassed = result.IsSuccess
            },
            output = new
            {
                artifactId = render.ArtifactId,
                path = render.RelativePath,
                sha256 = render.Sha256,
                sizeBytes = render.SizeBytes,
                width = result.Width,
                height = result.Height,
                durationFrames = result.DurationFrames
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string temporaryPath = reportPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, reportJson, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, reportPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        byte[] v2Bytes = System.Text.Encoding.UTF8.GetBytes(reportJson);
        ArtifactRecord v2 = new(
            reportId,
            "qa-report",
            Path.GetRelativePath(workspaceRoot, reportPath).Replace(Path.DirectorySeparatorChar, '/'),
            Convert.ToHexStringLower(SHA256.HashData(v2Bytes)),
            v2Bytes.LongLength,
            reportJson,
            render.ArtifactId,
            "video-qa/2",
            job.JobId,
            job.VariantId);
        ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(v2, cancellationToken);
        if (registration is ArtifactRegistrationResult.Rejected rejected)
            return (qa, StageExecutionResult.Permanent("packaging.qa_upgrade_rejected", rejected.Message));
        return (v2, null);
    }

    private async Task<StageExecutionResult?> ValidateQaAsync(
        JobCreationRecord job,
        EditPlanRevision? approvedRevision,
        ArtifactRecord render,
        ArtifactRecord qa,
        CancellationToken cancellationToken)
    {
        try
        {
            byte[] reportBytes = await File.ReadAllBytesAsync(ResolvePath(qa.RelativePath), cancellationToken);
            using JsonDocument report = JsonDocument.Parse(reportBytes);
            JsonElement root = report.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion)
                || !schemaVersion.TryGetInt32(out int version) || version != 2
                || !root.TryGetProperty("status", out JsonElement status)
                || status.ValueKind != JsonValueKind.String
                || status.GetString() != "passed"
                || !root.TryGetProperty("checks", out JsonElement checks)
                || !IsTrue(checks, "ffprobePassed")
                || !IsTrue(checks, "fullDecodePassed")
                || !IsTrue(checks, "audioAssertionsPassed"))
                return StageExecutionResult.Permanent("packaging.qa_not_passed", "Output QA has not passed.");
            if (!MatchesId(root, "jobId", job.JobId.Value)
                || !MatchesId(root, "variantId", job.VariantId.Value)
                || job.CurrentApprovedEditPlanRevisionId is null
                || !MatchesId(root, "planRevisionId", job.CurrentApprovedEditPlanRevisionId.Value.Value)
                || (approvedRevision is not null
                    && (!root.TryGetProperty("planHash", out JsonElement planHash)
                        || planHash.ValueKind != JsonValueKind.String
                        || planHash.GetString() != approvedRevision.PlanHash))
                || !root.TryGetProperty("output", out JsonElement output)
                || output.ValueKind != JsonValueKind.Object
                || !MatchesId(output, "artifactId", render.ArtifactId.Value)
                || !output.TryGetProperty("sha256", out JsonElement hash)
                || hash.ValueKind != JsonValueKind.String || hash.GetString() != render.Sha256
                || !output.TryGetProperty("sizeBytes", out JsonElement size)
                || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes) || bytes != render.SizeBytes
                || qa.ParentArtifactId != render.ArtifactId)
                return StageExecutionResult.Permanent("packaging.qa_mismatch", "QA report does not match the approved render.");
            if (qa.SizeBytes != reportBytes.LongLength
                || qa.Sha256 != Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant())
                return StageExecutionResult.Permanent("packaging.qa_changed", "QA report changed after registration.");
            return null;
        }
        catch (JsonException)
        {
            return StageExecutionResult.Permanent("packaging.qa_invalid", "Output QA report is invalid.");
        }
    }

    private static bool IsTrue(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;

    private async Task<(string? Error, List<object> Chain)> BuildCriticChainAsync(
        JobCreationRecord job,
        EditPlanRevision approvedRevision,
        CancellationToken cancellationToken)
    {
        if (critiqueAttemptStore is null || planStore is null)
            return ("Critic history storage is unavailable for Manifest V2.", []);

        IReadOnlyList<CritiqueAttemptRecord> attempts = await critiqueAttemptStore.ListAsync(
            job.JobId,
            job.VariantId,
            cancellationToken);
        IReadOnlyList<ArtifactRecord> reports = await artifactStore.ListAsync(
            job.JobId,
            job.VariantId,
            "objective-critique-report",
            cancellationToken);
        if (attempts.Count == 0)
            return ("The approved revision has no objective critic history.", []);

        List<object> chain = [];
        RevisionId? expectedSourceRevisionId = null;
        for (int index = 0; index < attempts.Count; index++)
        {
            CritiqueAttemptRecord attempt = attempts[index];
            if (attempt.CompletedAt is null || attempt.RemountNumber != index
                || attempt.Status is not ("accepted" or "remount"))
                return ("Critic history contains an incomplete or out-of-order attempt.", []);
            if (index > 0 && attempt.SourceRevisionId != expectedSourceRevisionId)
                return ("Critic remount history contains a disconnected correction revision.", []);
            if (attempt.Status == "remount" && attempt.CorrectionRevisionId is null)
                return ("Critic remount attempt does not identify its correction revision.", []);
            if (attempt.Status == "accepted" && (attempt.CorrectionRevisionId is not null || index != attempts.Count - 1))
                return ("Only the final critic attempt may be accepted.", []);

            EditPlanRevision? sourceRevision = await planStore.GetAsync(attempt.SourceRevisionId, cancellationToken);
            ArtifactRecord? attemptRender = await artifactStore.GetAsync(attempt.RenderArtifactId, cancellationToken);
            ArtifactRecord? report = reports.SingleOrDefault(candidate => candidate.ParentArtifactId == attempt.RenderArtifactId);
            if (sourceRevision is null || attemptRender is null || report is null)
                return ("Critic history refers to a missing plan, render, or report artifact.", []);
            if (!await ArtifactFileMatchesAsync(report, cancellationToken)
                || !ReportMatchesAttempt(report, sourceRevision, attempt))
                return ("Objective critic report does not match its persisted attempt.", []);

            chain.Add(new
            {
                remountNumber = attempt.RemountNumber,
                sourceRevisionId = sourceRevision.RevisionId.ToString(),
                sourcePlanHash = sourceRevision.PlanHash,
                render = ArtifactReference(attemptRender),
                criticReport = ArtifactReference(report),
                status = attempt.Status,
                correctionRevisionId = attempt.CorrectionRevisionId?.ToString()
            });
            expectedSourceRevisionId = attempt.CorrectionRevisionId;
        }

        CritiqueAttemptRecord finalAttempt = attempts[^1];
        if (finalAttempt.Status != "accepted" || finalAttempt.SourceRevisionId != approvedRevision.RevisionId)
            return ("The final critic attempt did not accept the approved revision.", []);
        return (null, chain);
    }

    private async Task<bool> ArtifactFileMatchesAsync(ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        string path = ResolvePath(artifact.RelativePath);
        return File.Exists(path)
            && new FileInfo(path).Length == artifact.SizeBytes
            && await HashAsync(path, cancellationToken) == artifact.Sha256;
    }

    private static bool ReportMatchesAttempt(
        ArtifactRecord report,
        EditPlanRevision sourceRevision,
        CritiqueAttemptRecord attempt)
    {
        if (string.IsNullOrWhiteSpace(report.MetadataJson)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(report.MetadataJson);
            JsonElement root = document.RootElement;
            return root.TryGetProperty("schemaVersion", out JsonElement version)
                && version.TryGetInt32(out int schemaVersion) && schemaVersion == 2
                && root.GetProperty("sourceRevisionId").GetString() == sourceRevision.RevisionId.ToString()
                && root.GetProperty("sourcePlanHash").GetString() == sourceRevision.PlanHash
                && root.GetProperty("renderArtifactId").GetString() == attempt.RenderArtifactId.ToString()
                && root.GetProperty("remountNumber").GetInt32() == attempt.RemountNumber
                && root.GetProperty("decision").GetString() == attempt.Status
                && root.GetProperty("correctionRevisionId").GetString() == attempt.CorrectionRevisionId?.ToString();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static ManifestArtifactReference ArtifactReference(ArtifactRecord artifact) =>
        new(artifact.ArtifactId.ToString(), artifact.Sha256 ?? string.Empty, artifact.SizeBytes ?? 0);

    private string ResolvePath(string relativePath)
    {
        return SafeFileTree.Resolve(workspaceRoot, relativePath);
    }

    private static OutputQualityRequest CreateEncodedQualityRequest(
        string encodedPath,
        OutputQualityOptionsProfile qualityProfile,
        EditPlanRevision revision) => new(
            encodedPath,
            qualityProfile.ExpectedWidth,
            qualityProfile.ExpectedHeight,
            revision.Plan.Output.FrameRate,
            revision.Plan.Output.DurationFrames,
            qualityProfile.DurationToleranceFrames,
            qualityProfile.RequireAudio,
            qualityProfile.ExpectedPixelFormat,
            ExpectedVideoCodec: "h264",
            ExpectedAudioCodec: "aac",
            ExpectedLoudnessLufs: revision.Presentation?.Audio?.TargetLoudnessLufs,
            LoudnessToleranceLufs: 1m,
            MaximumTruePeakDbtp: revision.Presentation?.Audio?.TruePeakDbtp);

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
        && Guid.TryParse(value.GetString(), out Guid actual) && actual == expected;

    private sealed record ManifestArtifactReference(string ArtifactId, string Sha256, long SizeBytes);
}
