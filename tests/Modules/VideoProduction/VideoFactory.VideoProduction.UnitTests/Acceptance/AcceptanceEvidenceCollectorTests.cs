using VideoFactory.VideoProduction.Application.Acceptance;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.UnitTests.Acceptance;

public sealed class AcceptanceEvidenceCollectorTests
{
    [Fact]
    public async Task CompletedJobWithMissingFinalFileIsRejectedWithoutThrowing()
    {
        JobId jobId = JobId.New();
        AcceptanceEvidenceSnapshot snapshot = AcceptanceEvidenceSnapshot.Completed(jobId) with
        {
            Artifacts =
            [
                new AcceptanceArtifactEvidence(
                    "final-mp4",
                    Guid.NewGuid().ToString(),
                    "stored-hash",
                    42,
                    Exists: false,
                    ActualSha256: null,
                    ActualSizeBytes: null)
            ]
        };
        AcceptanceEvidenceCollector collector = new(new StubSource(snapshot));

        AcceptanceEvidenceReport report = await collector.CollectAsync(jobId, CancellationToken.None);

        Assert.Equal(AcceptanceEvidenceOutcome.Failed, report.Outcome);
        Assert.Contains(report.Checks, check => check.Code == "artifact.final-mp4.missing" && !check.Passed);
    }

    [Fact]
    public async Task ConsistentCompletedJobPasses()
    {
        JobId jobId = JobId.New();
        AcceptanceEvidenceSnapshot snapshot = ValidCompleted(jobId);

        AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(new StubSource(snapshot))
            .CollectAsync(jobId, CancellationToken.None);

        Assert.Equal(AcceptanceEvidenceOutcome.Passed, report.Outcome);
        Assert.All(report.Checks, check => Assert.True(check.Passed, check.Code));
    }

    [Fact]
    public async Task RunningJobIsReportedAsInProgress()
    {
        JobId jobId = JobId.New();
        AcceptanceEvidenceSnapshot snapshot = AcceptanceEvidenceSnapshot.Completed(jobId) with
        {
            JobStatus = JobStatus.Running
        };

        AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(new StubSource(snapshot))
            .CollectAsync(jobId, CancellationToken.None);

        Assert.Equal(AcceptanceEvidenceOutcome.InProgress, report.Outcome);
        Assert.Equal("job.completed", Assert.Single(report.Checks).Code);
    }

    [Fact]
    public async Task CompletedJobOutsideDeliveryStageIsRejected()
    {
        JobId jobId = JobId.New();
        AcceptanceEvidenceSnapshot snapshot = ValidCompleted(jobId) with
        {
            JobStage = ProductionStage.Packaging
        };

        AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(new StubSource(snapshot))
            .CollectAsync(jobId, CancellationToken.None);

        Assert.Equal(AcceptanceEvidenceOutcome.Failed, report.Outcome);
        Assert.Contains(report.Checks, check => check.Code == "job.delivery_stage" && !check.Passed);
    }

    [Theory]
    [InlineData("qa.passed")]
    [InlineData("delivery.succeeded")]
    public async Task FailedQaOrDeliveryIsRejected(string failedCheck)
    {
        JobId jobId = JobId.New();
        AcceptanceEvidenceSnapshot valid = ValidCompleted(jobId);
        AcceptanceEvidenceSnapshot snapshot = failedCheck == "qa.passed"
            ? valid with { QaPassed = false }
            : valid with { DeliverySucceeded = false };

        AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(new StubSource(snapshot))
            .CollectAsync(jobId, CancellationToken.None);

        Assert.Equal(AcceptanceEvidenceOutcome.Failed, report.Outcome);
        Assert.Contains(report.Checks, check => check.Code == failedCheck && !check.Passed);
    }

    [Fact]
    public async Task DuplicateOrMismatchedUploadEvidenceIsRejected()
    {
        JobId jobId = JobId.New();
        AcceptanceEvidenceSnapshot valid = ValidCompleted(jobId);
        AcceptanceEvidenceSnapshot snapshot = valid with
        {
            SuccessfulDeliveryCount = 2,
            UploadedSha256 = new string('b', 64)
        };

        AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(new StubSource(snapshot))
            .CollectAsync(jobId, CancellationToken.None);

        Assert.Equal(2, report.SchemaVersion);
        Assert.Equal(AcceptanceEvidenceOutcome.Failed, report.Outcome);
        Assert.Contains(report.Checks, check => check.Code == "delivery.succeeded.exactly_one" && !check.Passed);
        Assert.Contains(report.Checks, check => check.Code == "delivery.upload.sha256.matches" && !check.Passed);
    }

    private static AcceptanceEvidenceSnapshot ValidCompleted(JobId jobId)
    {
        string hash = new('a', 64);
        return AcceptanceEvidenceSnapshot.Completed(jobId) with
        {
            JobStage = ProductionStage.Delivery,
            VariantId = Guid.NewGuid().ToString(),
            ApprovedRevisionId = Guid.NewGuid().ToString(),
            PlanHash = hash,
            RecomputedPlanHash = hash,
            SkillIds =
            [
                "multi-clip-director", "semantic-story", "hook-builder", "smart-cut", "best-shot",
                "pacing-director", "speech-tightening", "caption-director", "visual-director",
                "smart-reframe", "audio-director"
            ],
            AuditHash = hash,
            RecomputedAuditHash = hash,
            CorrelationMatches = true,
            ManifestV2 = true,
            ManifestLinksMatch = true,
            CriticChainValid = true,
            QaPassed = true,
            MediaProbePassed = true,
            FullDecodePassed = true,
            FrameAssertionsPassed = true,
            AudioAssertionsPassed = true,
            DeliverySucceeded = true,
            SuccessfulDeliveryCount = 1,
            UploadedSha256 = hash,
            UploadedSizeBytes = 10,
            ActualCostUsd = 0.01m,
            InstructionPresent = true,
            SourceOrderValid = true,
            StyleValid = true,
            SourceCount = 1,
            SourceMode = AcceptanceSourceMode.SingleSource,
            Artifacts =
            [
                new("render-output", Guid.NewGuid().ToString(), hash, 10, true, hash, 10),
                new("objective-critique-report", Guid.NewGuid().ToString(), hash, 10, true, hash, 10),
                new("qa-report", Guid.NewGuid().ToString(), hash, 10, true, hash, 10),
                new("final-mp4", Guid.NewGuid().ToString(), hash, 10, true, hash, 10),
                new("manifest", Guid.NewGuid().ToString(), hash, 10, true, hash, 10)
            ]
        };
    }

    private sealed class StubSource(AcceptanceEvidenceSnapshot snapshot) : IAcceptanceEvidenceSource
    {
        public Task<AcceptanceEvidenceSnapshot> ReadAsync(JobId jobId, CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }
}
