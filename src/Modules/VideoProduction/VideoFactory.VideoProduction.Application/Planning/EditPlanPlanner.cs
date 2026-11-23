using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Domain.Validation;

namespace VideoFactory.VideoProduction.Application.Planning;

/// <summary>Преобразует комментарий и Analysis Result в один проверенный Edit Plan.</summary>
public sealed class EditPlanPlanner
{
    private readonly IEditPlanProvider provider;
    private readonly IPlanningCostPolicy costPolicy;
    private readonly IEditPlanRevisionStore revisionStore;
    private readonly IPlanningAttemptStore attemptStore;
    private readonly IPlanningApprovalStore? approvalStore;

    /// <summary>Создаёт planner.</summary>
    public EditPlanPlanner(
        IEditPlanProvider provider,
        IPlanningCostPolicy costPolicy,
        IEditPlanRevisionStore revisionStore,
        IPlanningAttemptStore attemptStore,
        IPlanningApprovalStore? approvalStore = null)
    {
        this.provider = provider;
        this.costPolicy = costPolicy;
        this.revisionStore = revisionStore;
        this.attemptStore = attemptStore;
        this.approvalStore = approvalStore;
    }

    /// <summary>Строит план с максимум одной попыткой исправления ответа provider.</summary>
    public async Task<PlanningResult> PlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken)
    {
        try { return await PlanCoreAsync(request, cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
        {
            return new PlanningResult.Rejected(exception.GetType().Name, exception.Message);
        }
    }

    private async Task<PlanningResult> PlanCoreAsync(PlanningRequest request, CancellationToken cancellationToken)
    {
        PlanningResult.Rejected? inputError = ValidateInput(request);
        if (inputError is not null)
        {
            return inputError;
        }

        EditPlanRevision? approved = await revisionStore.FindApprovedAsync(
            request.JobId,
            request.VariantId,
            cancellationToken);
        if (approved is not null)
        {
            return new PlanningResult.Existing(approved);
        }

        ProviderPlanningRequest providerRequest = CreateProviderRequest(request);
        decimal accumulatedActualCostUsd = 0m;
        PlanConstructionException? lastError = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ProviderPlanningRequest attemptRequest = providerRequest with { RepairAttempt = attempt };
            PlanningCostEstimate estimate = await provider.EstimateCostAsync(
                attemptRequest,
                cancellationToken);
            PlanningCostDecision decision = costPolicy.Decide(estimate, accumulatedActualCostUsd);

            PlanningApproval? approval = approvalStore is null
                ? null
                : await approvalStore.FindAsync(request.JobId, request.VariantId, cancellationToken);

            bool approvedForThisAttempt = approval is not null
                && approval.Status == PlanningApprovalStatus.Approved
                && approval.TelegramUserId == request.TelegramUserId
                && approval.Version == attempt + 1
                && approval.EstimatedCostUsd == estimate.EstimatedCostUsd;

            if (approval is { Status: PlanningApprovalStatus.Rejected })
            {
                return new PlanningResult.Rejected(
                    "planning.cost_approval_denied",
                    "The user rejected the additional planning cost.");
            }

            if (decision.RequiresApproval && !approvedForThisAttempt)
            {
                int? approvalVersion = null;
                if (approvalStore is not null && request.TelegramUserId is long userId)
                {
                    PlanningApproval pending = await approvalStore.SavePendingAsync(
                        request.JobId,
                        request.VariantId,
                        userId,
                        estimate.EstimatedCostUsd,
                        accumulatedActualCostUsd,
                        request.JobLimitUsd,
                        cancellationToken);
                    approvalVersion = pending.Version;
                }

                return new PlanningResult.WaitingForApproval(
                    estimate.EstimatedCostUsd,
                    decision.Message ?? "Planning requires cost approval.",
                    approvalVersion);
            }

            if (!decision.IsAllowed)
            {
                return new PlanningResult.Rejected(
                    "planning.cost_limit_exceeded",
                    decision.Message ?? "Planning cost exceeds the job limit.");
            }

            ProviderPlanResponse response;
            try
            {
                response = await provider.CreatePlanAsync(attemptRequest, cancellationToken);
            }
            catch (PlanningProviderException exception)
            {
                await attemptStore.SaveAsync(
                    CreateAttemptRecord(request, attempt, estimate, exception.ActualCostUsd, "failed", exception),
                    cancellationToken);
                throw;
            }

            accumulatedActualCostUsd += response.ActualCostUsd;

            try
            {
                EditPlan plan = CreatePlan(request, response);
                ImmutableArray<ValidationIssue> issues = EditPlanValidator.Validate(
                    plan,
                    request.Analysis,
                    request.Assets,
                    request.Templates);

                if (!issues.IsEmpty)
                {
                    throw new PlanConstructionException(
                        "planning.plan_validation_failed",
                        string.Join("; ", issues.Select(issue =>
                            $"{issue.Code} at {issue.Path}")));
                }

                string planHash = CalculateHash(plan);
                EditPlanRevision? existing = await revisionStore.FindAsync(
                    request.JobId,
                    request.VariantId,
                    planHash,
                    cancellationToken);

                if (existing is not null)
                {
                    await attemptStore.SaveAsync(
                        CreateAttemptRecord(request, attempt, estimate, response.ActualCostUsd, "existing", null),
                        cancellationToken);
                    return new PlanningResult.Existing(existing);
                }

                EditPlanRevision revision = await revisionStore.SaveAsync(
                    plan,
                    planHash,
                    accumulatedActualCostUsd,
                    cancellationToken);

                await attemptStore.SaveAsync(
                    CreateAttemptRecord(request, attempt, estimate, response.ActualCostUsd, "succeeded", null),
                    cancellationToken);

                return new PlanningResult.Created(revision);
            }
            catch (PlanConstructionException exception)
            {
                await attemptStore.SaveAsync(
                    CreateAttemptRecord(request, attempt, estimate, response.ActualCostUsd, "invalid", exception),
                    cancellationToken);
                lastError = exception;
            }
        }

        return new PlanningResult.Rejected(
            lastError?.ErrorCode ?? "planning.provider_response_invalid",
            lastError?.Message ?? "Provider returned an invalid plan.");
    }

    private static PlanningAttemptRecord CreateAttemptRecord(
        PlanningRequest request,
        int attempt,
        PlanningCostEstimate estimate,
        decimal? actualCostUsd,
        string status,
        Exception? exception) =>
        new(
            Guid.NewGuid(),
            request.JobId,
            request.VariantId,
            attempt + 1,
            attempt,
            estimate.EstimatedCostUsd,
            actualCostUsd,
            status,
            exception is PlanConstructionException construction ? construction.ErrorCode : exception?.GetType().Name,
            exception?.Message,
            DateTimeOffset.UtcNow);

    private static PlanningResult.Rejected? ValidateInput(PlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.JobId.IsEmpty || request.VariantId.IsEmpty)
        {
            return new PlanningResult.Rejected(
                "planning.identity_invalid",
                "Job and variant identifiers are required.");
        }

        if (string.IsNullOrWhiteSpace(request.Comment))
        {
            return new PlanningResult.Rejected(
                "planning.comment_empty",
                "A non-empty user comment is required.");
        }

        if (request.Settings.MusicEnabled)
        {
            return new PlanningResult.Rejected(
                "planning.music_unsupported",
                "Music is not supported by the MVP planning flow.");
        }

        if (request.Settings.TargetDurationSeconds is <= 0)
        {
            return new PlanningResult.Rejected(
                "planning.duration_invalid",
                "Target duration must be positive when specified.");
        }

        return null;
    }

    private static ProviderPlanningRequest CreateProviderRequest(PlanningRequest request) =>
        new(
            request.Comment,
            request.Settings.Tempo,
            request.Settings.TargetDurationSeconds,
            request.Settings.CaptionsEnabled,
            request.Analysis.AllowedSegments
                .Where(segment => request.Analysis.SchemaVersion < 2 || segment.IsApproved)
                .Select(segment => new ProviderSegment(
                    segment.Id,
                    segment.Mapping.Master.DurationMicroseconds))
                .ToImmutableArray(),
            string.Join(
                Environment.NewLine,
                request.Analysis.Speech.Select(segment => segment.Text)));

    private static EditPlan CreatePlan(
        PlanningRequest request,
        ProviderPlanResponse response)
    {
        if (response.SchemaVersion != 1)
        {
            throw new PlanConstructionException(
                "planning.schema_version_unsupported",
                "Provider returned an unsupported plan schema version.");
        }

        if (!request.Templates.TryGetValue(
                TemplateId.Dynamic,
                out TemplateDefinition? template))
        {
            throw new PlanConstructionException(
                "planning.template_missing",
                "The fixed MVP template is not available.");
        }

        Dictionary<SegmentId, AllowedSegment> segments = request.Analysis.AllowedSegments
            .Where(segment => request.Analysis.SchemaVersion < 2 || segment.IsApproved)
            .ToDictionary(segment => segment.Id);
        List<VideoClip> clips = [];
        foreach (ProviderVideoClip providerClip in response.VideoClips)
        {
            if (!segments.TryGetValue(providerClip.SegmentId, out AllowedSegment? segment))
            {
                throw new PlanConstructionException(
                    "planning.segment_unknown",
                    $"Provider referred to unknown segment '{providerClip.SegmentId}'.");
            }

            clips.Add(new VideoClip(
                segment.Id,
                segment.Mapping,
                providerClip.Output,
                providerClip.Crop));
        }

        if (clips.Count == 0)
        {
            throw new PlanConstructionException(
                "planning.video_empty",
                "Provider returned no video clips.");
        }

        int durationFrames = clips.Max(clip => clip.Output.EndFrame);
        if (request.Settings.TargetDurationSeconds is int targetDuration)
        {
            int expectedFrames = checked(targetDuration * 30);
            if (durationFrames != expectedFrames)
            {
                throw new PlanConstructionException(
                    "planning.duration_mismatch",
                    "Provider output does not match the requested duration.");
            }
        }

        ImmutableArray<Caption> captions = response.Captions
            .Select(caption => new Caption(caption.Text, caption.Output))
            .ToImmutableArray();
        if (!request.Settings.CaptionsEnabled && !captions.IsEmpty)
        {
            throw new PlanConstructionException(
                "planning.captions_disabled",
                "Provider returned captions while captions are disabled.");
        }

        return new EditPlan(
            1,
            request.JobId,
            request.VariantId,
            CreateRevisionId(request, response),
            request.Analysis.RevisionId,
            response.Seed,
            new OutputProfile(1080, 1920, new FrameRate(30, 1), durationFrames),
            TemplateId.Dynamic,
            new TemplateProps(
                request.Settings.CaptionsEnabled
                    ? CaptionPreset.Emphasis
                    : CaptionPreset.None,
                ColorTheme.Dark,
                null),
            new VideoTrack(clips.ToImmutableArray()),
            new AudioTrack(
                clips.Select(clip => new SourceAudioClip(
                    clip.SegmentId,
                    clip.Output,
                    1m)).ToImmutableArray(),
                ImmutableArray<MusicClip>.Empty),
            new CaptionTrack(captions),
            new OverlayTrack(ImmutableArray<Overlay>.Empty));
    }

    private static RevisionId CreateRevisionId(
        PlanningRequest request,
        ProviderPlanResponse response)
    {
        string content = string.Join(
            "|",
            request.JobId,
            request.VariantId,
            request.Analysis.RevisionId,
            response.SchemaVersion,
            response.Seed,
            string.Join(",", response.VideoClips),
            string.Join(",", response.Captions));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return new RevisionId(new Guid(hash[..16]));
    }

    private static string CalculateHash(EditPlan plan)
    {
        string canonical = JsonSerializer.Serialize(plan);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
