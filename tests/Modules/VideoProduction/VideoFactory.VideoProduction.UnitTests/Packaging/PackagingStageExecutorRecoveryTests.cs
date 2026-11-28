using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Packaging;
using VideoFactory.VideoProduction.Infrastructure.QualityAssurance;
using VideoFactory.VideoProduction.UnitTests.Fixtures;

namespace VideoFactory.VideoProduction.UnitTests.Packaging;

public sealed class PackagingStageExecutorRecoveryTests
{
    [Fact]
    public async Task PassedV1ReportIsReverifiedAndPackagingUsesPublishedV2()
    {
        string root = Path.Combine(Path.GetTempPath(), $"packaging-qa-v1-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ValidPlanFixture fixture = ValidPlanFixture.Create();
            EditPlanRevision revision = new(
                fixture.Plan.RevisionId,
                fixture.Plan.JobId,
                fixture.Plan.VariantId,
                "plan-hash",
                fixture.Plan,
                0m);
            JobCreationRecord job = new(
                fixture.Plan.JobId,
                fixture.Plan.VariantId,
                new JobRequest(
                    fixture.Analysis.OriginalAssetId,
                    "comment",
                    ProductionMode.Fast,
                    new ProductionSettings(null, true, false)),
                JobProgress.Restore(
                    JobStatus.Running,
                    ProductionStage.Packaging,
                    ProductionMode.Fast,
                    ProductionMode.Fast,
                    null),
                revision.RevisionId);
            byte[] renderBytes = [1, 2, 3, 4, 5, 6];
            await File.WriteAllBytesAsync(Path.Combine(root, "render.mp4"), renderBytes);
            ArtifactRecord render = new(
                ArtifactId.New(), "render-output", "render.mp4", Hash(renderBytes), renderBytes.Length,
                null, null, "test", job.JobId, job.VariantId);
            string v1Json = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                jobId = job.JobId,
                variantId = job.VariantId,
                planRevisionId = revision.RevisionId,
                planHash = revision.PlanHash,
                status = "passed",
                output = new { artifactId = render.ArtifactId, sha256 = render.Sha256, sizeBytes = render.SizeBytes }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            string v1Path = Path.Combine(root, "qa-v1.json");
            await File.WriteAllTextAsync(v1Path, v1Json);
            ArtifactRecord v1 = new(
                ArtifactId.New(), "qa-report", "qa-v1.json", Hash(Encoding.UTF8.GetBytes(v1Json)), Encoding.UTF8.GetByteCount(v1Json),
                v1Json, render.ArtifactId, "video-qa/1", job.JobId, job.VariantId);
            MemoryArtifactStore artifacts = new(render, v1);
            SequencedQa qa = new(existingEncodeIsValid: true);
            PackagingStageExecutor executor = new(
                artifacts,
                root,
                1_000,
                null,
                null,
                new PlanStore(revision),
                qa,
                new OutputQualityOptionsProfile(1080, 1920, 1, true, null, 1_000),
                0);

            StageExecutionResult result = await executor.ExecuteAsync(job, CancellationToken.None);

            Assert.Equal(StageExecutionKind.Completed, result.Kind);
            Assert.Equal(1, qa.CallCount);
            Assert.Equal(v1Json, await File.ReadAllTextAsync(v1Path));
            ArtifactRecord v2 = Assert.Single(
                artifacts.Records,
                item => item.ArtifactType == "qa-report" && item.ArtifactId != v1.ArtifactId);
            using JsonDocument v2Document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, v2.RelativePath)));
            Assert.Equal(2, v2Document.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(render.ArtifactId.Value, v2.ParentArtifactId?.Value);
            ArtifactRecord final = Assert.Single(artifacts.Records, item => item.ArtifactType == "final-mp4");
            using JsonDocument finalMetadata = JsonDocument.Parse(final.MetadataJson!);
            Assert.Equal(v2.ArtifactId.Value, finalMetadata.RootElement.GetProperty("qaArtifactId").GetProperty("Value").GetGuid());

            StageExecutionResult repeated = await executor.ExecuteAsync(job, CancellationToken.None);

            Assert.Equal(StageExecutionKind.Completed, repeated.Kind);
            Assert.Equal(1, qa.CallCount);
            Assert.Equal(2, artifacts.Records.Count(item => item.ArtifactType == "qa-report"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("tampered")]
    [InlineData("wrong-identity")]
    [InlineData("failed")]
    [InlineData("unknown-version")]
    public async Task InvalidOrUnsupportedReportIsNeverUpgraded(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), $"packaging-qa-v1-rejected-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ValidPlanFixture fixture = ValidPlanFixture.Create();
            EditPlanRevision revision = new(
                fixture.Plan.RevisionId,
                fixture.Plan.JobId,
                fixture.Plan.VariantId,
                "plan-hash",
                fixture.Plan,
                0m);
            JobCreationRecord job = new(
                fixture.Plan.JobId,
                fixture.Plan.VariantId,
                new JobRequest(
                    fixture.Analysis.OriginalAssetId,
                    "comment",
                    ProductionMode.Fast,
                    new ProductionSettings(null, true, false)),
                JobProgress.Restore(
                    JobStatus.Running,
                    ProductionStage.Packaging,
                    ProductionMode.Fast,
                    ProductionMode.Fast,
                    null),
                revision.RevisionId);
            byte[] renderBytes = [1, 2, 3, 4, 5, 6];
            await File.WriteAllBytesAsync(Path.Combine(root, "render.mp4"), renderBytes);
            ArtifactRecord render = new(
                ArtifactId.New(), "render-output", "render.mp4", Hash(renderBytes), renderBytes.Length,
                null, null, "test", job.JobId, job.VariantId);
            string reportJson = scenario == "malformed"
                ? "{"
                : JsonSerializer.Serialize(new
                {
                    schemaVersion = scenario == "unknown-version" ? 3 : 1,
                    jobId = scenario == "wrong-identity" ? JobId.New() : job.JobId,
                    variantId = job.VariantId,
                    planRevisionId = revision.RevisionId,
                    planHash = revision.PlanHash,
                    status = scenario == "failed" ? "failed" : "passed",
                    output = new { artifactId = render.ArtifactId, sha256 = render.Sha256, sizeBytes = render.SizeBytes }
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(Path.Combine(root, "qa.json"), reportJson);
            byte[] registeredBytes = Encoding.UTF8.GetBytes(scenario == "tampered" ? reportJson + " " : reportJson);
            ArtifactRecord report = new(
                ArtifactId.New(), "qa-report", "qa.json", Hash(registeredBytes), Encoding.UTF8.GetByteCount(reportJson),
                reportJson, render.ArtifactId, "video-qa/1", job.JobId, job.VariantId);
            MemoryArtifactStore artifacts = new(render, report);
            SequencedQa qa = new(existingEncodeIsValid: true);
            PackagingStageExecutor executor = new(
                artifacts,
                root,
                1_000,
                null,
                null,
                new PlanStore(revision),
                qa,
                new OutputQualityOptionsProfile(1080, 1920, 1, true, null, 1_000),
                0);

            StageExecutionResult result = await executor.ExecuteAsync(job, CancellationToken.None);

            Assert.Equal(StageExecutionKind.Permanent, result.Kind);
            Assert.Equal(0, qa.CallCount);
            Assert.Single(artifacts.Records, item => item.ArtifactType == "qa-report");
            Assert.DoesNotContain(artifacts.Records, item => item.ArtifactType == "final-mp4");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 1)]
    public async Task ExistingControlledEncodeWithoutQaIsRecoveredAfterRestart(
        bool encodeWasRegistered,
        bool existingEncodeIsValid,
        int expectedEncodeCalls)
    {
        string root = Path.Combine(Path.GetTempPath(), $"packaging-encode-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ValidPlanFixture fixture = ValidPlanFixture.Create();
            EditPlanRevision revision = new(
                fixture.Plan.RevisionId,
                fixture.Plan.JobId,
                fixture.Plan.VariantId,
                "plan-hash",
                fixture.Plan,
                0m);
            JobCreationRecord job = new(
                fixture.Plan.JobId,
                fixture.Plan.VariantId,
                new JobRequest(
                    fixture.Analysis.OriginalAssetId,
                    "comment",
                    ProductionMode.Fast,
                    new ProductionSettings(null, true, false)),
                JobProgress.Restore(
                    JobStatus.Running,
                    ProductionStage.Packaging,
                    ProductionMode.Fast,
                    ProductionMode.Fast,
                    null),
                revision.RevisionId);
            byte[] sourceBytes = [1, 2, 3, 4, 5, 6];
            byte[] encodedBytes = [7, 8, 9];
            await File.WriteAllBytesAsync(Path.Combine(root, "render.mp4"), sourceBytes);
            string encodedRelativePath = $"data/jobs/{job.JobId}/renders/{job.VariantId}/delivery-encode/output.mp4";
            string encodedPath = Path.Combine(root, encodedRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(encodedPath)!);
            await File.WriteAllBytesAsync(encodedPath, encodedBytes);
            ArtifactRecord render = new(
                ArtifactId.New(), "render-output", "render.mp4", Hash(sourceBytes), sourceBytes.Length,
                null, null, "test", job.JobId, job.VariantId);
            ArtifactRecord encoded = new(
                ArtifactId.New(), "delivery-encode-output", encodedRelativePath, Hash(encodedBytes), encodedBytes.Length,
                null, render.ArtifactId, "test", job.JobId, job.VariantId);
            string qaJson = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                jobId = job.JobId,
                variantId = job.VariantId,
                planRevisionId = revision.RevisionId,
                planHash = revision.PlanHash,
                status = "passed",
                checks = new
                {
                    ffprobePassed = true,
                    fullDecodePassed = true,
                    audioAssertionsPassed = true
                },
                output = new { artifactId = render.ArtifactId, sha256 = render.Sha256, sizeBytes = render.SizeBytes }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(Path.Combine(root, "qa.json"), qaJson);
            ArtifactRecord sourceQa = new(
                ArtifactId.New(), "qa-report", "qa.json", Hash(Encoding.UTF8.GetBytes(qaJson)), Encoding.UTF8.GetByteCount(qaJson),
                qaJson, render.ArtifactId, "test", job.JobId, job.VariantId);
            MemoryArtifactStore artifacts = encodeWasRegistered
                ? new(render, sourceQa, encoded)
                : new(render, sourceQa);
            RecordingEncoder encoder = new();
            PackagingStageExecutor executor = new(
                artifacts,
                root,
                4,
                null,
                encoder,
                new PlanStore(revision),
                new SequencedQa(existingEncodeIsValid),
                new OutputQualityOptionsProfile(1080, 1920, 1, true, null, 4),
                1);

            StageExecutionResult result = await executor.ExecuteAsync(job, CancellationToken.None);

            Assert.Equal(StageExecutionKind.Completed, result.Kind);
            Assert.Equal(expectedEncodeCalls, encoder.CallCount);
            ArtifactRecord recoveredEncode = Assert.Single(artifacts.Records, item => item.ArtifactType == "delivery-encode-output");
            Assert.Contains(artifacts.Records, item => item.ArtifactType == "qa-report" && item.ParentArtifactId == recoveredEncode.ArtifactId);
            Assert.Contains(artifacts.Records, item => item.ArtifactType == "final-mp4" && item.ParentArtifactId == recoveredEncode.ArtifactId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class MemoryArtifactStore(params ArtifactRecord[] records) : IArtifactStore
    {
        public List<ArtifactRecord> Records { get; } = [.. records];

        public Task<ArtifactRecord?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken) =>
            Task.FromResult(Records.SingleOrDefault(item => item.ArtifactId == artifactId));

        public Task<ArtifactRecord?> FindLatestAsync(JobId jobId, string artifactType, CancellationToken cancellationToken) =>
            Task.FromResult(Records.LastOrDefault(item => item.JobId == jobId && item.ArtifactType == artifactType));

        public Task<IReadOnlyList<ArtifactRecord>> ListAsync(JobId jobId, string artifactType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtifactRecord>>(Records.Where(item => item.JobId == jobId && item.ArtifactType == artifactType).ToArray());

        public Task<ArtifactRegistrationResult> RegisterAsync(ArtifactRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.FromResult<ArtifactRegistrationResult>(new ArtifactRegistrationResult.Created(record));
        }
    }

    private sealed class PlanStore(EditPlanRevision revision) : IEditPlanRevisionStore
    {
        public Task<EditPlanRevision?> FindApprovedAsync(JobId jobId, VariantId variantId, CancellationToken cancellationToken) => Task.FromResult<EditPlanRevision?>(revision);
        public Task<EditPlanRevision?> GetAsync(RevisionId revisionId, CancellationToken cancellationToken) => Task.FromResult<EditPlanRevision?>(revision);
        public Task<EditPlanRevision?> FindAsync(JobId jobId, VariantId variantId, string planHash, CancellationToken cancellationToken) => Task.FromResult<EditPlanRevision?>(revision);
        public Task<EditPlanRevision> SaveAsync(EditPlan plan, string planHash, decimal actualCostUsd, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EditPlanRevision> CompletePlanningAsync(
            EditPlan plan, string planHash, decimal actualCostUsd,
            ReelsPresentationPlan? presentation, EditPlanAudit? audit,
            PlanningAttemptRecord completedAttempt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingEncoder : IControlledVideoEncoder
    {
        public int CallCount { get; private set; }
        public async Task<ControlledEncodeResult> EncodeAsync(ControlledEncodeRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            await File.WriteAllBytesAsync(request.OutputPath, [10, 11, 12], cancellationToken);
            return new ControlledEncodeResult(true, null, null);
        }
    }

    private sealed class SequencedQa(bool existingEncodeIsValid) : IOutputQualityAssurance
    {
        private int callCount;

        public int CallCount => callCount;

        public Task<OutputQualityResult> VerifyAsync(OutputQualityRequest request, CancellationToken cancellationToken)
        {
            callCount++;
            return Task.FromResult(!existingEncodeIsValid && callCount == 1
                ? OutputQualityResult.Failed("qa.decode_failed", "The recovered encode is incomplete.")
                : OutputQualityResult.Succeeded(1080, 1920, 180));
        }
    }
}
