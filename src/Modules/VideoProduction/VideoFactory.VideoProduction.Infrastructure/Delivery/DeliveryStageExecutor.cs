using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Packaging;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using System.Security.Cryptography;
using System.Text.Json;

namespace VideoFactory.VideoProduction.Infrastructure.Delivery;

/// <summary>Доставляет опубликованный MP4 и не выполняет повторный render.</summary>
public sealed class DeliveryStageExecutor : IJobStageExecutor
{
    private readonly IArtifactStore artifactStore;
    private readonly IVideoDelivery delivery;
    private readonly string workspaceRoot;
    private readonly long recipientId;
    private readonly IDeliveryReceiptStore receiptStore;
    private readonly IEditPlanRevisionStore? planStore;

    /// <summary>Создаёт исполнитель Delivery.</summary>
    public DeliveryStageExecutor(IArtifactStore artifactStore, IVideoDelivery delivery, IDeliveryReceiptStore receiptStore, string workspaceRoot, long recipientId, IEditPlanRevisionStore? planStore = null)
    {
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        this.receiptStore = receiptStore ?? throw new ArgumentNullException(nameof(receiptStore));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (recipientId <= 0) throw new ArgumentOutOfRangeException(nameof(recipientId));
        this.recipientId = recipientId;
        this.planStore = planStore;
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken)
    {
        if (job.Progress.Stage != ProductionStage.Delivery)
        {
            return StageExecutionResult.Permanent("delivery.invalid_stage", "Delivery executor received an unexpected stage.");
        }

        ArtifactRecord? finalArtifact = await artifactStore.FindLatestAsync(job.JobId, job.VariantId, "final-mp4", cancellationToken);
        if (finalArtifact is null)
        {
            return StageExecutionResult.Retryable("delivery.final_missing", "The packaged MP4 is not available yet.");
        }

        string path = ResolvePath(finalArtifact.RelativePath);
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
        {
            return StageExecutionResult.Permanent("delivery.file_missing", "The packaged MP4 does not exist.");
        }
        await using (FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
            if (file.Length != finalArtifact.SizeBytes || actualHash != finalArtifact.Sha256)
                return StageExecutionResult.Permanent("delivery.file_changed", "The packaged MP4 changed after verification.");
        }
        ArtifactRecord? manifest = await artifactStore.FindLatestAsync(job.JobId, job.VariantId, "manifest", cancellationToken);
        if (manifest is null || manifest.ParentArtifactId != finalArtifact.ArtifactId || !File.Exists(ResolvePath(manifest.RelativePath)))
            return StageExecutionResult.Permanent("delivery.manifest_missing", "Finalization has not completed.");

        try
        {
            string manifestPath = ResolvePath(manifest.RelativePath);
            byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            string manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            if (manifest.SizeBytes != manifestBytes.LongLength || manifest.Sha256 != manifestHash)
                return StageExecutionResult.Permanent("delivery.manifest_changed", "The delivery manifest changed after finalization.");

            using JsonDocument document = JsonDocument.Parse(manifestBytes);
            JsonElement root = document.RootElement;
            FinalManifestContractDispatch dispatch = FinalManifestContractDispatcher.Dispatch(root);
            if (dispatch.Status != FinalManifestContractStatus.Accepted)
                return StageExecutionResult.Permanent("delivery.manifest_invalid", "The delivery manifest contract is invalid or unsupported.");
            bool evidenceManifest = dispatch.Version == 2;
            ArtifactRecord? qa = await artifactStore.FindLatestAsync(job.JobId, job.VariantId, "qa-report", cancellationToken);
            ArtifactRecord? render = await artifactStore.FindLatestAsync(job.JobId, job.VariantId, "render-output", cancellationToken);
            if (evidenceManifest)
            {
                (_, render) = await VerifyArtifactReferenceAsync(root.GetProperty("render"), cancellationToken);
                (_, qa) = await VerifyArtifactReferenceAsync(root.GetProperty("qa"), cancellationToken);
            }
            if (!MatchesId(root, "jobId", job.JobId.Value)
                || !MatchesId(root, "variantId", job.VariantId.Value)
                || (!evidenceManifest && !MatchesId(root, "finalArtifactId", finalArtifact.ArtifactId.Value)))
                return StageExecutionResult.Permanent("delivery.manifest_mismatch", "The delivery manifest does not identify this Job, Variant, or final artifact.");
            if (qa is null || render is null)
                return StageExecutionResult.Permanent("delivery.manifest_mismatch", "The delivery manifest inputs are missing.");
            if (qa.ArtifactType != "qa-report"
                || render.ArtifactType is not ("render-output" or "delivery-encode-output")
                || finalArtifact.ArtifactType != "final-mp4"
                || qa.JobId != job.JobId || qa.VariantId != job.VariantId
                || render.JobId != job.JobId || render.VariantId != job.VariantId
                || finalArtifact.JobId != job.JobId || finalArtifact.VariantId != job.VariantId
                || qa.ParentArtifactId != render.ArtifactId
                || finalArtifact.ParentArtifactId != render.ArtifactId)
                return StageExecutionResult.Permanent("delivery.artifact_lineage_mismatch", "The manifest artifacts do not form the verified render, QA, and final lineage.");
            if (!evidenceManifest && !MatchesId(root, "qaArtifactId", qa.ArtifactId.Value))
                return StageExecutionResult.Permanent("delivery.manifest_mismatch", "The delivery manifest does not identify the QA artifact.");
            if (!root.TryGetProperty("render", out JsonElement renderElement)
                || !MatchesId(renderElement, "artifactId", render.ArtifactId.Value)
                || !MatchesString(renderElement, "sha256", render.Sha256)
                || !MatchesLong(renderElement, "sizeBytes", render.SizeBytes))
                return StageExecutionResult.Permanent("delivery.manifest_mismatch", "The delivery manifest does not match the render artifact.");
            if (!root.TryGetProperty("qa", out JsonElement qaElement)
                || !MatchesId(qaElement, "artifactId", qa.ArtifactId.Value)
                || !MatchesString(qaElement, "sha256", qa.Sha256)
                || !MatchesLong(qaElement, "sizeBytes", qa.SizeBytes))
                return StageExecutionResult.Permanent("delivery.manifest_mismatch", "The delivery manifest does not match the QA artifact.");
            if (!root.TryGetProperty("final", out JsonElement final)
                || !MatchesId(final, "artifactId", finalArtifact.ArtifactId.Value)
                || !MatchesString(final, "sha256", finalArtifact.Sha256)
                || !MatchesLong(final, "sizeBytes", finalArtifact.SizeBytes))
                return StageExecutionResult.Permanent("delivery.manifest_mismatch", "The delivery manifest does not match the final artifact.");

            if (planStore is not null)
            {
                if (job.CurrentApprovedEditPlanRevisionId is null
                    || !MatchesId(root, "planRevisionId", job.CurrentApprovedEditPlanRevisionId.Value.Value)
                    || !root.TryGetProperty("planHash", out JsonElement planHash)
                    || planHash.ValueKind != JsonValueKind.String)
                {
                    return StageExecutionResult.Permanent("delivery.plan_mismatch", "The delivery manifest does not identify the approved edit plan.");
                }

                EditPlanRevision? approved = await planStore.FindApprovedAsync(job.JobId, job.VariantId, cancellationToken);
                if (approved is null || approved.RevisionId != job.CurrentApprovedEditPlanRevisionId.Value
                    || approved.PlanHash != planHash.GetString())
                {
                    return StageExecutionResult.Permanent("delivery.plan_mismatch", "The delivery manifest does not match the approved edit plan.");
                }

                if (evidenceManifest)
                {
                    if (approved.Audit is null
                        || !root.TryGetProperty("audit", out JsonElement audit)
                        || !MatchesString(audit, "sha256", EditPlanRevisionIdentity.CalculateAuditHash(approved.Audit))
                        || !audit.TryGetProperty("skillCount", out JsonElement skillCount)
                        || !skillCount.TryGetInt32(out int count)
                        || count != approved.Audit.Skills.Length)
                        return StageExecutionResult.Permanent("delivery.audit_mismatch", "The delivery manifest does not match the approved editing skill audit.");

                    if (!await VerifyCriticChainAsync(job, root.GetProperty("criticChain"), cancellationToken))
                        return StageExecutionResult.Permanent("delivery.critic_chain_mismatch", "The delivery manifest critic chain does not match registered artifacts.");
                    JsonElement finalCriticRender = root.GetProperty("criticChain")
                        .EnumerateArray().Last().GetProperty("render").GetProperty("artifactId");
                    if (!finalCriticRender.TryGetGuid(out Guid criticRenderGuid)
                        || (render.ArtifactType == "render-output" && render.ArtifactId.Value != criticRenderGuid)
                        || (render.ArtifactType == "delivery-encode-output"
                            && render.ParentArtifactId != new ArtifactId(criticRenderGuid)))
                        return StageExecutionResult.Permanent("delivery.critic_render_lineage_mismatch", "The packaged render is not derived from the final critic-approved render.");
                }
            }
        }
        catch (JsonException)
        {
            return StageExecutionResult.Permanent("delivery.manifest_invalid", "The delivery manifest is invalid.");
        }

        if (await receiptStore.HasSucceededAsync(
                job.JobId,
                job.VariantId,
                finalArtifact.ArtifactId,
                recipientId,
                cancellationToken))
            return StageExecutionResult.Success("delivery.already_succeeded", "The final artifact was already delivered.");

        VideoDeliveryResult result = await delivery.SendAsync(
            new VideoDeliveryRequest(job.JobId, job.VariantId, path, "Готовый ролик", recipientId),
            cancellationToken);
        await receiptStore.AppendAsync(
            new DeliveryReceipt(
                Guid.NewGuid(),
                job.JobId,
                job.VariantId,
                finalArtifact.ArtifactId,
                recipientId,
                result.RemoteMessageId,
                result.IsSuccess ? "succeeded" : "failed",
                result.ErrorCode,
                result.ErrorMessage),
            cancellationToken);
        return result.IsSuccess
            ? StageExecutionResult.Success()
            : StageExecutionResult.Retryable(result.ErrorCode ?? "delivery.failed", result.ErrorMessage ?? "Video delivery failed.");
    }

