using System.Text.Json;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.QualityAssurance;

/// <summary>Проверяет render-output и публикует machine-readable QA report.</summary>
public sealed class VerificationStageExecutor : IJobStageExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IEditPlanRevisionStore planStore;
    private readonly IArtifactStore artifactStore;
    private readonly IOutputQualityAssurance qualityAssurance;
    private readonly string workspaceRoot;
    private readonly OutputQualityOptionsProfile profile;

    /// <summary>Создаёт исполнитель Verification.</summary>
    public VerificationStageExecutor(
        IEditPlanRevisionStore planStore,
        IArtifactStore artifactStore,
        IOutputQualityAssurance qualityAssurance,
        string workspaceRoot,
        OutputQualityOptionsProfile profile)
    {
        this.planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.qualityAssurance = qualityAssurance ?? throw new ArgumentNullException(nameof(qualityAssurance));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken)
    {
        if (job.Progress.Stage != ProductionStage.Verification)
        {
            return StageExecutionResult.Permanent("verification.invalid_stage", "Verification executor received an unexpected stage.");
        }

        if (job.CurrentApprovedEditPlanRevisionId is null)
        {
            return StageExecutionResult.Permanent("verification.plan_missing", "Verification requires an approved edit plan.");
        }

        EditPlanRevision? revision = await planStore.GetAsync(job.CurrentApprovedEditPlanRevisionId.Value, cancellationToken);
        if (revision is null || revision.JobId != job.JobId || revision.VariantId != job.VariantId)
        {
            return StageExecutionResult.Permanent("verification.plan_not_found", "The approved edit plan was not found for this Job.");
        }

        ArtifactRecord? render = await artifactStore.FindLatestAsync(job.JobId, "render-output", cancellationToken);
        if (render is null)
        {
            return StageExecutionResult.Retryable("verification.render_output_missing", "The render output is not available yet.");
        }

        string outputPath = ResolvePath(render.RelativePath);
        OutputQualityResult result = await qualityAssurance.VerifyAsync(
            new OutputQualityRequest(
                outputPath,
                profile.ExpectedWidth,
                profile.ExpectedHeight,
                revision.Plan.Output.FrameRate,
                revision.Plan.Output.DurationFrames,
                profile.DurationToleranceFrames,
                profile.RequireAudio,
                profile.ExpectedPixelFormat),
            cancellationToken);

        string reportPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "qa-report.json");
        string reportJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            jobId = job.JobId,
            variantId = job.VariantId,
            planRevisionId = revision.RevisionId,
            planHash = revision.PlanHash,
            status = result.IsSuccess ? "passed" : "failed",
            errorCode = result.ErrorCode,
            errorMessage = result.ErrorMessage,
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
        }, JsonOptions);
        await File.WriteAllTextAsync(reportPath, reportJson, cancellationToken);

        string reportHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(reportJson))).ToLowerInvariant();
        ArtifactRecord report = new(
            ArtifactId.New(),
            "qa-report",
            Path.GetRelativePath(workspaceRoot, reportPath).Replace(Path.DirectorySeparatorChar, '/'),
            reportHash,
            new FileInfo(reportPath).Length,
            reportJson,
            render.ArtifactId,
            "video-qa/1",
            job.JobId);
        ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(report, cancellationToken);
        if (registration is ArtifactRegistrationResult.Rejected rejected)
        {
            return StageExecutionResult.Permanent("verification.report_rejected", rejected.Message);
        }

        return result.IsSuccess
            ? StageExecutionResult.Success()
            : StageExecutionResult.Permanent(result.ErrorCode ?? "verification.failed", result.ErrorMessage ?? "Output QA failed.");
    }

    private string ResolvePath(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = workspaceRoot.EndsWith(Path.DirectorySeparatorChar) ? workspaceRoot : workspaceRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Artifact path escaped workspace root.");
        }

        return path;
    }
}

/// <summary>Профиль технических ограничений итогового файла.</summary>
public sealed record OutputQualityOptionsProfile(
    int ExpectedWidth,
    int ExpectedHeight,
    int DurationToleranceFrames,
    bool RequireAudio,
    string? ExpectedPixelFormat,
    long MaxTelegramBytes);
