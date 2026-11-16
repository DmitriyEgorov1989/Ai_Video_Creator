using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.QualityAssurance;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.QualityAssurance;

public sealed class VerificationPrivacyTests
{
    [Fact]
    public async Task UntrustedDiagnosticIsKeptForStageButNeverIncludedInQaArtifact()
    {
        string root = Path.Combine(Path.GetTempPath(), $"qa-privacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ValidPlanFixture fixture = ValidPlanFixture.Create();
            JobCreationRecord job = new(fixture.Plan.JobId, fixture.Plan.VariantId,
                new JobRequest(ArtifactId.New(), "private-comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Running, ProductionStage.Verification, ProductionMode.Fast, ProductionMode.Fast, null), fixture.Plan.RevisionId);
            Store store = new(new ArtifactRecord(ArtifactId.New(), "render-output", "output.mp4", "hash", 3, null, null, "test", job.JobId));
            const string diagnostic = "synthetic-token private-comment private-transcript";
            VerificationStageExecutor executor = new(new Plans(fixture.Plan), store, new Qa(diagnostic), root,
                new OutputQualityOptionsProfile(1080, 1920, 1, true, "yuv420p", 1_000));
            StageExecutionResult result = await executor.ExecuteAsync(job, CancellationToken.None);
            Assert.Equal(StageExecutionKind.Permanent, result.Kind);
            Assert.Equal(diagnostic, result.ErrorMessage);
            string json = await File.ReadAllTextAsync(Directory.GetFiles(root, "qa-report-*.json").Single());
            Assert.DoesNotContain("synthetic-token", json);
            Assert.DoesNotContain("private-comment", json);
            Assert.DoesNotContain("private-transcript", json);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Qa(string message) : IOutputQualityAssurance
    {
        public Task<OutputQualityResult> VerifyAsync(OutputQualityRequest request, CancellationToken token) =>
            Task.FromResult(OutputQualityResult.Failed("decode.failed", message));
    }

    private sealed class Plans(EditPlan plan) : IEditPlanRevisionStore
    {
        public Task<EditPlanRevision?> FindApprovedAsync(JobId id, VariantId variant, CancellationToken token) =>
            Task.FromResult<EditPlanRevision?>(null);
        public Task<EditPlanRevision?> GetAsync(RevisionId id, CancellationToken token) =>
            Task.FromResult<EditPlanRevision?>(new(plan.RevisionId, plan.JobId, plan.VariantId, "plan-hash", plan, 0));
        public Task<EditPlanRevision?> FindAsync(JobId id, VariantId variant, string hash, CancellationToken token) => throw new NotSupportedException();
        public Task<EditPlanRevision> SaveAsync(EditPlan value, string hash, decimal cost, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Store(ArtifactRecord render) : IArtifactStore
    {
        public Task<ArtifactRecord?> GetAsync(ArtifactId id, CancellationToken token) => Task.FromResult<ArtifactRecord?>(render);
        public Task<ArtifactRecord?> FindLatestAsync(JobId id, string type, CancellationToken token) => Task.FromResult<ArtifactRecord?>(render);
        public Task<ArtifactRegistrationResult> RegisterAsync(ArtifactRecord record, CancellationToken token) =>
            Task.FromResult<ArtifactRegistrationResult>(new ArtifactRegistrationResult.Created(record));
    }
}
