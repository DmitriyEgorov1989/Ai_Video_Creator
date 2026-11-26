using System.Collections.Immutable;
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
        string secondSource = Path.Combine(mediaRoot!, "similar-take-a.mp4");
        Assert.True(File.Exists(source), $"Acceptance media is missing: {source}");
        Assert.True(File.Exists(secondSource), $"Acceptance media is missing: {secondSource}");
        string repositoryRoot = Fixture.RepositoryRoot();
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "render-host", "dist", "cli.js")), "Build render-host before running this acceptance test.");

        await using Fixture fixture = await Fixture.CreateAsync([source, secondSource], whisperPython!);
        await fixture.SendTelegramInputAsync(restartBeforeDone: true);

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
        Assert.Equal(2, completed.Request.SourceAssetIds.Length);
        ArtifactRecord final = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(fixture.JobId, "final-mp4", CancellationToken.None));
        ArtifactRecord render = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(fixture.JobId, "render-output", CancellationToken.None));
        ArtifactRecord qa = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(fixture.JobId, "qa-report", CancellationToken.None));
        ArtifactRecord manifest = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(fixture.JobId, "manifest", CancellationToken.None));
        EditPlanRevision approvedPlan = Assert.IsType<EditPlanRevision>(await fixture.PlanRevisions.FindApprovedAsync(
            fixture.JobId, completed.VariantId, CancellationToken.None));
        Assert.Single(fixture.Delivery.Files);
        Assert.Equal(2, approvedPlan.Plan.Sources.Length);
        using (JsonDocument renderMetadata = JsonDocument.Parse(render.MetadataJson!))
        {
            Assert.Equal(2, renderMetadata.RootElement.GetProperty("sources").GetArrayLength());
        }

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
        await using Fixture fixture = await Fixture.CreateAsync([source], "python");
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

    [Fact]
    public async Task CancelledTwoSourceIntakeCreatesNoJob()
    {
        string source = Path.GetTempFileName();
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        try
        {
            await using Fixture fixture = await Fixture.CreateAsync([source, source], "python");
            await fixture.SendMediaAsync(0, 10);
            await fixture.SendMediaAsync(1, 20);
            await fixture.SendTextAsync("/cancel", 30);

            Assert.Equal(0, fixture.JobSignal.PublishCount);
            Assert.Null(await fixture.IntakeStore.GetOpenAsync(123456789, 123456789, CancellationToken.None));
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public async Task DuplicateSourceUpdateAndDoneCreateOneJob()
    {
        string source = Path.GetTempFileName();
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        try
        {
            await using Fixture fixture = await Fixture.CreateAsync([source], "python");
            await fixture.SendMediaAsync(0, 10);
            await fixture.SendMediaAsync(0, 10);
            await fixture.SendTextAsync("Собери ролик", 20);
            await fixture.SendTextAsync("/done", 30);
            JobId first = fixture.JobSignal.JobId;
            await fixture.SendTextAsync("/done", 31);

            Assert.False(first.IsEmpty);
            Assert.Equal(first, fixture.JobSignal.JobId);
            Assert.Equal(1, fixture.JobSignal.PublishCount);
            JobCreationRecord job = Assert.IsType<JobCreationRecord>(await fixture.State.GetAsync(first, CancellationToken.None));
            Assert.Single(job.Request.SourceAssetIds);
        }
        finally
        {
            File.Delete(source);
        }
    }

    private static string GetIdentifier(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Object => element.GetProperty("value").GetGuid().ToString("N"),
        _ => throw new Xunit.Sdk.XunitException($"Expected an identifier, received {element.ValueKind}.")
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string root, IReadOnlyList<string> sources, string whisperPython, string connectionString)
        {
            Root = root;
            Sources = sources;
            ConnectionString = connectionString;
            Runner = new RecordingProcessRunner();
            Probe = new FfprobeMediaProbe(Runner, "ffprobe");
            Decode = new FfmpegDecodeVerifier(Runner, "ffmpeg");
            Artifacts = new SqliteArtifactStore(connectionString);
            State = new SqliteJobStateStore(connectionString);
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
            CritiqueStageExecutor critique = new(
                PlanRevisions,
                Artifacts,
                new FfmpegRenderedMediaObserver(Runner, "ffmpeg", TimeSpan.FromMinutes(1), 65_536, 200),
                root,
                2);
            PackagingStageExecutor packaging = new(
                Artifacts, root, 50_000_000, new PermissiveBudget(), null, PlanRevisions, null, null, 0);
            DeliveryStageExecutor delivery = new(Artifacts, Delivery, Receipts, root, 123456789, PlanRevisions);
            ProductionStageExecutor stages = new(preparation, analysis, planning, rendering, critique, verification, packaging, delivery);
            Processor = new SequentialJobProcessor(Queue, State, Attempts, stages);
            JobSignal = new CapturingJobSignal();
            IntakeStore = new SqliteTelegramIntakeStore(connectionString);
            Handler = CreateHandler(IntakeStore);
        }

        public string Root { get; }
        public IReadOnlyList<string> Sources { get; }
        public string ConnectionString { get; }
        public JobId JobId { get; private set; }
        public RecordingProcessRunner Runner { get; }
        public IMediaProbe Probe { get; }
        public IMediaDecodeVerifier Decode { get; }
        public SqliteArtifactStore Artifacts { get; }
        public SqliteJobStateStore State { get; }
        public SqliteJobWorkQueue Queue { get; }
        public SqliteStageAttemptStore Attempts { get; }
        public SqliteEditPlanRevisionStore PlanRevisions { get; }
        public SqlitePlanningAttemptStore PlanningAttempts { get; }
        public SqlitePlanningApprovalStore Approvals { get; }
        public SqliteDeliveryReceiptStore Receipts { get; }
        public LocalArtifactStorage LocalStorage { get; }
        public CapturingDelivery Delivery { get; }
        public SequentialJobProcessor Processor { get; }
        public TelegramUpdateHandler Handler { get; private set; }
        public SqliteTelegramIntakeStore IntakeStore { get; private set; }
        public CapturingJobSignal JobSignal { get; }

        public static async Task<Fixture> CreateAsync(IReadOnlyList<string> sources, string whisperPython)
        {
            string root = Path.Combine(Path.GetTempPath(), $"video-factory-acceptance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            string database = Path.Combine(root, "jobs.sqlite");
            string connectionString = $"Data Source={database};Cache=Shared";
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            return new Fixture(root, sources, whisperPython, connectionString);
        }

        public async Task SendTelegramInputAsync(bool restartBeforeDone = false)
        {
            for (int index = 0; index < Sources.Count; index++)
                await SendMediaAsync(index, 10 + index * 10);
            await SendTextAsync("Сделай динамично", 70);
            await SendTextAsync("/style dynamic-viral", 80);
            Assert.Equal(0, JobSignal.PublishCount);
            if (restartBeforeDone) RestartTelegramAdapter();
            await SendTextAsync("/done", 90);
            JobId = JobSignal.JobId;
        }

        public Task SendMediaAsync(int sourceIndex, long updateId) => Handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, null,
                new TelegramMediaInput($"fixture-file-{sourceIndex}", $"fixture-unique-{sourceIndex}", new FileInfo(Sources[sourceIndex]).Length, "video/mp4", $"fixture-{sourceIndex}.mp4")) { UpdateId = updateId },
            CancellationToken.None);

        public Task SendTextAsync(string text, long updateId) => Handler.HandleAsync(
            new TelegramUpdate(123456789, 123456789, text, null) { UpdateId = updateId },
            CancellationToken.None);

        private void RestartTelegramAdapter()
        {
            IntakeStore = new SqliteTelegramIntakeStore(ConnectionString);
            Handler = CreateHandler(IntakeStore);
        }

        private TelegramUpdateHandler CreateHandler(SqliteTelegramIntakeStore intakeStore) => new(
            new TelegramOptions { AllowedUserId = 123456789 },
            new LimitsOptions { MaxInputFileBytes = 524_288_000, MaxInputDurationSeconds = 1800 },
            new FixtureDownloader(Root, Sources),
            Artifacts,
            intakeStore,
            new CapturingResponder(),
            jobSignal: JobSignal);

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

    private sealed class FixtureDownloader(string root, IReadOnlyList<string> sources) : ITelegramFileDownloader
    {
        public async Task<TelegramDownloadResult> DownloadAsync(
            TelegramMediaInput input,
            string relativePath,
            long reservedBytes,
            Func<long, CancellationToken, Task<long?>> tryExpandReservation,
            CancellationToken cancellationToken)
        {
            string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            int sourceIndex = int.Parse(input.FileId.AsSpan("fixture-file-".Length), System.Globalization.CultureInfo.InvariantCulture);
            File.Copy(sources[sourceIndex], path);
            await using FileStream stream = File.OpenRead(path);
            return new TelegramDownloadResult.Succeeded(new TelegramDownloadedFile(relativePath, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)), stream.Length));
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

    private sealed class CapturingJobSignal : IJobSignal
    {
        public JobId JobId { get; private set; }
        public int PublishCount { get; private set; }

        public ValueTask PublishAsync(JobId jobId, CancellationToken cancellationToken)
        {
            JobId = jobId;
            PublishCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public async IAsyncEnumerable<JobId> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class DeterministicPlanProvider : IEditPlanProvider
    {
        public Task<PlanningCostEstimate> EstimateCostAsync(ProviderPlanningRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PlanningCostEstimate(0.001m, "acceptance", "fake-v1"));

        public Task<ProviderPlanResponse> CreatePlanAsync(ProviderPlanningRequest request, CancellationToken cancellationToken)
        {
            int outputStart = 0;
            ImmutableArray<ProviderVideoClip> clips = [.. request.AllowedSegments.Select(segment =>
            {
                int frames = Math.Max(1, (int)Math.Round(segment.DurationMicroseconds / 1_000_000d * 30));
                ProviderVideoClip clip = new(segment.Id, new OutputRange(outputStart, outputStart + frames), CropPreset.CenterCrop);
                outputStart += frames;
                return clip;
            })];
            return Task.FromResult(new ProviderPlanResponse(1, 42,
                clips, [], 0.001m));
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