    private string ResolvePath(string relativePath)
    {
        return SafeFileTree.Resolve(workspaceRoot, relativePath);
    }

    private static bool MatchesId(JsonElement parent, string name, Guid expected) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement id)
        && ((id.ValueKind == JsonValueKind.String && id.TryGetGuid(out Guid direct) && direct == expected)
            || (id.ValueKind == JsonValueKind.Object
                && id.TryGetProperty("value", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && value.TryGetGuid(out Guid actual) && actual == expected));

    private static bool MatchesString(JsonElement parent, string name, string? expected) =>
        expected is not null && parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String && value.GetString() == expected;

    private static bool MatchesLong(JsonElement parent, string name, long? expected) =>
        expected is not null && parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long actual) && actual == expected.Value;

    private async Task<bool> VerifyCriticChainAsync(
        JobCreationRecord job,
        JsonElement chain,
        CancellationToken cancellationToken)
    {
        foreach (JsonElement attempt in chain.EnumerateArray())
        {
            (bool renderValid, ArtifactRecord? render) = await VerifyArtifactReferenceAsync(
                attempt.GetProperty("render"), cancellationToken);
            (bool reportValid, ArtifactRecord? report) = await VerifyArtifactReferenceAsync(
                attempt.GetProperty("criticReport"), cancellationToken);
            if (!renderValid || !reportValid || render is null || report is null || report.ParentArtifactId != render.ArtifactId
                || render.ArtifactType != "render-output" || report.ArtifactType != "objective-critique-report"
                || render.JobId != job.JobId || render.VariantId != job.VariantId
                || report.JobId != job.JobId || report.VariantId != job.VariantId
                || string.IsNullOrWhiteSpace(report.MetadataJson))
                return false;
            try
            {
                using JsonDocument reportDocument = JsonDocument.Parse(report.MetadataJson);
                JsonElement reportRoot = reportDocument.RootElement;
                if (reportRoot.GetProperty("sourceRevisionId").GetString() != attempt.GetProperty("sourceRevisionId").GetString()
                    || reportRoot.GetProperty("sourcePlanHash").GetString() != attempt.GetProperty("sourcePlanHash").GetString()
                    || reportRoot.GetProperty("renderArtifactId").GetString() != render.ArtifactId.ToString()
                    || reportRoot.GetProperty("remountNumber").GetInt32() != attempt.GetProperty("remountNumber").GetInt32()
                    || reportRoot.GetProperty("decision").GetString() != attempt.GetProperty("status").GetString()
                    || reportRoot.GetProperty("correctionRevisionId").GetString()
                        != attempt.GetProperty("correctionRevisionId").GetString())
                    return false;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            {
                return false;
            }
        }
        return true;
    }

    private async Task<(bool IsValid, ArtifactRecord? Artifact)> VerifyArtifactReferenceAsync(
        JsonElement reference,
        CancellationToken cancellationToken)
    {
        if (!reference.TryGetProperty("artifactId", out JsonElement id)
            || !id.TryGetGuid(out Guid artifactGuid))
            return (false, null);
        ArtifactRecord? artifact = await artifactStore.GetAsync(new ArtifactId(artifactGuid), cancellationToken);
        bool valid = artifact is not null
            && MatchesString(reference, "sha256", artifact.Sha256)
            && MatchesLong(reference, "sizeBytes", artifact.SizeBytes);
        return (valid, artifact);
    }
}
