using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Domain.Validation;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Application.Critique;

namespace VideoFactory.VideoProduction.Application.Planning;

/// <summary>Преобразует комментарий и Analysis Result в один проверенный Edit Plan.</summary>
public sealed class EditPlanPlanner
{
    private readonly IEditPlanProvider provider;
    private readonly IPlanningCostPolicy costPolicy;
    private readonly IEditPlanRevisionStore revisionStore;
    private readonly IPlanningAttemptStore attemptStore;
    private readonly IPlanningApprovalStore? approvalStore;
    private readonly EditingSkillEngine editingSkillEngine;
    private readonly VariantDiversityEvaluator diversityEvaluator;

    /// <summary>Создаёт planner.</summary>
    public EditPlanPlanner(
        IEditPlanProvider provider,
        IPlanningCostPolicy costPolicy,
        IEditPlanRevisionStore revisionStore,
        IPlanningAttemptStore attemptStore,
        IPlanningApprovalStore? approvalStore = null,
        EditingSkillEngine? editingSkillEngine = null,
        VariantDiversityEvaluator? diversityEvaluator = null)
    {
        this.provider = provider;
        this.costPolicy = costPolicy;
        this.revisionStore = revisionStore;
        this.attemptStore = attemptStore;
        this.approvalStore = approvalStore;
        this.diversityEvaluator = diversityEvaluator ?? new VariantDiversityEvaluator();
        this.editingSkillEngine = editingSkillEngine ?? new EditingSkillEngine(
        [
            new MultiClipDirectorSkill(),
            new SemanticStorySkill(),
            new HookBuilderSkill(),
            new SmartCutSkill(),
            new BestShotSelectorSkill(),
            new PacingDirectorSkill(),
            new SpeechTighteningSkill(),
            new CaptionDirectorSkill(),
            new VisualDirectorSkill(),
            new SmartReframeSkill(),
            new AudioDirectorSkill()
        ]);
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

        EditPlanRevision? parentApproved = await revisionStore.FindParentApprovedAsync(
            request.JobId,
            request.VariantId,
            cancellationToken);

        ReelsStyleConfiguration style = new ReelsProfileCatalog().Resolve(
            ToReelsStyle(request.Settings.RequestedStyle), request.Analysis);
        EditingEvaluation editingEvaluation = editingSkillEngine.Evaluate(
            new EditingContext(
                request.Analysis,
                request.Settings,
                request.Comment,
                KnownAssetIds: request.Assets.Keys.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal),
                Style: style,
                MultiSourceAnalysis: request.MultiSourceAnalysis),
            new EditingSkillConfiguration(
            [
                new("multi-clip-director", true),
                new("semantic-story", true),
                new("hook-builder", true),
                new("smart-cut", true),
                new("best-shot", true),
                new("pacing-director", true),
                new("speech-tightening", true),
                new("caption-director", true),
                new("visual-director", true),
                new("smart-reframe", true),
                new("audio-director", true)
            ]));
        ReelsPresentationPlan presentation = ReelsPresentationPlan.FromEvaluation(style, editingEvaluation);
        EditPlanAudit audit = EditPlanAudit.FromEvaluation(editingEvaluation);
        ProviderPlanningRequest providerRequest = CreateProviderRequest(request, editingEvaluation);
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
                AnalysisResult effectiveAnalysis = ApplyEditingEvaluation(request.Analysis, editingEvaluation);
                EditPlan plan = CreatePlan(request, response, effectiveAnalysis, editingEvaluation);
                plan = plan with
                {
                    RevisionId = EditPlanRevisionIdentity.CalculateRevisionId(plan, presentation, audit)
                };
                ImmutableArray<ValidationIssue> issues = request.MultiSourceAnalysis is null
                    ? EditPlanValidator.Validate(plan, effectiveAnalysis, request.Assets, request.Templates)
                    : EditPlanValidator.Validate(
                        plan,
                        request.MultiSourceAnalysis with { AllowedSegments = effectiveAnalysis.AllowedSegments },
                        request.Assets,
                        request.Templates);

                if (!issues.IsEmpty)
                {
                    throw new PlanConstructionException(
                        "planning.plan_validation_failed",
                        string.Join("; ", issues.Select(issue =>
                            $"{issue.Code} at {issue.Path}")));
                }

                if (parentApproved is not null)
                {
                    VariantDiversityResult diversity = diversityEvaluator.Evaluate(plan, parentApproved.Plan);
                    if (!diversity.IsAccepted)
                    {
                        throw new PlanConstructionException(
                            "planning.variant_not_diverse",
                            $"Alternative variant differs on {diversity.DifferenceCount} objective axes; " +
                            $"at least {diversity.RequiredDifferenceCount} are required.");
                    }
                }

                string planHash = EditPlanRevisionIdentity.CalculateHash(plan, presentation, audit);
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
                    presentation,
                    audit,
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

