using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Media;
using VideoFactory.VideoProduction.Infrastructure.Queue;
using VideoFactory.VideoProduction.Infrastructure.Rendering;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using VideoFactory.VideoProduction.Infrastructure.QualityAssurance;
using VideoFactory.VideoProduction.Infrastructure.Packaging;
using VideoFactory.VideoProduction.Infrastructure.Delivery;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Application.Critique;

namespace VideoFactory.VideoProduction.IntegrationTests.Reliability;

public sealed class StageFaultTests
{
    [Fact]
    public async Task RenderRejectsTelegramSizeEstimateBeforeStartingRenderHost()
    {
        string root = Path.Combine(Path.GetTempPath(), $"render-size-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string cs = $"Data Source={Path.Combine(root, "jobs.db")};Pooling=False";
        try
        {
            await new SqliteMigrationRunner(cs).InitializeAsync(CancellationToken.None);
            ArtifactId sourceId = ArtifactId.New();
            await File.WriteAllBytesAsync(Path.Combine(root, "source.mp4"), [7, 8, 9]);
            SqliteArtifactStore artifacts = new(cs);
            await artifacts.RegisterAsync(new ArtifactRecord(sourceId, "OriginalVideo", "source.mp4", "source-hash", 3, null, null, "test"), CancellationToken.None);
            CreateJobResult.Succeeded created = Assert.IsType<CreateJobResult.Succeeded>(await new CreateJobUseCase(new SqliteJobCreationStore(cs))
                .ExecuteAsync(new CreateJobCommand(sourceId, "private-comment", new ProductionSettings(null, false, false))));
            EditPlan plan = new(1, created.JobId, created.VariantId, RevisionId.New(), RevisionId.New(), 42,
                new OutputProfile(1080, 1920, new FrameRate(30, 1), 3_000), TemplateId.Dynamic,
                new TemplateProps(CaptionPreset.None, ColorTheme.Dark, null), new VideoTrack([]),
                new AudioTrack([], []), new CaptionTrack([]), new OverlayTrack([]));
            SqliteEditPlanRevisionStore plans = new(cs);
            await plans.SaveAsync(plan, "plan-hash", 0, CancellationToken.None);
            await using (SqliteConnection connection = new(cs))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE jobs SET stage = $stage, current_approved_edit_plan_revision_id = $revision;";
                command.Parameters.AddWithValue("$stage", (int)ProductionStage.Rendering);
                command.Parameters.AddWithValue("$revision", plan.RevisionId.ToString());
                await command.ExecuteNonQueryAsync();
            }

            Host host = new(null);
            RenderingStageExecutor executor = new(plans, artifacts, host, new NoAudio(), new PassThroughNormalizer(), new Probe(), root,
                TimeSpan.FromSeconds(5), 4096, 50_000_000, 48000, "stereo", -16, -1,
                new OutputEncodingProfile(4_000_000, 128_000, 2_097_152), new Budget(false));

            StageExecutionResult result = await executor.ExecuteAsync(
                (await new SqliteJobStateStore(cs).GetAsync(created.JobId, CancellationToken.None))!,
                CancellationToken.None);

            Assert.Equal(StageExecutionKind.Permanent, result.Kind);
            Assert.Equal("render.telegram_size_estimate_exceeded", result.ErrorCode);
            Assert.Equal(0, host.Calls);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("process.timeout")]
    [InlineData("render.failed")]
    [InlineData("storage.insufficient_space")]
    [InlineData("process.cancelled")]
    [InlineData("process.abrupt")]
    public async Task RenderFailureSurvivesRestartWithoutSecondVariantOrFinalMp4(string fault)
    {
        string root = Path.Combine(Path.GetTempPath(), $"render-fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string cs = $"Data Source={Path.Combine(root, "jobs.db")};Pooling=False";
        try
        {
            await new SqliteMigrationRunner(cs).InitializeAsync(CancellationToken.None);
            ArtifactId sourceId = ArtifactId.New();
            await File.WriteAllBytesAsync(Path.Combine(root, "source.mp4"), [7, 8, 9]);
            SqliteArtifactStore artifacts = new(cs);
            await artifacts.RegisterAsync(new ArtifactRecord(sourceId, "OriginalVideo", "source.mp4", "source-hash", 3, null, null, "test"), CancellationToken.None);
            CreateJobResult.Succeeded created = Assert.IsType<CreateJobResult.Succeeded>(await new CreateJobUseCase(new SqliteJobCreationStore(cs))
                .ExecuteAsync(new CreateJobCommand(sourceId, "private-comment", new ProductionSettings(null, false, false))));
            EditPlan plan = new(1, created.JobId, created.VariantId, RevisionId.New(), RevisionId.New(), 42,
                new OutputProfile(1080, 1920, new FrameRate(30, 1), 30), TemplateId.Dynamic,
                new TemplateProps(CaptionPreset.None, ColorTheme.Dark, null), new VideoTrack([]),
                new AudioTrack([], []), new CaptionTrack([]), new OverlayTrack([]));
            SqliteEditPlanRevisionStore plans = new(cs);
            await plans.SaveAsync(plan, "plan-hash", 0, CancellationToken.None);
            await UpdateAsync("UPDATE jobs SET stage = $stage, current_approved_edit_plan_revision_id = $revision;",
                ("$stage", (int)ProductionStage.Rendering), ("$revision", plan.RevisionId.ToString()));
            if (fault == "process.abrupt")
                await new SqliteStageAttemptStore(cs).AppendAsync(new StageAttemptRecord(Guid.NewGuid(), created.JobId, created.VariantId,
                    ProductionStage.Rendering, 1, StageAttemptStatus.Running, null, null, DateTimeOffset.UtcNow.AddHours(-1), null), CancellationToken.None);
            Host host = new(fault);
            Budget budget = new(fault == "storage.insufficient_space");
            RenderingStageExecutor Executor() => new(plans, artifacts, host, new NoAudio(), new PassThroughNormalizer(), new Probe(), root,
                TimeSpan.FromSeconds(5), 4096, 50_000_000, 48000, "stereo", -16, -1,
                new OutputEncodingProfile(4_000_000, 128_000, 2_097_152), budget);
            SequentialJobProcessor Processor() => new(new SqliteJobWorkQueue(cs), new SqliteJobStateStore(cs), new SqliteStageAttemptStore(cs), Executor());
            Assert.True(await Processor().ProcessNextAsync("first", TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), 3, CancellationToken.None));
            JobCreationRecord? persisted = await new SqliteJobStateStore(cs).GetAsync(created.JobId, CancellationToken.None);
            Assert.Equal(ProductionStage.Rendering, persisted?.Progress.Stage);
            Assert.Equal(JobStatus.Running, persisted?.Progress.Status);
            Assert.Equal(fault, (await new SqliteStageAttemptStore(cs).GetLatestAsync(created.JobId, ProductionStage.Rendering, CancellationToken.None))?.ErrorCode);
            Assert.Null(await artifacts.FindLatestAsync(created.JobId, "render-output", CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(root, "data", "jobs", created.JobId.ToString(), "final")));
            if (fault == "storage.insufficient_space") Assert.Equal(0, host.Calls);
            host.Fault = null;
            budget.Reject = false;
            await UpdateAsync("UPDATE jobs SET heartbeat_at = '2000-01-01T00:00:00Z';");
            Assert.True(await Processor().ProcessNextAsync("restarted", TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), 3, CancellationToken.None));
            JobCreationRecord? recovered = await new SqliteJobStateStore(cs).GetAsync(created.JobId, CancellationToken.None);
            Assert.Equal(created.VariantId, recovered?.VariantId);
            Assert.Equal(ProductionStage.Critique, recovered?.Progress.Stage);
            Assert.NotNull(await artifacts.FindLatestAsync(created.JobId, "render-output", CancellationToken.None));
            JobCreationRecord renderingReplay = recovered! with { Progress = JobProgress.Restore(JobStatus.Running, ProductionStage.Rendering, ProductionMode.Fast, ProductionMode.Fast, null) };
            Assert.Equal(StageExecutionKind.Completed, (await Executor().ExecuteAsync(renderingReplay, CancellationToken.None)).Kind);
            Assert.Equal(fault == "storage.insufficient_space" ? 1 : 2, host.Calls);
            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(Path.Combine(root, "source.mp4")));
            CritiqueStageExecutor critique = new(plans, artifacts, new NoDefectObserver(), root, 2);
            Assert.Equal(StageExecutionKind.Completed, (await critique.ExecuteAsync(recovered!, CancellationToken.None)).Kind);
            JobCreationRecord verificationJob = recovered! with { Progress = recovered.Progress.CompleteCritique() };
            VerificationStageExecutor verification = new(plans, artifacts, new Qa(), root,
                new OutputQualityOptionsProfile(1080, 1920, 1, false, "yuv420p", 1000));
            Assert.Equal(StageExecutionKind.Completed, (await verification.ExecuteAsync(verificationJob, CancellationToken.None)).Kind);
            ArtifactRecord? firstReport = await artifacts.FindLatestAsync(created.JobId, "qa-report", CancellationToken.None);
            Assert.Equal(StageExecutionKind.Completed, (await verification.ExecuteAsync(verificationJob, CancellationToken.None)).Kind);
            Assert.Equal(firstReport?.ArtifactId, (await artifacts.FindLatestAsync(created.JobId, "qa-report", CancellationToken.None))?.ArtifactId);
            JobCreationRecord packagingJob = recovered! with { Progress = JobProgress.Restore(JobStatus.Running, ProductionStage.Packaging, ProductionMode.Fast, ProductionMode.Fast, null) };
            PackagingStageExecutor packaging = new(artifacts, root, 1000, null, null, plans, null, null, 0);
            Assert.Equal(StageExecutionKind.Completed, (await packaging.ExecuteAsync(packagingJob, CancellationToken.None)).Kind);
            Assert.Equal(StageExecutionKind.Completed, (await packaging.ExecuteAsync(packagingJob, CancellationToken.None)).Kind);
            await UpdateAsync("UPDATE jobs SET stage = $stage, heartbeat_at = '2000-01-01T00:00:00Z';", ("$stage", (int)ProductionStage.Delivery));
            Delivery transport = new();
            DeliveryStageExecutor delivery = new(artifacts, transport, new SqliteDeliveryReceiptStore(cs), root, 123456, plans);
            SequentialJobProcessor DeliverProcessor() => new(new SqliteJobWorkQueue(cs), new SqliteJobStateStore(cs), new SqliteStageAttemptStore(cs), delivery);
            Assert.True(await DeliverProcessor().ProcessNextAsync("delivery-first", TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), 3, CancellationToken.None));
            JobCreationRecord? waiting = await new SqliteJobStateStore(cs).GetAsync(created.JobId, CancellationToken.None);
            Assert.Equal(JobStatus.Running, waiting?.Progress.Status);
            Assert.Equal(ProductionStage.Delivery, waiting?.Progress.Stage);
            transport.Fail = false;
            await UpdateAsync("UPDATE jobs SET heartbeat_at = '2000-01-01T00:00:00Z';");
            Assert.True(await DeliverProcessor().ProcessNextAsync("delivery-restarted", TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), 3, CancellationToken.None));
            Assert.Equal(JobStatus.Completed, (await new SqliteJobStateStore(cs).GetAsync(created.JobId, CancellationToken.None))?.Progress.Status);
            await using (SqliteConnection check = new(cs))
            {
                await check.OpenAsync();
                await using SqliteCommand command = check.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM stage_attempts WHERE status = 'Running';";
                Assert.Equal(0L, await command.ExecuteScalarAsync());
            }
            Assert.Equal(fault == "storage.insufficient_space" ? 1 : 2, host.Calls);
            Assert.Equal(2, transport.Calls);
            ArtifactRecord final = (await artifacts.FindLatestAsync(created.JobId, "final-mp4", CancellationToken.None))!;
            await File.WriteAllBytesAsync(Path.Combine(root, final.RelativePath), [9, 9, 9]);
            StageExecutionResult changed = await delivery.ExecuteAsync(waiting!, CancellationToken.None);
            Assert.Equal(StageExecutionKind.Permanent, changed.Kind);
            Assert.Equal("delivery.file_changed", changed.ErrorCode);
            Assert.Equal(2, transport.Calls);

            async Task UpdateAsync(string sql, params (string Key, object Value)[] parameters)
            {
                await using SqliteConnection connection = new(cs);
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var item in parameters) command.Parameters.AddWithValue(item.Key, item.Value);
                await command.ExecuteNonQueryAsync();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("before")]
    [InlineData("ffmpeg")]
    [InlineData("during")]
    public async Task NormalizeFailureLeavesOriginalUntouchedAndNoPublishedMaster(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), $"normalize-fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "original.mp4");
            string master = Path.Combine(root, "normalized", "master.mp4");
            await File.WriteAllBytesAsync(original, [1, 2, 3]);
            Runner runner = new() { WaitForCancellation = scenario == "during" };
            Budget budget = new(scenario == "before") { RejectAfterFirstCheck = scenario == "during" };
            FfmpegMezzanineBuilder builder = new(runner, new Probe(), new NoDecode(), "ffmpeg", budget);
            MezzanineResult result = await builder.BuildAsync(new MezzanineRequest(original, master, "original", "master",
                new MediaMetadata("mp4", 1_000_000, [new VideoStreamMetadata(0, "h264", 64, 64, new FrameRate(30, 1), true, 0)], []),
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), 4096), CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal(scenario switch { "before" => "storage.insufficient_space", "during" => "storage.job_quota_exceeded", _ => "process.exit_code" }, result.ErrorCode);
            Assert.Equal(scenario == "before" ? 0 : 1, runner.Calls);
            Assert.False(File.Exists(master));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(original));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Runner : IProcessRunner
    {
        public int Calls { get; private set; }
        public bool WaitForCancellation { get; init; }
        public async Task<ProcessExecutionResult> RunAsync(ProcessStartRequest request, CancellationToken token)
        {
            Calls++;
            await File.WriteAllBytesAsync(request.Arguments[^1], [1], token);
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(ProcessExecutionStatus.NonZeroExitCode, 1, "", "original diagnostic", "process.exit_code", "original diagnostic", false, false);
        }
    }

    private sealed class Host(string? fault) : IRenderHostAdapter
    {
        public string? Fault { get; set; } = fault;
        public int Calls { get; private set; }
        public async Task<RenderHostResult> RenderAsync(RenderHostRequest request, CancellationToken token)
        {
            Calls++;
            await File.WriteAllBytesAsync(request.ExpectedOutputPath, [1, 2, 3], token);
            return Fault is null ? RenderHostResult.Succeeded(request.ExpectedOutputPath, 30, [], "", "")
                : RenderHostResult.Failed(Fault, "original render diagnostic", [], "", "original render diagnostic");
        }
    }

    private sealed class Budget(bool reject) : IStorageBudget
    {
        private int checks;
        public bool Reject { get; set; } = reject;
        public bool RejectAfterFirstCheck { get; init; }
        public Task<StorageBudgetFailure?> CheckAsync(string path, long bytes, CancellationToken token)
        {
            int check = Interlocked.Increment(ref checks);
            return Task.FromResult<StorageBudgetFailure?>(Reject ? new("storage.insufficient_space", "Insufficient space.")
                : RejectAfterFirstCheck && check > 1 ? new("storage.job_quota_exceeded", "Quota exceeded.") : null);
        }
    }
    private sealed class Probe : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(ProbeRequest request, CancellationToken token) =>
            Task.FromResult(MediaProbeResult.Succeeded(new MediaMetadata("mp4", 1_000_000,
                [new VideoStreamMetadata(0, "h264", 64, 64, new FrameRate(30, 1), false, 0)], [])));
    }
    private sealed class NoAudio : IAudioStemBuilder
    {
        public Task<AudioStemResult> BuildAsync(AudioStemRequest request, CancellationToken token) => throw new InvalidOperationException("No audio expected.");
    }
    private sealed class PassThroughNormalizer : IRenderOutputNormalizer
    {
        public async Task<RenderOutputNormalizationResult> NormalizeAsync(
            RenderOutputNormalizationRequest request,
            CancellationToken token)
        {
            await using FileStream source = new(request.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using FileStream output = new(request.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(output, token);
            return new(true, null, null);
        }
    }
    private sealed class NoDecode : IMediaDecodeVerifier
    {
        public Task<MediaDecodeResult> VerifyAsync(MediaDecodeRequest request, CancellationToken token) => throw new InvalidOperationException("Failed normalization must not decode.");
    }
    private sealed class Qa : IOutputQualityAssurance
    {
        public Task<OutputQualityResult> VerifyAsync(OutputQualityRequest request, CancellationToken token) =>
            Task.FromResult(OutputQualityResult.Succeeded(1080, 1920, 30));
    }
    private sealed class NoDefectObserver : IRenderedMediaObserver
    {
        public Task<RenderedMediaObservationResult> ObserveAsync(
            RenderedMediaObservationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<RenderedMediaObservationResult>(
                new RenderedMediaObservationResult.Succeeded(new RenderedMediaObservation([])));
    }
    private sealed class Delivery : IVideoDelivery
    {
        public bool Fail { get; set; } = true;
        public int Calls { get; private set; }
        public async Task<VideoDeliveryResult> SendAsync(VideoDeliveryRequest request, CancellationToken token)
        {
            Calls++;
            if (Fail) return VideoDeliveryResult.Failed("telegram.network", "original network diagnostic");
            byte[] bytes = await File.ReadAllBytesAsync(request.FilePath, token);
            return VideoDeliveryResult.Succeeded("42", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.LongLength);
        }
    }
}
