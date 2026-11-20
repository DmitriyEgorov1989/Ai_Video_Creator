using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Packaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace VideoFactory.VideoProduction.IntegrationTests.Reliability;

public sealed class PackagingSafetyTests
{
    [Fact]
    public async Task PackagingRejectsMissingApprovedPlanBeforePublishingManifest()
    {
        string root = Path.Combine(Path.GetTempPath(), $"packaging-plan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            JobId jobId = JobId.New();
            JobCreationRecord job = new(jobId, VariantId.New(),
                new JobRequest(ArtifactId.New(), "comment", ProductionMode.Fast,
                    new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Running, ProductionStage.Packaging,
                    ProductionMode.Fast, ProductionMode.Fast, null), RevisionId.New());
            MemoryArtifacts artifacts = new(
                new ArtifactRecord(ArtifactId.New(), "render-output", "render.mp4", "abc", 3, null, null, "test", jobId),
                new ArtifactRecord(ArtifactId.New(), "qa-report", "qa.json", "abc", 3, null, null, "test", jobId));

            StageExecutionResult result = await new PackagingStageExecutor(
                artifacts, root, 1_000, null, null, new MissingPlanStore(), null, null, 0)
                .ExecuteAsync(job, CancellationToken.None);

            Assert.Equal("packaging.plan_missing", result.ErrorCode);
            Assert.DoesNotContain(artifacts.Records, record => record.ArtifactType == "manifest");
            Assert.DoesNotContain(artifacts.Records, record => record.ArtifactType == "final-mp4");
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{\"status\":\"failed\"}", "packaging.qa_not_passed")]
    [InlineData("{\"status\":\"passed\"}", "packaging.qa_mismatch")]
    public async Task UnverifiedQaNeverPublishesFinalVideo(string reportJson, string expectedCode)
    {
        string root = Path.Combine(Path.GetTempPath(), $"packaging-safety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            JobId jobId = JobId.New();
            JobCreationRecord job = new(jobId, VariantId.New(),
                new JobRequest(ArtifactId.New(), "private comment", ProductionMode.Fast,
                    new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Running, ProductionStage.Packaging,
                    ProductionMode.Fast, ProductionMode.Fast, null), RevisionId.New());
            await File.WriteAllBytesAsync(Path.Combine(root, "render.mp4"), [1, 2, 3]);
            await File.WriteAllTextAsync(Path.Combine(root, "qa.json"), reportJson);
            MemoryArtifacts artifacts = new(
                new ArtifactRecord(ArtifactId.New(), "render-output", "render.mp4", null, 3, null, null, "test", jobId),
                new ArtifactRecord(ArtifactId.New(), "qa-report", "qa.json", null, null, null, null, "test", jobId));
            IJobStageExecutor executor = new PackagingStageExecutor(artifacts, root, 1_000);

            StageExecutionResult result = await executor.ExecuteAsync(job, CancellationToken.None);

            Assert.Equal(StageExecutionKind.Permanent, result.Kind);
            Assert.Equal(expectedCode, result.ErrorCode);
            Assert.DoesNotContain(artifacts.Records, record => record.ArtifactType == "final-mp4");
            Assert.DoesNotContain(Directory.GetFiles(root, "*.mp4", SearchOption.AllDirectories),
                path => path.Contains($"{Path.DirectorySeparatorChar}final{Path.DirectorySeparatorChar}"));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class MemoryArtifacts(params ArtifactRecord[] records) : IArtifactStore
    {
        public List<ArtifactRecord> Records { get; } = [.. records];
        public bool FailNextFinalRegistration { get; set; }
        public Task<ArtifactRecord?> GetAsync(ArtifactId id, CancellationToken token) =>
            Task.FromResult(Records.Find(record => record.ArtifactId == id));
        public Task<ArtifactRecord?> FindLatestAsync(JobId id, string type, CancellationToken token) =>
            Task.FromResult(Records.FindLast(record => record.JobId == id && record.ArtifactType == type));
        public Task<ArtifactRegistrationResult> RegisterAsync(ArtifactRecord record, CancellationToken token)
        {
            if (FailNextFinalRegistration && record.ArtifactType == "final-mp4")
            {
                FailNextFinalRegistration = false;
                throw new IOException("simulated SQLite/storage interruption");
            }
            Records.Add(record);
            return Task.FromResult<ArtifactRegistrationResult>(new ArtifactRegistrationResult.Created(record));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedPublicationIsRecoverableAndRejectsTamperedReport(bool tamperedReport)
    {
        string root = Path.Combine(Path.GetTempPath(), $"packaging-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            JobId id = JobId.New();
            RevisionId revision = RevisionId.New();
            JobCreationRecord job = new(id, VariantId.New(),
                new JobRequest(ArtifactId.New(), "private comment", ProductionMode.Fast, new ProductionSettings(null, true, false)),
                JobProgress.Restore(JobStatus.Running, ProductionStage.Packaging, ProductionMode.Fast, ProductionMode.Fast, null), revision);
            byte[] bytes = [1, 2, 3];
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(root, "render.mp4"), bytes);
            ArtifactRecord render = new(ArtifactId.New(), "render-output", "render.mp4", hash, 3, null, null, "test", id);
            string report = JsonSerializer.Serialize(new
            {
                status = "passed", jobId = id, variantId = job.VariantId, planRevisionId = revision,
                output = new { artifactId = render.ArtifactId, sha256 = hash, sizeBytes = 3 }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(Path.Combine(root, "qa.json"), report);
            string qaHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tamperedReport ? report + " " : report))).ToLowerInvariant();
            MemoryArtifacts store = new(render,
                new ArtifactRecord(ArtifactId.New(), "qa-report", "qa.json", qaHash, System.Text.Encoding.UTF8.GetByteCount(report), report, render.ArtifactId, "test", id))
            { FailNextFinalRegistration = true };
            if (tamperedReport)
            {
                store.FailNextFinalRegistration = false;
                StageExecutionResult rejected = await new PackagingStageExecutor(store, root, 1000).ExecuteAsync(job, CancellationToken.None);
                Assert.Equal("packaging.qa_changed", rejected.ErrorCode);
                Assert.DoesNotContain(store.Records, record => record.ArtifactType == "final-mp4");
                return;
            }
            StageExecutionResult noSpace = await new PackagingStageExecutor(store, root, 1000, new NoSpace())
                .ExecuteAsync(job, CancellationToken.None);
            Assert.Equal("storage.insufficient_space", noSpace.ErrorCode);
            Assert.False(Directory.Exists(Path.Combine(root, "data", "jobs", id.ToString(), "final")));
            IJobStageExecutor executor = new PackagingStageExecutor(store, root, 1_000);

            await Assert.ThrowsAsync<IOException>(() => executor.ExecuteAsync(job, CancellationToken.None));
            Assert.Equal(StageExecutionKind.Completed, (await executor.ExecuteAsync(job, CancellationToken.None)).Kind);
            Assert.Equal(StageExecutionKind.Completed, (await executor.ExecuteAsync(job, CancellationToken.None)).Kind);
            Assert.Single(store.Records, record => record.ArtifactType == "final-mp4");
            Assert.Single(store.Records, record => record.ArtifactType == "manifest");
            string manifest = await File.ReadAllTextAsync(Directory.GetFiles(root, "*.manifest.json", SearchOption.AllDirectories).Single());
            Assert.DoesNotContain("private comment", manifest);
            Assert.DoesNotContain(root.Replace("\\", "\\\\"), manifest);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class NoSpace : IStorageBudget
    {
        public Task<StorageBudgetFailure?> CheckAsync(string directory, long bytes, CancellationToken token) =>
            Task.FromResult<StorageBudgetFailure?>(new("storage.insufficient_space", "Insufficient space."));
    }

    private sealed class MissingPlanStore : IEditPlanRevisionStore
    {
        public Task<EditPlanRevision?> FindApprovedAsync(JobId jobId, VariantId variantId, CancellationToken cancellationToken) =>
            Task.FromResult<EditPlanRevision?>(null);
        public Task<EditPlanRevision?> GetAsync(RevisionId revisionId, CancellationToken cancellationToken) =>
            Task.FromResult<EditPlanRevision?>(null);
        public Task<EditPlanRevision?> FindAsync(JobId jobId, VariantId variantId, string planHash, CancellationToken cancellationToken) =>
            Task.FromResult<EditPlanRevision?>(null);
        public Task<EditPlanRevision> SaveAsync(VideoFactory.VideoProduction.Domain.Editing.EditPlan plan, string planHash, decimal actualCostUsd, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