    private static ReelsStyle ToReelsStyle(VideoFactory.VideoProduction.Domain.Jobs.RequestedReelsStyle style) => style switch
    {
        VideoFactory.VideoProduction.Domain.Jobs.RequestedReelsStyle.Auto => ReelsStyle.Auto,
        VideoFactory.VideoProduction.Domain.Jobs.RequestedReelsStyle.ExpertClean => ReelsStyle.ExpertClean,
        VideoFactory.VideoProduction.Domain.Jobs.RequestedReelsStyle.DynamicViral => ReelsStyle.DynamicViral,
        VideoFactory.VideoProduction.Domain.Jobs.RequestedReelsStyle.Cinematic => ReelsStyle.Cinematic,
        VideoFactory.VideoProduction.Domain.Jobs.RequestedReelsStyle.PremiumAds => ReelsStyle.PremiumAds,
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };

    private static ProviderPlanningRequest CreateProviderRequest(
        PlanningRequest request,
        EditingEvaluation editingEvaluation)
    {
        AnalysisResult effectiveAnalysis = ApplyEditingEvaluation(request.Analysis, editingEvaluation);
        MultiClipDecision? multiClip = editingEvaluation.Decisions
            .OfType<MultiClipDecision>()
            .FirstOrDefault(decision => decision.Status == EditingDecisionStatus.Applied);
        HashSet<SegmentId>? selectedSegments = multiClip?.Selections
            .Select(selection => selection.SegmentId)
            .ToHashSet();
        return new(
            request.Comment,
            request.Settings.Tempo,
            request.Settings.TargetDurationSeconds
                ?? (int)Math.Round(
                    new ReelsProfileCatalog().SelectSemanticDurationMicroseconds(effectiveAnalysis) / 1_000_000m,
                    MidpointRounding.AwayFromZero),
            request.Settings.CaptionsEnabled,
            effectiveAnalysis.AllowedSegments
                .Where(segment => request.Analysis.SchemaVersion < 2 || segment.IsApproved)
                .Where(segment => selectedSegments is null || selectedSegments.Contains(segment.Id))
                .Select(segment => new ProviderSegment(
                    segment.Id,
                    segment.Mapping.Master.DurationMicroseconds))
                .ToImmutableArray(),
            string.Join(
                Environment.NewLine,
                request.Analysis.Speech.Select(segment => segment.Text)),
            0,
            editingEvaluation);
    }

    private static AnalysisResult ApplyEditingEvaluation(
        AnalysisResult analysis,
        EditingEvaluation evaluation)
    {
        Dictionary<SegmentId, SourceMapping> mappings = evaluation.Decisions
            .OfType<SmartCutDecision>()
            .SelectMany(decision => decision.Edits)
            .ToDictionary(edit => edit.SegmentId, edit => edit.Mapping);
        if (mappings.Count == 0)
        {
            return analysis;
        }

        return analysis with
        {
            AllowedSegments = analysis.AllowedSegments
                .Select(segment => mappings.TryGetValue(segment.Id, out SourceMapping? mapping)
                    ? segment with { Mapping = mapping }
                    : segment)
                .ToImmutableArray()
        };
    }

    private static EditPlan CreatePlan(
        PlanningRequest request,
        ProviderPlanResponse response,
        AnalysisResult analysis,
        EditingEvaluation editingEvaluation)
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

        Dictionary<SegmentId, AllowedSegment> segments = analysis.AllowedSegments
            .Where(segment => analysis.SchemaVersion < 2 || segment.IsApproved)
            .ToDictionary(segment => segment.Id);
        Dictionary<SegmentId, OutputRange> pacingOutputs = editingEvaluation.Decisions
            .OfType<PacingDecision>()
            .Where(decision => decision.Status == EditingDecisionStatus.Applied)
            .SelectMany(decision => decision.Shots)
            .ToDictionary(shot => shot.SegmentId, shot => shot.Output);
        ImmutableArray<SegmentId> requiredSegments = editingEvaluation.Decisions
            .OfType<MultiClipDecision>()
            .Where(decision => decision.Status == EditingDecisionStatus.Applied)
            .SelectMany(decision => decision.Selections)
            .Select(selection => selection.SegmentId)
            .ToImmutableArray();
        if (requiredSegments.Any(required => response.VideoClips.All(clip => clip.SegmentId != required)))
        {
            throw new PlanConstructionException(
                "planning.required_segment_missing",
                "Provider omitted a segment selected by the multi-clip director.");
        }
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
                pacingOutputs.TryGetValue(segment.Id, out OutputRange pacedOutput)
                    ? pacedOutput
                    : providerClip.Output,
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
            request.MultiSourceAnalysis is null ? 1 : 3,
            request.JobId,
            request.VariantId,
            new RevisionId(new Guid("00000000-0000-0000-0000-000000000001")),
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
            new OverlayTrack(ImmutableArray<Overlay>.Empty))
        {
            Sources = request.MultiSourceAnalysis is null
                ? []
                : [.. clips
                    .Select(clip => clip.Mapping)
                    .DistinctBy(mapping => (mapping.OriginalAssetId, mapping.MasterAssetId))
                    .Select(mapping => new EditPlanSource(
                        mapping.OriginalAssetId,
                        mapping.MasterAssetId,
                        request.Assets[mapping.MasterAssetId].Metadata.DurationMicroseconds))]
        };
    }

}
