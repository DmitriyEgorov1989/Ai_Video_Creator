using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.QualityAssurance;

/// <summary>Проверяет объективные дефекты render-output до обязательного технического QA.</summary>
public sealed class CritiqueStageExecutor : IJobStageExecutor
{
    private readonly IEditPlanRevisionStore planStore;
    private readonly IArtifactStore artifactStore;
    private readonly IRenderedMediaObserver observer;
    private readonly string workspaceRoot;
    private readonly int maxRemounts;
    private readonly ICritiqueAnalysisSource? analysisSource;
    private readonly ICritiqueAttemptStore? attemptStore;
    private readonly IOutputAudioQualityAnalyzer? audioAnalyzer;
    private readonly decimal maximumTruePeakDbtp;
    private readonly TimeSpan audioAnalysisTimeout;
    private readonly int maxOutputBytes;

    /// <summary>Создаёт исполнитель Critique с отдельным лимитом перемонтажей.</summary>
    public CritiqueStageExecutor(
        IEditPlanRevisionStore planStore,
        IArtifactStore artifactStore,
        IRenderedMediaObserver observer,
        string workspaceRoot,
        int maxRemounts,
        ICritiqueAnalysisSource? analysisSource = null,
        ICritiqueAttemptStore? attemptStore = null,
        IOutputAudioQualityAnalyzer? audioAnalyzer = null,
        decimal maximumTruePeakDbtp = -1m,
        TimeSpan? audioAnalysisTimeout = null,
        int maxOutputBytes = 65_536)
    {
        this.planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (maxRemounts < 0) throw new ArgumentOutOfRangeException(nameof(maxRemounts));
        this.maxRemounts = maxRemounts;
        this.analysisSource = analysisSource;
        this.attemptStore = attemptStore;
        this.audioAnalyzer = audioAnalyzer;
        this.maximumTruePeakDbtp = maximumTruePeakDbtp;
        this.audioAnalysisTimeout = audioAnalysisTimeout ?? TimeSpan.FromMinutes(5);
        if (this.audioAnalysisTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(audioAnalysisTimeout));
        if (maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        this.maxOutputBytes = maxOutputBytes;
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(
        JobCreationRecord job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Progress.Stage != ProductionStage.Critique)
        {
            return StageExecutionResult.Permanent(
                "critic.invalid_stage",
                "Critique executor received an unexpected stage.");
        }

        ArtifactRecord? render = await artifactStore.FindLatestAsync(
            job.JobId,
            job.VariantId,
            "render-output",
            cancellationToken);
        if (render is null)
        {
            return StageExecutionResult.Retryable(
                "critic.render_output_missing",
                "The render output is not available yet.");
        }

        if (!TryReadRenderIdentity(render, job.VariantId, out RevisionId sourceRevisionId, out string? sourcePlanHash))
        {
            return StageExecutionResult.Permanent(
                "critic.render_revision_mismatch",
                "The render output does not belong to the approved plan revision.");
        }

        CritiqueAttemptRecord? existingAttempt = attemptStore is null
            ? null
            : await attemptStore.GetAsync(job.VariantId, sourceRevisionId, render.ArtifactId, cancellationToken);
        if (existingAttempt?.CompletedAt is not null)
        {
            return existingAttempt.Status switch
            {
                "accepted" => StageExecutionResult.Success("critic.accepted", "The saved critique accepted this render."),
                "remount" => StageExecutionResult.Success(
                    "critic.remount_required",
                    "The saved critique already created a corrected plan revision.",
                    StageCompletionAction.Remount),
                _ => StageExecutionResult.Permanent(
                    existingAttempt.ErrorCode ?? "critic.rejected",
                    existingAttempt.ErrorMessage ?? "The saved critique rejected this render.")
            };
        }

        if (existingAttempt is not null
            && job.CurrentApprovedEditPlanRevisionId != sourceRevisionId
            && await TryRecoverCompletedRemountAsync(job, render, existingAttempt, cancellationToken))
        {
            return StageExecutionResult.Success(
                "critic.remount_required",
                "The persisted critic evidence restored the corrected plan revision.",
                StageCompletionAction.Remount);
        }

        if (job.CurrentApprovedEditPlanRevisionId != sourceRevisionId)
        {
            return StageExecutionResult.Permanent(
                "critic.render_revision_mismatch",
                "The render output does not belong to the approved plan revision.");
        }

        EditPlanRevision? revision = await planStore.GetAsync(sourceRevisionId, cancellationToken);
        if (revision is null || revision.JobId != job.JobId || revision.VariantId != job.VariantId
            || revision.PlanHash != sourcePlanHash)
        {
            return StageExecutionResult.Permanent(
                "critic.plan_not_found",
                "The approved edit plan was not found for this Job variant.");
        }

        string outputPath = SafeFileTree.Resolve(workspaceRoot, render.RelativePath);
        if (!File.Exists(outputPath))
        {
            return StageExecutionResult.Retryable(
                "critic.render_file_missing",
                "The render output file is not available yet.");
        }

        int remountNumber = Math.Max(0, (await artifactStore.ListAsync(
            job.JobId,
            job.VariantId,
            "render-output",
            cancellationToken)).Count - 1);
        DateTimeOffset startedAt = existingAttempt?.CreatedAt ?? DateTimeOffset.UtcNow;
        await SaveAttemptAsync(
            new CritiqueAttemptRecord(
                job.JobId,
                job.VariantId,
                revision.RevisionId,
                render.ArtifactId,
                null,
                remountNumber,
                0m,
                null,
                "running",
                null,
                null,
                startedAt,
                null),
            cancellationToken);

        RenderedMediaObservationResult observation = await observer.ObserveAsync(
            new RenderedMediaObservationRequest(
                outputPath,
                revision.Plan.Output.FrameRate,
                revision.Plan.Output.DurationFrames),
            cancellationToken);
        if (observation is RenderedMediaObservationResult.Failed failed)
        {
            return StageExecutionResult.Retryable(failed.ErrorCode, failed.ErrorMessage);
        }

        RenderedMediaObservation measured = ((RenderedMediaObservationResult.Succeeded)observation).Observation;
        if (audioAnalyzer is not null)
        {
            OutputAudioQualityResult audio = await audioAnalyzer.AnalyzeAsync(
                new OutputAudioQualityRequest(outputPath, audioAnalysisTimeout, maxOutputBytes),
                cancellationToken);
            if (!audio.IsSuccess)
            {
                return StageExecutionResult.Retryable(
                    audio.ErrorCode ?? "critic.audio_measurement_failed",
                    audio.ErrorMessage ?? "The rendered audio could not be measured.");
            }

            measured = measured with { TruePeakDbtp = audio.TruePeakDbtp };
        }

        bool audioPeakExceeded = measured.TruePeakDbtp is decimal truePeak
            && truePeak > maximumTruePeakDbtp;
        if (measured.BlackIntervals.IsEmpty && measured.FrozenIntervals.IsEmpty && !audioPeakExceeded)
        {
            string? reportError = await PublishObjectiveReportAsync(
                job,
                revision,
                render,
                measured,
                "accepted",
                null,
                remountNumber,
                cancellationToken);
            if (reportError is not null)
            {
                return StageExecutionResult.Permanent("critic.report_rejected", reportError);
            }
            await SaveAttemptAsync(
                CreateCompletedAttempt(job, revision, render, remountNumber, startedAt, "accepted", null, null, null),
                cancellationToken);
            return StageExecutionResult.Success("critic.accepted", "No objective editing defects were found.");
        }

        if (analysisSource is null)
        {
            return StageExecutionResult.Permanent(
                "critic.analysis_source_unavailable",
                "The render contains an objective defect, but its analysis is unavailable.");
        }

        if (remountNumber >= maxRemounts)
        {
            return StageExecutionResult.Permanent(
                "critic.remount_limit_reached",
                "The objective defect remains after the allowed number of remounts.");
        }

        MultiSourceAnalysisResult? analysis = await analysisSource.ReadAsync(
            job.JobId,
            cancellationToken);
        if (analysis is null || analysis.RevisionId != revision.Plan.AnalysisRevisionId)
        {
            return StageExecutionResult.Permanent(
                "critic.analysis_invalid",
                "The approved analysis required for correction is unavailable.");
        }

        ObjectiveCritiqueResult critique = new ReelsEditingCritic().Critique(
            new ObjectiveCritiqueRequest(revision.Plan, analysis, measured.BlackIntervals)
            {
                FrozenIntervals = measured.FrozenIntervals,
                MeasuredTruePeakDbtp = measured.TruePeakDbtp,
                MaximumTruePeakDbtp = maximumTruePeakDbtp
            });
        if (critique.Decision != CriticDecision.RemountRequired
            || critique.Corrections.FirstOrDefault() is not CorrectionProposal correction)
        {
            return StageExecutionResult.Permanent(
                "critic.objective_defect_unrepairable",
                "The render contains an objective defect without a supported correction.");
        }

        EditPlanCorrectionResult correctionResult = new EditPlanCorrectionService().Apply(
            revision.Plan,
            analysis,
            correction);
        if (correctionResult is not EditPlanCorrectionResult.Applied applied)
        {
            EditPlanCorrectionResult.Rejected rejected = (EditPlanCorrectionResult.Rejected)correctionResult;
            return StageExecutionResult.Permanent(rejected.ErrorCode, rejected.ErrorMessage);
        }

        EditPlan correctedPlan = applied.Plan with
        {
            RevisionId = EditPlanRevisionIdentity.CalculateRevisionId(
                applied.Plan,
                revision.Presentation,
                revision.Audit)
        };
        string planHash = EditPlanRevisionIdentity.CalculateHash(
            correctedPlan,
            revision.Presentation,
            revision.Audit);
        string? correctionReportError = await PublishObjectiveReportAsync(
            job,
            revision,
            render,
            measured,
            "remount",
            correctedPlan.RevisionId,
            remountNumber,
            cancellationToken);
        if (correctionReportError is not null)
        {
            return StageExecutionResult.Permanent("critic.report_rejected", correctionReportError);
        }
        EditPlanRevision correctedRevision = await planStore.SaveAsync(
            correctedPlan,
            planHash,
            revision.ActualCostUsd,
            revision.Presentation,
            revision.Audit,
            cancellationToken);
        await SaveAttemptAsync(
            CreateCompletedAttempt(
                job,
                revision,
                render,
                remountNumber,
                startedAt,
                "remount",
                correctedRevision.RevisionId,
                null,
                null),
            cancellationToken);
        return StageExecutionResult.Success(
            "critic.remount_required",
            "An objective defect produced a new corrected plan revision.",
            StageCompletionAction.Remount);
    }

    private static bool TryReadRenderIdentity(
        ArtifactRecord render,
        VariantId expectedVariantId,
        out RevisionId revisionId,
        out string? planHash)
    {
        revisionId = default;
        planHash = null;
        if (string.IsNullOrWhiteSpace(render.MetadataJson)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(render.MetadataJson);
            JsonElement root = document.RootElement;
            if (!MatchesId(root, "variantId", expectedVariantId.Value)
                || !root.TryGetProperty("planRevisionId", out JsonElement storedRevision)
                || storedRevision.ValueKind != JsonValueKind.String
                || !Guid.TryParse(storedRevision.GetString(), out Guid revisionGuid)
                || !root.TryGetProperty("planHash", out JsonElement storedHash)
                || storedHash.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            revisionId = new RevisionId(revisionGuid);
            planHash = storedHash.GetString();
            return !string.IsNullOrWhiteSpace(planHash);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private Task SaveAttemptAsync(CritiqueAttemptRecord attempt, CancellationToken cancellationToken) =>
        attemptStore?.SaveAsync(attempt, cancellationToken) ?? Task.CompletedTask;

    private async Task<bool> TryRecoverCompletedRemountAsync(
        JobCreationRecord job,
        ArtifactRecord render,
        CritiqueAttemptRecord runningAttempt,
        CancellationToken cancellationToken)
    {
        if (attemptStore is null || job.CurrentApprovedEditPlanRevisionId is not RevisionId approvedRevisionId)
            return false;
        ArtifactRecord? report = await artifactStore.FindLatestAsync(
            job.JobId,
            job.VariantId,
            "objective-critique-report",
            cancellationToken);
        if (report?.ParentArtifactId != render.ArtifactId || string.IsNullOrWhiteSpace(report.MetadataJson))
            return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(report.MetadataJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int schemaVersion)
                || schemaVersion != 2
                || root.GetProperty("sourceRevisionId").GetString() != runningAttempt.SourceRevisionId.ToString()
                || root.GetProperty("renderArtifactId").GetString() != render.ArtifactId.ToString()
                || !root.TryGetProperty("remountNumber", out JsonElement remount)
                || remount.ValueKind != JsonValueKind.Number
                || !remount.TryGetInt32(out int remountNumber)
                || remountNumber != runningAttempt.RemountNumber
                || root.GetProperty("decision").GetString() != "remount"
                || !Guid.TryParse(root.GetProperty("correctionRevisionId").GetString(), out Guid correctionGuid)
                || new RevisionId(correctionGuid) != approvedRevisionId)
                return false;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return false;
        }

        EditPlanRevision? corrected = await planStore.GetAsync(approvedRevisionId, cancellationToken);
        if (corrected is null || corrected.JobId != job.JobId || corrected.VariantId != job.VariantId)
            return false;
        await attemptStore.SaveAsync(
            new CritiqueAttemptRecord(
                runningAttempt.JobId,
                runningAttempt.VariantId,
                runningAttempt.SourceRevisionId,
                runningAttempt.RenderArtifactId,
                approvedRevisionId,
                runningAttempt.RemountNumber,
                runningAttempt.EstimatedCostUsd,
                0m,
                "remount",
                null,
                null,
                runningAttempt.CreatedAt,
                DateTimeOffset.UtcNow),
            cancellationToken);
        return true;
    }

    private static CritiqueAttemptRecord CreateCompletedAttempt(
        JobCreationRecord job,
        EditPlanRevision revision,
        ArtifactRecord render,
        int remountNumber,
        DateTimeOffset startedAt,
        string status,
        RevisionId? correctionRevisionId,
        string? errorCode,
        string? errorMessage) =>
        new(
            job.JobId,
            job.VariantId,
            revision.RevisionId,
            render.ArtifactId,
            correctionRevisionId,
            remountNumber,
            0m,
            0m,
            status,
            errorCode,
            errorMessage,
            startedAt,
            DateTimeOffset.UtcNow);

    private static bool MatchesId(JsonElement parent, string name, Guid expected) =>
        parent.TryGetProperty(name, out JsonElement id)
        && id.ValueKind == JsonValueKind.Object
        && id.TryGetProperty("value", out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out Guid actual)
        && actual == expected;

    private async Task<string?> PublishObjectiveReportAsync(
        JobCreationRecord job,
        EditPlanRevision revision,
        ArtifactRecord render,
        RenderedMediaObservation observation,
        string decision,
        RevisionId? correctionRevisionId,
        int remountNumber,
        CancellationToken cancellationToken)
    {
        ArtifactRecord? existing = await artifactStore.FindLatestAsync(
            job.JobId,
            job.VariantId,
            "objective-critique-report",
            cancellationToken);
        if (existing?.ParentArtifactId == render.ArtifactId
            && File.Exists(SafeFileTree.Resolve(workspaceRoot, existing.RelativePath)))
        {
            return ExistingReportMatches(
                existing,
                revision,
                render,
                decision,
                correctionRevisionId,
                remountNumber)
                ? null
                : "An immutable objective critique report already exists with different identity.";
        }

        ArtifactId reportId = ArtifactId.New();
        string renderPath = SafeFileTree.Resolve(workspaceRoot, render.RelativePath);
        string reportPath = Path.Combine(
            Path.GetDirectoryName(renderPath)!,
            $"objective-critique-{reportId}.json");
        List<object> findings =
        [
            .. observation.BlackIntervals.Select(interval => (object)new
            {
                code = "critic.black_frames",
                kind = "blackFrames",
                output = new { interval.StartFrame, interval.EndFrame }
            }),
            .. observation.FrozenIntervals.Select(interval => (object)new
            {
                code = "critic.frozen_frames",
                kind = "frozenFrames",
                output = new { interval.StartFrame, interval.EndFrame }
            })
        ];
        if (observation.TruePeakDbtp is decimal measuredPeak && measuredPeak > maximumTruePeakDbtp)
        {
            findings.Add(new
            {
                code = "critic.audio_true_peak",
                kind = "audioTruePeak",
                measuredTruePeakDbtp = measuredPeak,
                maximumTruePeakDbtp
            });
        }

        string reportJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            jobId = job.JobId.ToString(),
            variantId = job.VariantId.ToString(),
            sourceRevisionId = revision.RevisionId.ToString(),
            sourcePlanHash = revision.PlanHash,
            correctionRevisionId = correctionRevisionId?.ToString(),
            renderArtifactId = render.ArtifactId.ToString(),
            remountNumber,
            decision,
            truePeakDbtp = observation.TruePeakDbtp,
            maximumTruePeakDbtp,
            findings
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

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reportJson)));
        ArtifactRecord report = new(
            reportId,
            "objective-critique-report",
            Path.GetRelativePath(workspaceRoot, reportPath).Replace(Path.DirectorySeparatorChar, '/'),
            hash,
            new FileInfo(reportPath).Length,
            reportJson,
            render.ArtifactId,
            "reels-objective-critic/1",
            job.JobId,
            job.VariantId);
        ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(report, cancellationToken);
        return registration is ArtifactRegistrationResult.Rejected rejected ? rejected.Message : null;
    }

    private static bool ExistingReportMatches(
        ArtifactRecord report,
        EditPlanRevision revision,
        ArtifactRecord render,
        string decision,
        RevisionId? correctionRevisionId,
        int remountNumber)
    {
        if (string.IsNullOrWhiteSpace(report.MetadataJson)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(report.MetadataJson);
            JsonElement root = document.RootElement;
            return root.TryGetProperty("schemaVersion", out JsonElement version)
                && version.ValueKind == JsonValueKind.Number
                && version.TryGetInt32(out int schemaVersion)
                && schemaVersion == 2
                && root.GetProperty("sourceRevisionId").GetString() == revision.RevisionId.ToString()
                && root.GetProperty("sourcePlanHash").GetString() == revision.PlanHash
                && root.GetProperty("renderArtifactId").GetString() == render.ArtifactId.ToString()
                && root.GetProperty("decision").GetString() == decision
                && root.TryGetProperty("remountNumber", out JsonElement storedRemount)
                && storedRemount.ValueKind == JsonValueKind.Number
                && storedRemount.TryGetInt32(out int storedRemountNumber)
                && storedRemountNumber == remountNumber
                && root.GetProperty("correctionRevisionId").GetString() == correctionRevisionId?.ToString();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return false;
        }
    }
}
