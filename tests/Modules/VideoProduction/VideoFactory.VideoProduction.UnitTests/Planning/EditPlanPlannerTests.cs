using System.Collections.Immutable;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Planning;

public sealed class EditPlanPlannerTests
{
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

    private sealed class PlanningFixture
    {
        public required ValidPlanFixture Source { get; init; }
        public required TestProvider Provider { get; init; }
        public required InMemoryRevisionStore Store { get; init; }
        public required PlanningRequest Request { get; init; }
        public EditPlanPlanner Planner { get; set; } = null!;
        public required TestCostPolicy CostPolicy { get; init; }
        public AnalysisResult Analysis => Source.Analysis;

        public static PlanningFixture Create()
        {
            ValidPlanFixture source = ValidPlanFixture.Create();
            TestProvider provider = new(source.Plan);
            InMemoryRevisionStore store = new();
            TestCostPolicy costPolicy = new(1m);
            PlanningFixture fixture = new()
            {
                Source = source,
                Provider = provider,
                Store = store,
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
            fixture.Planner = new EditPlanPlanner(provider, costPolicy, store);
            return fixture;
        }
    }

    private sealed class TestProvider : IEditPlanProvider
    {
        private readonly ProviderPlanResponse response;
        public decimal EstimatedCostUsd { get; set; } = 0.01m;
        public bool ReturnUnknownSegment { get; set; }
        public int CreatePlanCount { get; private set; }
        public int LastRepairAttempt { get; private set; }

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
                0.01m);
        }

        public Task<PlanningCostEstimate> EstimateCostAsync(
            ProviderPlanningRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PlanningCostEstimate(
                EstimatedCostUsd,
                "fake",
                "deterministic"));

        public Task<ProviderPlanResponse> CreatePlanAsync(
            ProviderPlanningRequest request,
            CancellationToken cancellationToken)
        {
            CreatePlanCount++;
            LastRepairAttempt = request.RepairAttempt;
            if (ReturnUnknownSegment)
            {
                return Task.FromResult(response with
                {
                    VideoClips = response.VideoClips.SetItem(
                        0,
                        response.VideoClips[0] with { SegmentId = SegmentId.New() })
                });
            }

            return Task.FromResult(response);
        }
    }

    private sealed class TestCostPolicy(decimal limit) : IPlanningCostPolicy
    {
        public decimal Limit { get; set; } = limit;

        public PlanningCostDecision Decide(PlanningCostEstimate estimate) =>
            estimate.EstimatedCostUsd > Limit
                ? new PlanningCostDecision(false, true, "Approval is required.")
                : new PlanningCostDecision(true, false, null);
    }

    private sealed class InMemoryRevisionStore : IEditPlanRevisionStore
    {
        private EditPlanRevision? revision;
        public int SaveCount { get; private set; }

        public Task<EditPlanRevision?> GetAsync(
            RevisionId revisionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(revision?.RevisionId == revisionId ? revision : null);

        public Task<EditPlanRevision?> FindAsync(
            JobId jobId,
            VariantId variantId,
            string planHash,
            CancellationToken cancellationToken) =>
            Task.FromResult(revision is { JobId: var storedJob, VariantId: var storedVariant, PlanHash: var storedHash }
                && storedJob == jobId
                && storedVariant == variantId
                && storedHash == planHash
                    ? revision
                    : null);

        public Task<EditPlanRevision> SaveAsync(
            EditPlan plan,
            string planHash,
            decimal actualCostUsd,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            revision = new EditPlanRevision(
                plan.RevisionId,
                plan.JobId,
                plan.VariantId,
                planHash,
                plan,
                actualCostUsd);
            return Task.FromResult(revision);
        }
    }
}
