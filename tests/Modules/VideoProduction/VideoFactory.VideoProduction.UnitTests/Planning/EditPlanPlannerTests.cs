using System.Collections.Immutable;
using System.Net;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Planning;

public sealed class EditPlanPlannerTests
{
    [Fact]
    public async Task MultiSourceAnalysisCreatesV3PlanWithDeclaredSources()
    {
        ValidPlanFixture source = ValidPlanFixture.Create();
        AllowedSegment first = source.Analysis.AllowedSegments[0] with { IsApproved = true };
        AllowedSegment legacySecond = source.Analysis.AllowedSegments[1] with { IsApproved = true };
        ArtifactId secondOriginalId = ArtifactId.New();
        ArtifactId secondMasterId = ArtifactId.New();
        AllowedSegment second = legacySecond with
        {
            Mapping = new SourceMapping(
                secondOriginalId,
                new OriginalRange(0, 3_000_000),
                secondMasterId,
                new MasterRange(0, 3_000_000))
        };
        AnalysisResult compatibilityAnalysis = source.Analysis with { AllowedSegments = [first, second] };
        MultiSourceAnalysisResult multiSourceAnalysis = new(
            3,
            source.Analysis.RevisionId,
            [
                new AnalysisSource(source.Analysis.OriginalAssetId, source.Analysis.MasterAssetId, [], [], [new Scene(first.Mapping.Master)], AnalysisFeatureSet.Empty),
                new AnalysisSource(secondOriginalId, secondMasterId, [], [], [new Scene(second.Mapping.Master)], AnalysisFeatureSet.Empty)
            ],
            [first, second]);
        Dictionary<ArtifactId, MediaAsset> assets = source.Assets.ToDictionary();
        MediaMetadata secondMetadata = new(
            "mp4",
            3_000_000,
            [new VideoStreamMetadata(0, "h264", 1080, 1920, new FrameRate(30, 1), false, 0)],
            [new AudioStreamMetadata(1, "aac", 48_000, 2)]);
        assets[secondOriginalId] = new MediaAsset(secondOriginalId, secondMetadata);
        assets[secondMasterId] = new MediaAsset(secondMasterId, secondMetadata);
        EditPlan providerPlan = source.Plan with
        {
            Video = new VideoTrack([
                source.Plan.Video.Clips[0],
                source.Plan.Video.Clips[1] with { Mapping = second.Mapping }
            ])
        };
        TestProvider provider = new(providerPlan);
        InMemoryRevisionStore store = new();
        EditPlanPlanner planner = new(provider, new TestCostPolicy(1m), store, new InMemoryAttemptStore());
        PlanningRequest request = new(
            source.Plan.JobId,
            source.Plan.VariantId,
            compatibilityAnalysis,
            "Собери два источника",
            new ProductionSettings(6, true, false),
            assets,
            source.Templates,
            MultiSourceAnalysis: multiSourceAnalysis);

        PlanningResult.Created created = Assert.IsType<PlanningResult.Created>(
            await planner.PlanAsync(request, CancellationToken.None));

        Assert.Equal(3, created.Revision.Plan.SchemaVersion);
        Assert.Equal([source.Analysis.MasterAssetId, secondMasterId], created.Revision.Plan.Sources.Select(item => item.MasterAssetId));
        Assert.Equal([source.Analysis.MasterAssetId, secondMasterId], created.Revision.Plan.Video.Clips.Select(item => item.Mapping.MasterAssetId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProviderNetworkFailureDoesNotSavePlanOrTriggerRepair(bool duringEstimate)
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.NetworkFailure = duringEstimate ? "estimate" : "create";
        PlanningResult.Rejected rejected = Assert.IsType<PlanningResult.Rejected>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));
        Assert.Equal("HttpRequestException", rejected.ErrorCode);
        Assert.Equal("original integration diagnostic", rejected.ErrorMessage);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(duringEstimate ? 0 : 1, fixture.Provider.CreatePlanCount);
        Assert.Equal(duringEstimate ? 0 : 1, fixture.Attempts.Attempts.Count);
        if (!duringEstimate) Assert.Null(fixture.Attempts.Attempts[0].ActualCostUsd);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task TransientProviderHttpFailureEscapesForStageRetry(HttpStatusCode statusCode)
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.NetworkStatusCode = statusCode;

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(1, fixture.Provider.CreatePlanCount);
        Assert.Single(fixture.Attempts.Attempts);
        Assert.Null(fixture.Attempts.Attempts[0].ActualCostUsd);
    }

