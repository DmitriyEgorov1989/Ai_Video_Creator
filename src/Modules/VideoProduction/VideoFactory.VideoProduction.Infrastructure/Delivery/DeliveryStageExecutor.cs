using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Delivery;

/// <summary>Доставляет опубликованный MP4 и не выполняет повторный render.</summary>
public sealed class DeliveryStageExecutor : IJobStageExecutor
{
    private readonly IArtifactStore artifactStore;
    private readonly IVideoDelivery delivery;
    private readonly string workspaceRoot;
    private readonly long recipientId;
    private readonly IDeliveryReceiptStore receiptStore;

    /// <summary>Создаёт исполнитель Delivery.</summary>
    public DeliveryStageExecutor(IArtifactStore artifactStore, IVideoDelivery delivery, IDeliveryReceiptStore receiptStore, string workspaceRoot, long recipientId)
    {
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        this.receiptStore = receiptStore ?? throw new ArgumentNullException(nameof(receiptStore));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (recipientId <= 0) throw new ArgumentOutOfRangeException(nameof(recipientId));
        this.recipientId = recipientId;
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken)
    {
        if (job.Progress.Stage != ProductionStage.Delivery)
        {
            return StageExecutionResult.Permanent("delivery.invalid_stage", "Delivery executor received an unexpected stage.");
        }

        ArtifactRecord? finalArtifact = await artifactStore.FindLatestAsync(job.JobId, "final-mp4", cancellationToken);
        if (finalArtifact is null)
        {
            return StageExecutionResult.Retryable("delivery.final_missing", "The packaged MP4 is not available yet.");
        }

        string path = ResolvePath(finalArtifact.RelativePath);
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
        {
            return StageExecutionResult.Permanent("delivery.file_missing", "The packaged MP4 does not exist.");
        }

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
        string path = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = workspaceRoot.EndsWith(Path.DirectorySeparatorChar) ? workspaceRoot : workspaceRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Artifact path escaped workspace root.");
        return path;
    }
}
