using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.Bot.Configuration;
using VideoFactory.Bot.Telegram;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Analysis;
using VideoFactory.VideoProduction.Infrastructure.Delivery;
using VideoFactory.VideoProduction.Infrastructure.Media;
using VideoFactory.VideoProduction.Infrastructure.Packaging;
using VideoFactory.VideoProduction.Infrastructure.Pipeline;
using VideoFactory.VideoProduction.Infrastructure.Planning;
using VideoFactory.VideoProduction.Infrastructure.QualityAssurance;
using VideoFactory.VideoProduction.Infrastructure.Queue;
using VideoFactory.VideoProduction.Infrastructure.Rendering;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using VideoFactory.VideoProduction.Infrastructure.Processes;
using VideoFactory.VideoProduction.IntegrationTests.Media;

namespace VideoFactory.Bot.Tests.Acceptance;

public sealed class TelegramToDeliveryAcceptanceTests
{
    [AcceptanceFact]
    public async Task TelegramVideoAndCommentReachVerifiedDeliveryThroughWorker()
    {
        string? mediaRoot = Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT");
        string? whisperPython = Environment.GetEnvironmentVariable("WHISPER_TEST_PYTHON");
        string source = Path.Combine(mediaRoot!, "speech.mp4");
        Assert.True(File.Exists(source), $"Acceptance media is missing: {source}");
        string repositoryRoot = Fixture.RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "render-host", "dist", "cli.js")), "Build render-host before running this acceptance test.");

        await using Fixture fixture = await Fixture.CreateAsync(source, whisperPython!);
        await fixture.SendTelegramInputAsync();

        for (int i = 0; i < 32; i++)
        {
            await fixture.Processor.ProcessNextAsync("acceptance-worker", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), 3, CancellationToken.None);
            JobCreationRecord? current = await fixture.State.GetAsync(fixture.JobId, CancellationToken.None);
            if (current?.Progress.Status == JobStatus.Completed)
            {
                break;
            }
        }

        JobCreationRecord completed = Assert.IsType<JobCreationRecord>(await fixture.State.GetAsync(fixture.JobId, CancellationToken.None));
        StageAttemptRecord? failedAttempt = await fixture.Attempts.GetLatestAsync(
            fixture.JobId, completed.Progress.Stage, CancellationToken.None);
        Assert.True(completed.Progress.Status == JobStatus.Completed,
            $"root={fixture.Root}; stage={completed.Progress.Stage}; error={failedAttempt?.ErrorCode}; message={failedAttempt?.ErrorMessage}; process={fixture.Runner.LastRequest?.FileName} {string.Join(' ', fixture.Runner.LastRequest?.Arguments ?? [])}; stderr={fixture.Runner.LastResult?.StandardError}");
        Assert.Equal(ProductionStage.Delivery, completed.Progress.Stage);
        ArtifactRecord final = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(fixture.JobId, "final-mp4", CancellationToken.None));
        ArtifactRecord render = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(fixture.JobId, "render-output", CancellationToken.None));
        ArtifactRecord qa = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(fixture.JobId, "qa-report", CancellationToken.None));
        ArtifactRecord manifest = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(fixture.JobId, "manifest", CancellationToken.None));
        EditPlanRevision approvedPlan = Assert.IsType<EditPlanRevision>(await fixture.PlanRevisions.FindApprovedAsync(
            fixture.JobId, completed.VariantId, CancellationToken.None));
        Assert.Single(fixture.Delivery.Files);

        string finalPath = Path.Combine(fixture.Root, final.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        byte[] finalBytes = await File.ReadAllBytesAsync(finalPath);
        Assert.Equal(final.Sha256, Convert.ToHexStringLower(SHA256.HashData(finalBytes)));
        Assert.Equal(finalBytes, fixture.Delivery.Files.Single());

        string manifestPath = Path.Combine(fixture.Root, manifest.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        using JsonDocument manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        JsonElement manifestRoot = manifestDocument.RootElement;
        Assert.Equal(fixture.JobId.ToString(), GetIdentifier(manifestRoot.GetProperty("jobId")));
        Assert.Equal(completed.VariantId.ToString(), GetIdentifier(manifestRoot.GetProperty("variantId")));
        Assert.Equal(approvedPlan.RevisionId.ToString(), GetIdentifier(manifestRoot.GetProperty("planRevisionId")));
        Assert.Equal(approvedPlan.PlanHash, manifestRoot.GetProperty("planHash").GetString());
        Assert.Equal(render.ArtifactId.ToString(), GetIdentifier(manifestRoot.GetProperty("render").GetProperty("artifactId")));
        Assert.Equal(qa.ArtifactId.ToString(), GetIdentifier(manifestRoot.GetProperty("qa").GetProperty("artifactId")));
        Assert.Equal(final.ArtifactId.ToString(), GetIdentifier(manifestRoot.GetProperty("final").GetProperty("artifactId")));
        Assert.Equal(final.Sha256, manifestRoot.GetProperty("final").GetProperty("sha256").GetString());
        Assert.Equal(finalBytes.LongLength, manifestRoot.GetProperty("final").GetProperty("sizeBytes").GetInt64());

        MediaProbeResult probe = await fixture.Probe.ProbeAsync(new ProbeRequest(finalPath, TimeSpan.FromMinutes(1), 65_536), CancellationToken.None);
        Assert.True(probe.IsSuccess, probe.ErrorMessage);
        Assert.NotNull(probe.Metadata);
        Assert.Single(probe.Metadata!.VideoStreams);
        Assert.Single(probe.Metadata.AudioStreams);
        RgbSample middleFrame = await MediaMeasurements.SampleRegionAsync(
            new SystemProcessRunner(),
            finalPath,
            TimeSpan.FromTicks(probe.Metadata.DurationMicroseconds * 5),
            new PixelRegion(0, 0, 1080, 1920),
            CancellationToken.None);
        MontageQualityAssertions.RequireNonBlack("delivered MP4 middle frame", middleFrame, 10);
        MediaDecodeResult decode = await fixture.Decode.VerifyAsync(new MediaDecodeRequest(finalPath, TimeSpan.FromMinutes(2), 65_536), CancellationToken.None);
        Assert.True(decode.IsSuccess, decode.ErrorMessage);
    }

    [MediaAcceptanceFact]
    public async Task CorruptTelegramVideoFailsBeforeDelivery()
    {
        string source = Path.Combine(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!, "corrupt.mp4");
        await using Fixture fixture = await Fixture.CreateAsync(source, "python");
        await fixture.SendTelegramInputAsync();

        for (int attemptNumber = 0; attemptNumber < 3; attemptNumber++)
        {
            Assert.True(await fixture.Processor.ProcessNextAsync(
                "acceptance-worker", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), 3, CancellationToken.None));
        }

        JobCreationRecord failed = Assert.IsType<JobCreationRecord>(await fixture.State.GetAsync(fixture.JobId, CancellationToken.None));
        StageAttemptRecord? attempt = await fixture.Attempts.GetLatestAsync(
            fixture.JobId, failed.Progress.Stage, CancellationToken.None);
        Assert.True(failed.Progress.Status == JobStatus.Failed,
            $"stage={failed.Progress.Stage}; error={attempt?.ErrorCode}; message={attempt?.ErrorMessage}");
        Assert.Empty(fixture.Delivery.Files);
    }

    private static string GetIdentifier(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Object => element.GetProperty("value").GetGuid().ToString("N"),
        _ => throw new Xunit.Sdk.XunitException($"Expected an identifier, received {element.ValueKind}.")
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, string source, string whisperPython, string connectionString)
        {
            Root = root;
            Source = source;
            ConnectionString = connectionString;
            Runner = new RecordingProcessRunner();
            Probe = new FfprobeMediaProbe(Runner, "ffprobe");
            Decode = new FfmpegDecodeVerifier(Runner, "ffmpeg");
            Artifacts = new SqliteArtifactStore(connectionString);
            State = new SqliteJobStateStore(connectionString);
            JobCreation = new SqliteJobCreationStore(connectionString);
            Queue = new SqliteJobWorkQueue(connectionString);
            Attempts = new SqliteStageAttemptStore(connectionString);
            PlanRevisions = new SqliteEditPlanRevisionStore(connectionString);
            PlanningAttempts = new SqlitePlanningAttemptStore(connectionString);
            Approvals = new SqlitePlanningApprovalStore(connectionString);
            Receipts = new SqliteDeliveryReceiptStore(connectionString);
            LocalStorage = new LocalArtifactStorage(root);
            Delivery = new CapturingDelivery();

            FfmpegAudioActivityDetector activity = new(Runner, "ffmpeg", TimeSpan.FromMinutes(5), 65_536);
            FfmpegSceneBoundaryDetector scenes = new(Runner, "ffmpeg", TimeSpan.FromMinutes(5), 65_536);
            WhisperProcessTranscriber whisper = new(Runner, whisperPython, FindWorkerPath(), TimeSpan.FromMinutes(10), 65_536);
            AnalysisService analysisService = new(activity, whisper, scenes);
            AnalysisStageExecutor analysis = new(analysisService, new AnalysisArtifactPublisher(LocalStorage, Artifacts), Artifacts, root);

            EditPlanPlanner planner = new(
                new DeterministicPlanProvider(),
                new PlanningCostPolicy(new PlanningCostPolicyOptions(0.05m)),
                PlanRevisions,
                PlanningAttempts,
                Approvals);
            PlanningStageExecutor planning = new(planner, Artifacts, root, 0.05m);

            FfmpegMezzanineBuilder mezzanine = new(Runner, Probe, Decode, "ffmpeg", new PermissiveBudget());
            PreparationStageExecutor preparation = new(
                Artifacts, Probe, mezzanine, Decode, root,
                new MediaInputProfile { MaxFileBytes = 524_288_000, MaxDurationSeconds = 1800 },
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), 65_536, new PermissiveBudget());

            RenderHostProcessAdapter renderHost = new(Runner, "node", Path.Combine(RepositoryRoot(), "render-host", "dist", "cli.js"));
            RenderingStageExecutor rendering = new(
                PlanRevisions, Artifacts, renderHost,
                new FfmpegAudioStemBuilder(Runner, "ffmpeg"), Probe, root,
                TimeSpan.FromMinutes(30), 65_536, 50_000_000, 48_000, "stereo", -16, -1,
                new OutputEncodingProfile(4_000_000, 128_000, 2_097_152), new PermissiveBudget());

            OutputQualityOptionsProfile qualityProfile = new(1080, 1920, 3, true, "yuv420p", 50_000_000);
            VerificationStageExecutor verification = new(
                PlanRevisions, Artifacts,
                new OutputQualityAssurance(Probe, Decode, TimeSpan.FromMinutes(1), 65_536), root, qualityProfile);
            PackagingStageExecutor packaging = new(
                Artifacts, root, 50_000_000, new PermissiveBudget(), null, PlanRevisions, null, null, 0);
            DeliveryStageExecutor delivery = new(Artifacts, Delivery, Receipts, root, 123456789, PlanRevisions);
            ProductionStageExecutor stages = new(preparation, analysis, planning, rendering, verification, packaging, delivery);
            Processor = new SequentialJobProcessor(Queue, State, Attempts, stages);
            CapturingCreateJob createJob = new(new CreateJobUseCase(JobCreation));
            CreateJobAdapter = createJob;
            Handler = new TelegramUpdateHandler(
                new TelegramOptions { AllowedUserId = 123456789 },
                new LimitsOptions { MaxInputFileBytes = 524_288_000, MaxInputDurationSeconds = 1800 },
                new FixtureDownloader(root, source), Artifacts,
                createJob,
                new InMemoryTelegramIntakeSessionStore(), new CapturingResponder());
        }

        public string Root { get; }
        public string Source { get; }
        public string ConnectionString { get; }
        public JobId JobId { get; private set; }
        public RecordingProcessRunner Runner { get; }
        public IMediaProbe Probe { get; }
        public IMediaDecodeVerifier Decode { get; }
        public SqliteArtifactStore Artifacts { get; }
        public SqliteJobStateStore State { get; }
        public SqliteJobCreationStore JobCreation { get; }
        public SqliteJobWorkQueue Queue { get; }
        public SqliteStageAttemptStore Attempts { get; }
        public SqliteEditPlanRevisionStore PlanRevisions { get; }
        public SqlitePlanningAttemptStore PlanningAttempts { get; }
        public SqlitePlanningApprovalStore Approvals { get; }
        public SqliteDeliveryReceiptStore Receipts { get; }
        public LocalArtifactStorage LocalStorage { get; }
        public CapturingDelivery Delivery { get; }
        public SequentialJobProcessor Processor { get; }
        public TelegramUpdateHandler Handler { get; }
        private CapturingCreateJob CreateJobAdapter { get; }

        public static async Task<Fixture> CreateAsync(string source, string whisperPython)
        {
            string root = Path.Combine(Path.GetTempPath(), $"video-factory-acceptance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string database = Path.Combine(root, "jobs.sqlite");
            string connectionString = $"Data Source={database};Cache=Shared";
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            return new Fixture(root, source, whisperPython, connectionString);
        }

        public async Task SendTelegramInputAsync()
        {
            await Handler.HandleAsync(new TelegramUpdate(123456789, 123456789, null,
                new TelegramMediaInput("fixture-file", "fixture-file", new FileInfo(Source).Length, "video/mp4", "fixture.mp4")), CancellationToken.None);
            await Handler.HandleAsync(new TelegramUpdate(123456789, 123456789, "Сделай динамично", null), CancellationToken.None);
            JobId = CreateJobAdapter.JobId;
        }

        public async ValueTask DisposeAsync()
        {
            try { Directory.Delete(Root, true); } catch { await Task.CompletedTask; }
        }

        private static string FindWorkerPath() => Path.Combine(RepositoryRoot(), "scripts", "whisper-worker.py");

        internal static string RepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ai_Video_Creator.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        }
    }

    private sealed class PermissiveBudget : IStorageBudget
    {
        public Task<StorageBudgetFailure?> CheckAsync(string directory, long additionalBytes, CancellationToken cancellationToken) => Task.FromResult<StorageBudgetFailure?>(null);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private readonly SystemProcessRunner inner = new();

        public ProcessStartRequest? LastRequest { get; private set; }
        public ProcessExecutionResult? LastResult { get; private set; }

        public async Task<ProcessExecutionResult> RunAsync(ProcessStartRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastResult = await inner.RunAsync(request, cancellationToken);
            return LastResult;
        }
    }

    private sealed class FixtureDownloader(string root, string source) : ITelegramFileDownloader
    {
        public async Task<TelegramDownloadResult> DownloadAsync(TelegramMediaInput input, CancellationToken cancellationToken)
        {
            string relative = $"data/intake/{Guid.NewGuid():N}.mp4";
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(source, path);
            await using FileStream stream = File.OpenRead(path);
            return new TelegramDownloadResult.Succeeded(new TelegramDownloadedFile(relative, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)), stream.Length));
        }
    }

    private sealed class CapturingDelivery : IVideoDelivery
    {
        public List<byte[]> Files { get; } = [];
        public async Task<VideoDeliveryResult> SendAsync(VideoDeliveryRequest request, CancellationToken cancellationToken)
        {
            Files.Add(await File.ReadAllBytesAsync(request.FilePath, cancellationToken));
            return VideoDeliveryResult.Succeeded($"message-{Files.Count}");
        }
    }

    private sealed class CapturingResponder : ITelegramResponder
    {
        public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingCreateJob(CreateJobUseCase inner) : ICreateJobUseCase
    {
        public JobId JobId { get; private set; }

        public async Task<CreateJobResult> ExecuteAsync(CreateJobCommand command, CancellationToken cancellationToken)
        {
            CreateJobResult result = await inner.ExecuteAsync(command, cancellationToken);
            if (result is CreateJobResult.Succeeded succeeded)
            {
                JobId = succeeded.JobId;
            }

            return result;
        }
    }

    private sealed class DeterministicPlanProvider : IEditPlanProvider
    {
        public Task<PlanningCostEstimate> EstimateCostAsync(ProviderPlanningRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PlanningCostEstimate(0.001m, "acceptance", "fake-v1"));

        public Task<ProviderPlanResponse> CreatePlanAsync(ProviderPlanningRequest request, CancellationToken cancellationToken)
        {
            ProviderSegment segment = request.AllowedSegments[0];
            int frames = Math.Max(30, (int)Math.Min(600, Math.Round(segment.DurationMicroseconds / 1_000_000d * 30)));
            return Task.FromResult(new ProviderPlanResponse(1, 42,
                [new ProviderVideoClip(segment.Id, new OutputRange(0, frames), CropPreset.CenterCrop)], [], 0.001m));
        }
    }
}

public sealed class AcceptanceFactAttribute : FactAttribute
{
    public AcceptanceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WHISPER_TEST_PYTHON")))
        {
            Skip = "Set ACCEPTANCE_MEDIA_ROOT and WHISPER_TEST_PYTHON for the real local acceptance flow.";
        }
    }
}

public sealed class MediaAcceptanceFactAttribute : FactAttribute
{
    public MediaAcceptanceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")))
        {
            Skip = "Set ACCEPTANCE_MEDIA_ROOT after running scripts/Prepare-AcceptanceMedia.ps1.";
        }
    }
}