    [Fact]
    public async Task ProviderTimeoutEscapesForStageRetry()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.Timeout = true;

        TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(
            () => fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(1, fixture.Provider.CreatePlanCount);
        Assert.Single(fixture.Attempts.Attempts);
        Assert.Null(fixture.Attempts.Attempts[0].ActualCostUsd);
    }

    [Fact]
    public async Task CancellationAfterProviderRequestRecordsUnknownCost()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.Cancel = true;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Planner.PlanAsync(fixture.Request, cancellation.Token));

        Assert.Single(fixture.Attempts.Attempts);
        Assert.Null(fixture.Attempts.Attempts[0].ActualCostUsd);
    }

    [Fact]
    public async Task CancellationAfterPaidResponseRecordsActualCost()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        using CancellationTokenSource cancellation = new();
        fixture.Provider.CancellationToTrigger = cancellation;
        fixture.Store.CancelOnFind = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Planner.PlanAsync(fixture.Request, cancellation.Token));

        PlanningAttemptRecord attempt = Assert.Single(fixture.Attempts.Attempts);
        Assert.Equal(fixture.Provider.ActualCostUsd, attempt.ActualCostUsd);
    }

    [Fact]
    public async Task SameInputCreatesOneValidDeterministicPlanFromKnownSegments()
    {
        PlanningFixture fixture = PlanningFixture.Create();

        PlanningResult first = await fixture.Planner.PlanAsync(
            fixture.Request,
            CancellationToken.None);
        PlanningResult second = await fixture.Planner.PlanAsync(
            fixture.Request,
            CancellationToken.None);

        PlanningResult.Created created = Assert.IsType<PlanningResult.Created>(first);
        PlanningResult.Existing existing = Assert.IsType<PlanningResult.Existing>(second);
        Assert.Equal(created.Revision.Plan, existing.Revision.Plan);
        Assert.Equal(created.Revision.PlanHash, existing.Revision.PlanHash);
        Assert.Empty(created.Revision.Plan.Audio.MusicClips);
        Assert.All(
            created.Revision.Plan.Video.Clips,
            clip => Assert.Contains(
                fixture.Analysis.AllowedSegments,
                segment => segment.Id == clip.SegmentId));
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal(1, fixture.Provider.EstimateCostCount);
        Assert.Equal(1, fixture.Provider.CreatePlanCount);
    }

    [Fact]
    public async Task AttemptIntentExistsBeforeProviderCallAndIsUpdatedOnSuccess()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        Guid? intentId = null;
        fixture.Provider.OnCreate = () =>
        {
            PlanningAttemptRecord intent = Assert.Single(fixture.Attempts.Attempts);
            Assert.Equal("started", intent.Status);
            Assert.Null(intent.ActualCostUsd);
            intentId = intent.AttemptId;
        };

        Assert.IsType<PlanningResult.Created>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        PlanningAttemptRecord completed = Assert.Single(fixture.Attempts.Attempts);
        Assert.Equal(intentId, completed.AttemptId);
        Assert.Equal("succeeded", completed.Status);
        Assert.Equal(fixture.Provider.ActualCostUsd, completed.ActualCostUsd);
    }

    [Fact]
    public async Task CreatedRevisionPreservesAuditForAllConfiguredEditingSkills()
    {
        PlanningFixture fixture = PlanningFixture.Create();

        PlanningResult.Created created = Assert.IsType<PlanningResult.Created>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        EditPlanAudit audit = Assert.IsType<EditPlanAudit>(created.Revision.Audit);
        Assert.Equal(1, audit.SchemaVersion);
        Assert.Equal(
            [
                "multi-clip-director",
                "semantic-story",
                "hook-builder",
                "smart-cut",
                "best-shot",
                "pacing-director",
                "speech-tightening",
                "caption-director",
                "visual-director",
                "smart-reframe",
                "audio-director"
            ],
            audit.Skills.Select(skill => skill.SkillId));
        Assert.All(audit.Skills, skill =>
        {
            Assert.Equal("1.0.0", skill.SkillVersion);
            Assert.True(Enum.IsDefined(skill.Status));
            Assert.False(skill.Reasons.IsDefault);
        });
    }

    [Fact]
    public void SkillDecisionChangeChangesRevisionHash()
    {
        EditPlan plan = ValidPlanFixture.Create().Plan;
        EditPlanAudit first = new(
            1,
            [new EditingSkillAuditEntry(
                "smart-cut",
                "1.0.0",
                EditingDecisionStatus.Applied,
                [new EditingDecisionReason("cut.applied", "First decision.")])]);
        EditPlanAudit second = first with
        {
            Skills = [first.Skills[0] with
            {
                Reasons = [new EditingDecisionReason("cut.applied", "Changed decision.")]
            }]
        };

        string firstHash = EditPlanRevisionIdentity.CalculateHash(plan, null, first);
        string repeatedHash = EditPlanRevisionIdentity.CalculateHash(plan, null, first);
        string secondHash = EditPlanRevisionIdentity.CalculateHash(plan, null, second);
        RevisionId firstRevisionId = EditPlanRevisionIdentity.CalculateRevisionId(plan, null, first);
        RevisionId secondRevisionId = EditPlanRevisionIdentity.CalculateRevisionId(plan, null, second);

        Assert.Equal(firstHash, repeatedHash);
        Assert.NotEqual(firstHash, secondHash);
        Assert.NotEqual(firstRevisionId, secondRevisionId);
    }

    [Fact]
    public async Task UnknownProviderSegmentIsRejectedAndNotSaved()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.ReturnUnknownSegment = true;

        PlanningResult result = await fixture.Planner.PlanAsync(
            fixture.Request,
            CancellationToken.None);

        PlanningResult.Rejected rejected = Assert.IsType<PlanningResult.Rejected>(result);
        Assert.Equal("planning.segment_unknown", rejected.ErrorCode);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(2, fixture.Provider.CreatePlanCount);
    }

    [Fact]
    public async Task AlternativeVariantIdenticalToParentIsRejectedAndNotSaved()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        EditPlan parentPlan = fixture.Source.Plan with { VariantId = VariantId.New() };
        fixture.Store.ParentApproved = new EditPlanRevision(
            parentPlan.RevisionId,
            parentPlan.JobId,
            parentPlan.VariantId,
            "parent-hash",
            parentPlan,
            0m);

        PlanningResult.Rejected rejected = Assert.IsType<PlanningResult.Rejected>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        Assert.Equal("planning.variant_not_diverse", rejected.ErrorCode);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(2, fixture.Provider.CreatePlanCount);
    }

    [Fact]
    public async Task ProviderCannotOmitMultiClipDirectorSelection()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        AnalysisResult approvedAnalysis = fixture.Analysis with
        {
            AllowedSegments = [.. fixture.Analysis.AllowedSegments.Select(segment => segment with { IsApproved = true })]
        };
        fixture.Request = fixture.Request with
        {
            Analysis = approvedAnalysis,
            MultiSourceAnalysis = AnalysisContractAdapter.ToMultiSource(approvedAnalysis)
        };
        fixture.Provider.OmitLastSegment = true;

        PlanningResult.Rejected rejected = Assert.IsType<PlanningResult.Rejected>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        Assert.Equal("planning.required_segment_missing", rejected.ErrorCode);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(2, fixture.Provider.CreatePlanCount);
    }

    [Fact]
    public async Task ExceedingCostLimitWaitsForApprovalWithoutPaidCall()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.EstimatedCostUsd = 0.08m;
        fixture.CostPolicy.Limit = 0.05m;

        PlanningResult result = await fixture.Planner.PlanAsync(
            fixture.Request,
            CancellationToken.None);

        PlanningResult.WaitingForApproval waiting =
            Assert.IsType<PlanningResult.WaitingForApproval>(result);
        Assert.Equal(0.08m, waiting.EstimatedCostUsd);
        Assert.Equal(0, fixture.Provider.CreatePlanCount);
    }

    [Fact]
    public async Task ProviderResponseIsRetriedAtMostOnce()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.ReturnUnknownSegment = true;

        PlanningResult result = await fixture.Planner.PlanAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.IsType<PlanningResult.Rejected>(result);
        Assert.Equal(2, fixture.Provider.CreatePlanCount);
        Assert.Equal(1, fixture.Provider.LastRepairAttempt);
    }

    [Fact]
    public async Task InvalidFirstResponseAndRepairAreRecordedInTotalCost()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.InvalidFirstResponse = true;

        PlanningResult result = await fixture.Planner.PlanAsync(
            fixture.Request,
            CancellationToken.None);

        PlanningResult.Created created = Assert.IsType<PlanningResult.Created>(result);
        Assert.Equal(0.02m, created.Revision.ActualCostUsd);
        Assert.Equal(2, fixture.Attempts.Attempts.Count);
        Assert.Equal(0.02m, fixture.Attempts.Attempts.Sum(attempt => attempt.ActualCostUsd ?? 0m));
        Assert.Equal("invalid", fixture.Attempts.Attempts[0].Status);
        Assert.Equal("succeeded", fixture.Attempts.Attempts[1].Status);
    }

    [Fact]
    public async Task InvalidStructuredProviderResponseUsesRepairAttempt()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.InvalidStructuredFirstResponse = true;

        PlanningResult result = await fixture.Planner.PlanAsync(
            fixture.Request,
            CancellationToken.None);

        PlanningResult.Created created = Assert.IsType<PlanningResult.Created>(result);
        Assert.Equal(0.02m, created.Revision.ActualCostUsd);
        Assert.Equal(2, fixture.Provider.CreatePlanCount);
        Assert.Equal(["invalid", "succeeded"], fixture.Attempts.Attempts.Select(attempt => attempt.Status));
    }

    [Fact]
    public async Task ReorderedProviderClipsGetContiguousOutputTimeline()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.ReverseClips = true;

        PlanningResult.Created created = Assert.IsType<PlanningResult.Created>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(
            created.Revision.Plan.Video.Clips.SkipLast(1).Select(clip => clip.Output.EndFrame),
            created.Revision.Plan.Video.Clips.Skip(1).Select(clip => clip.Output.StartFrame));
        Assert.Equal(0, created.Revision.Plan.Video.Clips[0].Output.StartFrame);
    }

    [Fact]
    public async Task RepairAttemptIsBlockedWhenAccumulatedCostExceedsBudget()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.InvalidFirstResponse = true;
        fixture.Provider.ActualCostUsd = 0.4m;
        fixture.Provider.EstimatedCostUsd = 0.4m;
        fixture.CostPolicy.Limit = 0.7m;

        PlanningResult result = await fixture.Planner.PlanAsync(
            fixture.Request,
            CancellationToken.None);

        PlanningResult.WaitingForApproval waiting = Assert.IsType<PlanningResult.WaitingForApproval>(result);
        Assert.Equal(0.4m, waiting.EstimatedCostUsd);
        Assert.Equal(1, fixture.Provider.CreatePlanCount);
        Assert.Single(fixture.Attempts.Attempts);
        Assert.Equal(0.4m, fixture.Attempts.Attempts[0].ActualCostUsd);
    }

    [Fact]
    public async Task ApprovedRepairResumesWithoutRepeatingInvalidAttempt()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.InvalidFirstResponse = true;
        fixture.Provider.ActualCostUsd = 0.4m;
        fixture.Provider.EstimatedCostUsd = 0.4m;
        fixture.CostPolicy.Limit = 0.7m;
        fixture.Request = fixture.Request with { TelegramUserId = 123, JobLimitUsd = 0.7m };

        PlanningResult.WaitingForApproval waiting = Assert.IsType<PlanningResult.WaitingForApproval>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));
        Assert.NotNull(waiting.Version);
        fixture.Approvals.ApproveLatest();

        PlanningResult.Created created = Assert.IsType<PlanningResult.Created>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(0.8m, created.Revision.ActualCostUsd);
        Assert.Equal([0, 1], fixture.Provider.RepairAttempts);
    }

    [Fact]
    public async Task ApprovalDoesNotAuthorizeCallAfterJobLimitIsLowered()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Provider.EstimatedCostUsd = 0.11m;
        fixture.CostPolicy.Limit = 0.10m;
        fixture.Request = fixture.Request with { TelegramUserId = 123, JobLimitUsd = 0.10m };

        Assert.IsType<PlanningResult.WaitingForApproval>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));
        fixture.Approvals.ApproveLatest();
        fixture.CostPolicy.Limit = 0.05m;
        fixture.Request = fixture.Request with { JobLimitUsd = 0.05m };

        PlanningResult.WaitingForApproval waiting = Assert.IsType<PlanningResult.WaitingForApproval>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        Assert.Equal(2, waiting.Version);
        Assert.Equal(0.05m, fixture.Approvals.Latest!.LimitUsd);
        Assert.Equal(PlanningApprovalStatus.Pending, fixture.Approvals.Latest.Status);
        Assert.Equal(0, fixture.Provider.CreatePlanCount);
    }

    [Fact]
    public async Task ProviderTargetDurationUsesEffectiveSmartCutMappings()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Request = fixture.Request with
        {
            Settings = new ProductionSettings(null, true, false)
        };
        fixture.Planner = new EditPlanPlanner(
            fixture.Provider,
            fixture.CostPolicy,
            fixture.Store,
            fixture.Attempts,
            editingSkillEngine: new EditingSkillEngine([new FixedSmartCutSkill(fixture.Analysis)]));

        await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(2, fixture.Provider.LastTargetDurationSeconds);
        Assert.All(fixture.Provider.LastAllowedSegments, segment => Assert.Equal(1_000_000, segment.DurationMicroseconds));
    }

    [Fact]
    public async Task PreviousPaidAttemptCountsAgainstRetryBudget()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.CostPolicy.Limit = 0.10m;
        fixture.Provider.EstimatedCostUsd = 0.06m;
        fixture.Attempts.Attempts.Add(new PlanningAttemptRecord(
            Guid.NewGuid(), fixture.Request.JobId, fixture.Request.VariantId,
            1, 0, 0.06m, 0.06m, "failed", "provider.timeout", "timeout", DateTimeOffset.UtcNow));

        PlanningResult result = await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None);

        Assert.IsType<PlanningResult.WaitingForApproval>(result);
        Assert.Equal(0, fixture.Provider.CreatePlanCount);
    }

    [Fact]
    public async Task UnknownActualCostBlocksAnotherProviderCall()
    {
        PlanningFixture fixture = PlanningFixture.Create();
        fixture.Attempts.Attempts.Add(new PlanningAttemptRecord(
            Guid.NewGuid(), fixture.Request.JobId, fixture.Request.VariantId,
            1, 0, 0.06m, null, "started", null, null, DateTimeOffset.UtcNow));

        PlanningResult.Rejected rejected = Assert.IsType<PlanningResult.Rejected>(
            await fixture.Planner.PlanAsync(fixture.Request, CancellationToken.None));

        Assert.Equal("planning.cost_unknown", rejected.ErrorCode);
        Assert.Equal(0, fixture.Provider.CreatePlanCount);
    }

    private sealed class PlanningFixture
    {
        public required ValidPlanFixture Source { get; init; }
        public required TestProvider Provider { get; init; }
        public required InMemoryRevisionStore Store { get; init; }
        public required InMemoryAttemptStore Attempts { get; init; }
        public required InMemoryApprovalStore Approvals { get; init; }
        public required PlanningRequest Request { get; set; }
        public EditPlanPlanner Planner { get; set; } = null!;
        public required TestCostPolicy CostPolicy { get; init; }
        public AnalysisResult Analysis => Source.Analysis;

        public static PlanningFixture Create()
        {
            ValidPlanFixture source = ValidPlanFixture.Create();
            TestProvider provider = new(source.Plan);
            InMemoryRevisionStore store = new();
            InMemoryAttemptStore attempts = new();
            store.CompleteAttempt = attempt => attempts.SaveAsync(attempt, CancellationToken.None);
            InMemoryApprovalStore approvals = new();
            TestCostPolicy costPolicy = new(1m);
            PlanningFixture fixture = new()
            {
                Source = source,
                Provider = provider,
                Store = store,
                Attempts = attempts,
                Approvals = approvals,
                CostPolicy = costPolicy,
                Request = new PlanningRequest(
                    source.Plan.JobId,
                    source.Plan.VariantId,
                    source.Analysis,
                    "Сделай динамично и добавь субтитры",
                    new ProductionSettings(6, true, false),
                    source.Assets,
                    new Dictionary<TemplateId, TemplateDefinition>
                    {
                        [TemplateId.Dynamic] = source.Templates[TemplateId.Dynamic]
                    })
            };
            fixture.Planner = new EditPlanPlanner(provider, costPolicy, store, attempts, approvals);
            return fixture;
        }
    }

    private sealed class TestProvider : IEditPlanProvider
    {
        private readonly ProviderPlanResponse response;
        public decimal EstimatedCostUsd { get; set; } = 0.01m;
        public decimal ActualCostUsd { get; set; } = 0.01m;
        public bool ReturnUnknownSegment { get; set; }
        public bool OmitLastSegment { get; set; }
        public bool InvalidFirstResponse { get; set; }
        public bool InvalidStructuredFirstResponse { get; set; }
        public bool ReverseClips { get; set; }
        public string? NetworkFailure { get; set; }
        public HttpStatusCode? NetworkStatusCode { get; set; }
        public bool Timeout { get; set; }
        public bool Cancel { get; set; }
        public CancellationTokenSource? CancellationToTrigger { get; set; }
        public Action? OnCreate { get; set; }
        public int CreatePlanCount { get; private set; }
        public int EstimateCostCount { get; private set; }
        public int LastRepairAttempt { get; private set; }
        public int? LastTargetDurationSeconds { get; private set; }
        public ImmutableArray<ProviderSegment> LastAllowedSegments { get; private set; } = [];
        public List<int> RepairAttempts { get; } = [];

        public TestProvider(EditPlan plan)
        {
            response = new ProviderPlanResponse(
                1,
                plan.Seed,
                plan.Video.Clips.Select(clip => new ProviderVideoClip(
                    clip.SegmentId,
                    clip.Output,
                    clip.Crop)).ToImmutableArray(),
                plan.Captions.Items.Select(caption => new ProviderCaption(
                    caption.Text,
                    caption.Output)).ToImmutableArray(),
                ActualCostUsd);
        }

        public Task<PlanningCostEstimate> EstimateCostAsync(
            ProviderPlanningRequest request,
            CancellationToken cancellationToken)
        {
            EstimateCostCount++;
            LastTargetDurationSeconds = request.TargetDurationSeconds;
            LastAllowedSegments = request.AllowedSegments;
            return NetworkFailure == "estimate"
                ? throw new HttpRequestException("original integration diagnostic")
                : Task.FromResult(new PlanningCostEstimate(
                    EstimatedCostUsd,
                    "fake",
                    "deterministic"));
        }

        public Task<ProviderPlanResponse> CreatePlanAsync(
            ProviderPlanningRequest request,
            CancellationToken cancellationToken)
        {
            CreatePlanCount++;
            RepairAttempts.Add(request.RepairAttempt);
            OnCreate?.Invoke();
            if (Cancel) throw new OperationCanceledException(cancellationToken);
            if (Timeout) throw new TaskCanceledException("The provider request timed out.");
            if (NetworkStatusCode is HttpStatusCode statusCode)
                throw new HttpRequestException("transient provider diagnostic", null, statusCode);
            if (NetworkFailure == "create") throw new HttpRequestException("original integration diagnostic");
            CancellationToTrigger?.Cancel();
            LastRepairAttempt = request.RepairAttempt;
            if (InvalidStructuredFirstResponse && request.RepairAttempt == 0)
            {
                throw new PlanningProviderException(
                    "The provider returned a structured plan with invalid values.",
                    ActualCostUsd,
                    new FormatException("The provider returned an invalid crop preset."));
            }
            if (ReturnUnknownSegment || (InvalidFirstResponse && request.RepairAttempt == 0))
            {
                return Task.FromResult(response with
                {
                    VideoClips = response.VideoClips.SetItem(
                        0,
                        response.VideoClips[0] with { SegmentId = SegmentId.New() }),
                    ActualCostUsd = ActualCostUsd
                });
            }

            if (OmitLastSegment)
            {
                return Task.FromResult(response with
                {
                    VideoClips = response.VideoClips.RemoveAt(response.VideoClips.Length - 1),
                    ActualCostUsd = ActualCostUsd
                });
            }

            if (ReverseClips)
            {
                return Task.FromResult(response with
                {
                    VideoClips = response.VideoClips.Reverse().ToImmutableArray(),
                    ActualCostUsd = ActualCostUsd
                });
            }

            return Task.FromResult(response with { ActualCostUsd = ActualCostUsd });
        }
    }

    private sealed class FixedSmartCutSkill(AnalysisResult analysis) : IEditingSkill
    {
        public string Id => "smart-cut";
        public string Version => "test";
        public ImmutableArray<string> Prerequisites => [];

        public EditingDecision Evaluate(EditingContext context) => new SmartCutDecision(
            Id,
            Version,
            EditingDecisionStatus.Applied,
            [],
            analysis.AllowedSegments.Select(segment => new SmartCutEdit(
                segment.Id,
                segment.Mapping with
                {
                    Master = new MasterRange(
                        segment.Mapping.Master.StartMicroseconds,
                        segment.Mapping.Master.StartMicroseconds + 1_000_000)
                },
                "test-boundary")).ToImmutableArray());
    }

    private sealed class TestCostPolicy(decimal limit) : IPlanningCostPolicy
    {
        public decimal Limit { get; set; } = limit;

        public PlanningCostDecision Decide(PlanningCostEstimate estimate, decimal accumulatedActualCostUsd) =>
            accumulatedActualCostUsd + estimate.EstimatedCostUsd > Limit
                ? new PlanningCostDecision(false, true, "Approval is required.")
                : new PlanningCostDecision(true, false, null);
    }

    private sealed class InMemoryRevisionStore : IEditPlanRevisionStore
    {
        private EditPlanRevision? revision;
        public int SaveCount { get; private set; }
        public EditPlanRevision? ParentApproved { get; set; }
        public bool CancelOnFind { get; set; }
        public Func<PlanningAttemptRecord, Task>? CompleteAttempt { get; set; }

        public Task<EditPlanRevision?> FindParentApprovedAsync(
            JobId jobId,
            VariantId variantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ParentApproved);

        public Task<EditPlanRevision?> GetAsync(
            RevisionId revisionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(revision?.RevisionId == revisionId ? revision : null);

        public Task<EditPlanRevision?> FindApprovedAsync(
            JobId jobId,
            VariantId variantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(revision is { JobId: var storedJob, VariantId: var storedVariant }
                && storedJob == jobId
                && storedVariant == variantId
                    ? revision
                    : null);

        public Task<EditPlanRevision?> FindAsync(
            JobId jobId,
            VariantId variantId,
            string planHash,
            CancellationToken cancellationToken)
        {
            if (CancelOnFind) throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(revision is { JobId: var storedJob, VariantId: var storedVariant, PlanHash: var storedHash }
                && storedJob == jobId
                && storedVariant == variantId
                && storedHash == planHash
                    ? revision
                    : null);
        }

        public Task<EditPlanRevision> SaveAsync(
            EditPlan plan,
            string planHash,
            decimal actualCostUsd,
            CancellationToken cancellationToken)
            => SaveAsync(plan, planHash, actualCostUsd, null, null, cancellationToken);

        public Task<EditPlanRevision> SaveAsync(
            EditPlan plan,
            string planHash,
            decimal actualCostUsd,
            ReelsPresentationPlan? presentation,
            EditPlanAudit? audit,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            revision = new EditPlanRevision(
                plan.RevisionId,
                plan.JobId,
                plan.VariantId,
                planHash,
                plan,
                actualCostUsd,
                presentation,
                audit);
            return Task.FromResult(revision);
        }

        public async Task<EditPlanRevision> CompletePlanningAsync(
            EditPlan plan,
            string planHash,
            decimal actualCostUsd,
            ReelsPresentationPlan? presentation,
            EditPlanAudit? audit,
            PlanningAttemptRecord completedAttempt,
            CancellationToken cancellationToken)
        {
            EditPlanRevision saved = await SaveAsync(
                plan, planHash, actualCostUsd, presentation, audit, cancellationToken);
            if (CompleteAttempt is not null) await CompleteAttempt(completedAttempt);
            return saved;
        }
    }

    private sealed class InMemoryAttemptStore : IPlanningAttemptStore
    {
        public List<PlanningAttemptRecord> Attempts { get; } = [];

        public Task<PlanningCostLedger> GetLedgerAsync(
            JobId jobId,
            VariantId variantId,
            CancellationToken cancellationToken)
        {
            PlanningAttemptRecord[] attempts = [.. Attempts.Where(attempt => attempt.JobId == jobId)];
            if (attempts.Any(attempt => attempt.ActualCostUsd is null))
                return Task.FromResult(new PlanningCostLedger(
                    PlanningCostLedgerState.Unknown, null, attempts.Where(attempt => attempt.VariantId == variantId)
                        .Select(attempt => attempt.RepairAttempt + 1).DefaultIfEmpty().Max()));
            return Task.FromResult(new PlanningCostLedger(
                PlanningCostLedgerState.Known,
                attempts.Sum(attempt => attempt.ActualCostUsd),
                attempts.Where(attempt => attempt.VariantId == variantId)
                    .Select(attempt => attempt.RepairAttempt + 1).DefaultIfEmpty().Max()));
        }

        public Task SaveAsync(PlanningAttemptRecord attempt, CancellationToken cancellationToken)
        {
            int index = Attempts.FindIndex(existing => existing.AttemptId == attempt.AttemptId);
            if (index < 0) Attempts.Add(attempt);
            else Attempts[index] = attempt;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryApprovalStore : IPlanningApprovalStore
    {
        private PlanningApproval? approval;

        public PlanningApproval? Latest => approval;

        public Task<PlanningApproval?> FindAsync(
            JobId jobId,
            VariantId variantId,
            CancellationToken cancellationToken) => Task.FromResult(approval);

        public Task<PlanningApproval> SavePendingAsync(
            JobId jobId,
            VariantId variantId,
            long telegramUserId,
            int repairAttempt,
            decimal estimatedCostUsd,
            decimal accumulatedActualCostUsd,
            decimal limitUsd,
            CancellationToken cancellationToken)
        {
            if (approval is { Status: PlanningApprovalStatus.Pending }) return Task.FromResult(approval);
            approval = new PlanningApproval(
                Guid.NewGuid(), jobId, variantId, telegramUserId, (approval?.Version ?? 0) + 1, repairAttempt,
                estimatedCostUsd, accumulatedActualCostUsd, limitUsd,
                PlanningApprovalStatus.Pending, DateTimeOffset.UtcNow, null);
            return Task.FromResult(approval);
        }

        public Task<PlanningApprovalResult> ResolveAsync(
            JobId jobId,
            VariantId variantId,
            long telegramUserId,
            int version,
            PlanningApprovalDecision decision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void ApproveLatest() => approval = approval! with
        {
            Status = PlanningApprovalStatus.Approved,
            DecidedAt = DateTimeOffset.UtcNow
        };
    }
}
