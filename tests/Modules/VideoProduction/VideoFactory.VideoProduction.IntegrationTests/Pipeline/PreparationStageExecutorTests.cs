using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Pipeline;
using VideoFactory.VideoProduction.Infrastructure.Media;
using VideoFactory.VideoProduction.Infrastructure.Queue;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using VideoFactory.VideoProduction.Infrastructure.Processes;
using VideoFactory.VideoProduction.IntegrationTests.Media;

namespace VideoFactory.VideoProduction.IntegrationTests.Pipeline;

public sealed class PreparationStageExecutorTests
{
    [Fact]
    public async Task RestartAfterSecondSourceFailureReusesFirstMasterAndCompletesBothSources()
    {
        await using Fixture fixture = await Fixture.CreateAsync(multipleSources: true);
        InterruptSecondSourceBuilder builder = new();

        Assert.True(await fixture.ProcessAsync(mezzanineBuilder: builder));
        Assert.Equal(ProductionStage.Preparation, (await fixture.StateAsync())!.Progress.Stage);
        Assert.Single(await fixture.Artifacts.ListAsync(fixture.Job.JobId, "master-video", CancellationToken.None));

        Assert.True(await fixture.ProcessAsync(mezzanineBuilder: builder));

        IReadOnlyList<ArtifactRecord> masters = await fixture.Artifacts.ListAsync(
            fixture.Job.JobId, "master-video", CancellationToken.None);
        Assert.Equal(ProductionStage.Analysis, (await fixture.StateAsync())!.Progress.Stage);
        Assert.Equal(2, masters.Count);
        Assert.Contains(masters, item => item.ParentArtifactId == fixture.Source.ArtifactId);
        Assert.Contains(masters, item => item.ParentArtifactId == fixture.SecondSource!.ArtifactId);
        Assert.Equal(1, builder.CallsFor("source.mp4"));
        Assert.Equal(2, builder.CallsFor("source-b.mp4"));
    }

    [Fact]
    public async Task PreparationPreservesOriginalAndRegistersMasterBeforeMovingToAnalysis()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.ProcessAsync());

        Assert.Equal(ProductionStage.Analysis, (await fixture.StateAsync())!.Progress.Stage);
        ArtifactRecord master = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(
            fixture.Job.JobId, "master-video", CancellationToken.None));
        Assert.Equal(fixture.Source.ArtifactId, master.ParentArtifactId);
        Assert.StartsWith($"data/jobs/{fixture.Job.JobId}/normalized/", master.RelativePath);
        Assert.Equal(fixture.Source.Sha256, master.Sha256);
        Assert.Equal(4, master.SizeBytes);
        using JsonDocument metadata = JsonDocument.Parse(master.MetadataJson!);
        JsonElement mapping = metadata.RootElement.GetProperty("mapping");
        Assert.Equal(fixture.Source.ArtifactId.ToString(), mapping.GetProperty("originalArtifactId").GetString());
        Assert.Equal(master.ArtifactId.ToString(), mapping.GetProperty("masterArtifactId").GetString());
        Assert.Equal(0, mapping.GetProperty("originalStartMicroseconds").GetInt64());
        Assert.Equal(25_000_000, mapping.GetProperty("originalDurationMicroseconds").GetInt64());
        Assert.Equal(0, mapping.GetProperty("masterStartMicroseconds").GetInt64());
        Assert.Equal(25_000_000, mapping.GetProperty("masterDurationMicroseconds").GetInt64());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(fixture.SourcePath));
        Assert.Equal(fixture.Source, await fixture.Artifacts.GetAsync(fixture.Source.ArtifactId, CancellationToken.None));
    }

    [Fact]
    public async Task RestartAfterArtifactRegistrationReusesMasterWithoutRunningProcessesAgain()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<IOException>(() => fixture.ProcessAsync(new InterruptedTransition(fixture.ConnectionString)));
        Assert.Equal(ProductionStage.Preparation, (await fixture.StateAsync())!.Progress.Stage);
        ArtifactRecord? first = await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None);
        Assert.NotNull(first);
        int calls = fixture.Processes.Calls;

        Assert.True(await fixture.ProcessAsync());

        Assert.Equal(ProductionStage.Analysis, (await fixture.StateAsync())!.Progress.Stage);
        Assert.Equal(first, await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None));
        Assert.Equal(calls, fixture.Processes.Calls);
        Assert.Single(Directory.GetFiles(Path.Combine(fixture.Root, "data", "jobs", fixture.Job.JobId.ToString(), "normalized")));
    }

    [Fact]
    public async Task ChangedOriginalIsRejectedBeforeAnyMediaProcessRuns()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await File.WriteAllBytesAsync(fixture.SourcePath, [4, 3, 2, 1]);

        Assert.True(await fixture.ProcessAsync());

        Assert.Equal(JobStatus.Failed, (await fixture.StateAsync())!.Progress.Status);
        Assert.Equal("preparation.source_changed", (await new SqliteStageAttemptStore(fixture.ConnectionString)
            .GetLatestAsync(fixture.Job.JobId, ProductionStage.Preparation, CancellationToken.None))!.ErrorCode);
        Assert.Equal(0, fixture.Processes.Calls);
        Assert.Null(await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None));
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 1)]
    [InlineData(false, 90, 1)]
    public async Task OnlyVariableFrameRateOrRotationRequiresTranscoding(bool variableFrameRate, int rotation, int expectedTranscodes)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Processes.VariableFrameRate = variableFrameRate;
        fixture.Processes.Rotation = rotation;

        Assert.True(await fixture.ProcessAsync());

        Assert.Equal(ProductionStage.Analysis, (await fixture.StateAsync())!.Progress.Stage);
        Assert.Equal(expectedTranscodes, fixture.Processes.Transcodes);
        Assert.Equal(1, fixture.Processes.Decodes);
        ArtifactRecord master = (await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None))!;
        using JsonDocument metadata = JsonDocument.Parse(master.MetadataJson!);
        Assert.Equal(expectedTranscodes == 1, metadata.RootElement.GetProperty("wasNormalized").GetBoolean());
        Assert.Equal(25_000_000, metadata.RootElement.GetProperty("mapping").GetProperty("masterDurationMicroseconds").GetInt64());
        Assert.False(metadata.RootElement.GetProperty("mediaMetadata").GetProperty("videoStreams")[0].GetProperty("isVariableFrameRate").GetBoolean());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(fixture.SourcePath));
    }

    [Fact]
    public async Task UnsupportedProfileFailsWithoutCreatingMaster()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Processes.VideoCodec = "mpeg4";

        Assert.True(await fixture.ProcessAsync());

        Assert.Equal(JobStatus.Failed, (await fixture.StateAsync())!.Progress.Status);
        StageAttemptRecord attempt = (await new SqliteStageAttemptStore(fixture.ConnectionString)
            .GetLatestAsync(fixture.Job.JobId, ProductionStage.Preparation, CancellationToken.None))!;
        Assert.Equal("media.video_codec_unsupported", attempt.ErrorCode);
        Assert.Equal(0, fixture.Processes.Transcodes);
        Assert.Equal(0, fixture.Processes.Decodes);
        Assert.Null(await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None));
    }

    [Theory]
    [InlineData("probe", "process.exit_code")]
    [InlineData("normalize", "process.timeout")]
    [InlineData("decode", "process.exit_code")]
    [InlineData("normalize", "process.cancelled")]
    public async Task ProcessFailurePreservesDiagnosticsAndCanBeRetried(string operation, string code)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Processes.VariableFrameRate = true;
        fixture.Processes.FailedOperation = operation;
        fixture.Processes.ErrorCode = code;

        Assert.True(await fixture.ProcessAsync());

        Assert.Equal(ProductionStage.Preparation, (await fixture.StateAsync())!.Progress.Stage);
        StageAttemptRecord attempt = (await new SqliteStageAttemptStore(fixture.ConnectionString)
            .GetLatestAsync(fixture.Job.JobId, ProductionStage.Preparation, CancellationToken.None))!;
        Assert.Equal(code, attempt.ErrorCode);
        Assert.Equal("Original process diagnostic", attempt.ErrorMessage);
        Assert.Equal(code == "process.cancelled" ? StageAttemptStatus.Cancelled : StageAttemptStatus.Retryable, attempt.Status);
        Assert.Null(await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None));
        fixture.Processes.FailedOperation = null;

        Assert.True(await fixture.ProcessAsync());
        Assert.Equal(ProductionStage.Analysis, (await fixture.StateAsync())!.Progress.Stage);
    }

    [Fact]
    public async Task HostCancellationReachesProcessAndDoesNotCompletePreparation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource cancellation = new();
        fixture.Processes.CancelOnRun = cancellation;

        Assert.True(await fixture.ProcessAsync(cancellationToken: cancellation.Token));

        Assert.Equal(ProductionStage.Preparation, (await fixture.StateAsync())!.Progress.Stage);
        Assert.True(fixture.Processes.ReceivedCancelableToken);
        Assert.Equal(StageAttemptStatus.Cancelled, (await new SqliteStageAttemptStore(fixture.ConnectionString)
            .GetLatestAsync(fixture.Job.JobId, ProductionStage.Preparation, CancellationToken.None))!.Status);
        Assert.Null(await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None));
    }

    [AcceptanceMediaFact]
    public async Task RealNormalizeCancellationIsRecoverableByANewProcessor()
    {
        string input = Path.Combine(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!, "speech.mp4");
        await using Fixture fixture = await Fixture.CreateAsync(input, rotated: true);
        CancellationAfterNormalizeStartRunner interruptedRunner = new();

        Assert.True(await fixture.ProcessAsync(runner: interruptedRunner));

        Assert.Equal(ProductionStage.Preparation, (await fixture.StateAsync())!.Progress.Stage);
        StageAttemptRecord interruptedAttempt = (await new SqliteStageAttemptStore(fixture.ConnectionString)
            .GetLatestAsync(fixture.Job.JobId, ProductionStage.Preparation, CancellationToken.None))!;
        Assert.Equal(StageAttemptStatus.Cancelled, interruptedAttempt.Status);
        Assert.Equal("process.cancelled", interruptedAttempt.ErrorCode);
        Assert.Null(await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None));
        Assert.Equal(fixture.Source.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourcePath))));

        Assert.True(await fixture.ProcessAsync(runner: new SystemProcessRunner()));

        Assert.Equal(ProductionStage.Analysis, (await fixture.StateAsync())!.Progress.Stage);
        ArtifactRecord master = Assert.IsType<ArtifactRecord>(await fixture.Artifacts.FindLatestAsync(
            fixture.Job.JobId, "master-video", CancellationToken.None));
        Assert.True(master.SizeBytes > 0);
        Assert.Equal(fixture.Source.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourcePath))));
    }

    [Fact]
    public async Task NormalizationThatLeavesVariableFrameRateDoesNotCompletePreparation()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Processes.VariableFrameRate = true;
        fixture.Processes.MasterRemainsVariableFrameRate = true;

        Assert.True(await fixture.ProcessAsync());

        Assert.Equal(ProductionStage.Preparation, (await fixture.StateAsync())!.Progress.Stage);
        Assert.Equal("preparation.master_not_normalized", (await new SqliteStageAttemptStore(fixture.ConnectionString)
            .GetLatestAsync(fixture.Job.JobId, ProductionStage.Preparation, CancellationToken.None))!.ErrorCode);
        Assert.Null(await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None));
    }

    [Fact]
    public async Task InconsistentCachedTimelineIsRebuiltInsteadOfReused()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<IOException>(() => fixture.ProcessAsync(new InterruptedTransition(fixture.ConnectionString)));
        ArtifactRecord first = (await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None))!;
        ArtifactId invalidId = ArtifactId.New();
        string invalidPath = $"data/jobs/{fixture.Job.JobId}/normalized/{invalidId}.mp4";
        File.Copy(Path.Combine(fixture.Root, first.RelativePath), Path.Combine(fixture.Root, invalidPath));
        JsonNode metadata = JsonNode.Parse(first.MetadataJson!)!;
        metadata["mapping"]!["masterArtifactId"] = invalidId.ToString();
        metadata["mapping"]!["masterDurationMicroseconds"] = 10_000_000;
        await fixture.Artifacts.RegisterAsync(first with { ArtifactId = invalidId, RelativePath = invalidPath,
            MetadataJson = metadata.ToJsonString() }, CancellationToken.None);
        int calls = fixture.Processes.Calls;

        Assert.True(await fixture.ProcessAsync());

        Assert.Equal(ProductionStage.Analysis, (await fixture.StateAsync())!.Progress.Stage);
        Assert.NotEqual(invalidId, (await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None))!.ArtifactId);
        Assert.True(fixture.Processes.Calls > calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrChangedMasterIsRebuiltWithoutOverwritingPreviousRevision(bool missing)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<IOException>(() => fixture.ProcessAsync(new InterruptedTransition(fixture.ConnectionString)));
        ArtifactRecord first = (await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None))!;
        string firstPath = Path.Combine(fixture.Root, first.RelativePath);
        if (missing) File.Delete(firstPath);
        else await File.WriteAllBytesAsync(firstPath, [9, 9, 9, 9]);

        Assert.True(await fixture.ProcessAsync());

        Assert.Equal(ProductionStage.Analysis, (await fixture.StateAsync())!.Progress.Stage);
        ArtifactRecord next = (await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None))!;
        Assert.NotEqual(first.ArtifactId, next.ArtifactId);
        Assert.Equal(fixture.Source.Sha256, next.Sha256);
        Assert.Equal(first, await fixture.Artifacts.GetAsync(first.ArtifactId, CancellationToken.None));
        if (!missing) Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(firstPath));
    }

    [Theory]
    [InlineData(false, "storage.insufficient_space", StageAttemptStatus.Retryable)]
    [InlineData(true, "storage.insufficient_space", StageAttemptStatus.Retryable)]
    [InlineData(false, "storage.job_quota_exceeded", StageAttemptStatus.Permanent)]
    [InlineData(true, "storage.job_quota_exceeded", StageAttemptStatus.Permanent)]
    public async Task StorageLimitPreventsMasterPublication(bool normalize, string code, StageAttemptStatus expectedStatus)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Processes.VariableFrameRate = normalize;

        Assert.True(await fixture.ProcessAsync(budget: new RejectedBudget(code)));

        StageAttemptRecord attempt = (await new SqliteStageAttemptStore(fixture.ConnectionString)
            .GetLatestAsync(fixture.Job.JobId, ProductionStage.Preparation, CancellationToken.None))!;
        Assert.Equal(expectedStatus, attempt.Status);
        Assert.Equal(code, attempt.ErrorCode);
        Assert.Equal(ProductionStage.Preparation, (await fixture.StateAsync())!.Progress.Stage);
        Assert.Null(await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None));
        Assert.Equal(0, fixture.Processes.Transcodes);
    }

    [AcceptanceMediaFact]
    public Task AcceptanceSpeechReachesAnalysisWithoutTranscoding() => VerifyAcceptanceMediaAsync(false);

    [AcceptanceMediaFact]
    public Task RotatedAcceptanceSpeechReachesAnalysisWithVerifiedNormalizedMaster() => VerifyAcceptanceMediaAsync(true);

    private static async Task VerifyAcceptanceMediaAsync(bool rotated)
    {
        string input = Path.Combine(Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!, "speech.mp4");
        await using Fixture fixture = await Fixture.CreateAsync(input, rotated);
        SystemProcessRunner runner = new();
        Assert.True(await fixture.ProcessAsync(runner: runner));
        Assert.Equal(ProductionStage.Analysis, (await fixture.StateAsync())!.Progress.Stage);
        ArtifactRecord master = (await fixture.Artifacts.FindLatestAsync(fixture.Job.JobId, "master-video", CancellationToken.None))!;
        Assert.NotNull(master);
        string path = Path.Combine(fixture.Root, master.RelativePath);
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Equal(new FileInfo(path).Length, master.SizeBytes);
        Assert.Equal(master.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        Assert.Equal(fixture.Source.Sha256, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(fixture.SourcePath))));
        MediaProbeResult probe = await new FfprobeMediaProbe(runner, "ffprobe").ProbeAsync(
            new ProbeRequest(path, TimeSpan.FromSeconds(30), 1_048_576), CancellationToken.None);
        Assert.True(probe.IsSuccess, probe.ErrorMessage);
        Assert.InRange(probe.Metadata!.DurationMicroseconds, 25_000_000, 25_100_000);
        var video = Assert.Single(probe.Metadata.VideoStreams);
        Assert.Equal("h264", video.Codec);
        Assert.NotEqual(true, video.IsVariableFrameRate);
        Assert.True(video.RotationDegrees is null or 0);
        Assert.Equal(rotated ? 640 : 360, video.Width);
        Assert.Equal(rotated ? 360 : 640, video.Height);
        Assert.Equal("aac", Assert.Single(probe.Metadata.AudioStreams).Codec);
        ProcessExecutionResult decoded = await runner.RunAsync(new ProcessStartRequest("ffmpeg",
            ["-v", "error", "-xerror", "-nostdin", "-i", path, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"],
            TimeSpan.FromMinutes(1), 1_048_576), CancellationToken.None);
        Assert.True(decoded.IsSuccess, decoded.ErrorMessage);
        using JsonDocument metadata = JsonDocument.Parse(master.MetadataJson!);
        Assert.Equal(rotated, metadata.RootElement.GetProperty("wasNormalized").GetBoolean());
        JsonElement mapping = metadata.RootElement.GetProperty("mapping");
        Assert.Equal(25_000_000, mapping.GetProperty("originalDurationMicroseconds").GetInt64());
        Assert.Equal(probe.Metadata.DurationMicroseconds, mapping.GetProperty("masterDurationMicroseconds").GetInt64());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"preparation-{Guid.NewGuid():N}");
        public string ConnectionString => $"Data Source={Path.Combine(Root, "jobs.db")};Pooling=False";
        public string SourcePath => Path.Combine(Root, "source.mp4");
        public SqliteArtifactStore Artifacts => new(ConnectionString);
        public ArtifactRecord Source { get; private set; } = null!;
        public ArtifactRecord? SecondSource { get; private set; }
        public CreateJobResult.Succeeded Job { get; private set; } = null!;
        public MediaProcesses Processes { get; } = new();

        public static async Task<Fixture> CreateAsync(
            string? sourcePath = null,
            bool rotated = false,
            bool multipleSources = false)
        {
            Fixture fixture = new();
            Directory.CreateDirectory(fixture.Root);
            await new SqliteMigrationRunner(fixture.ConnectionString).InitializeAsync(CancellationToken.None);
            if (sourcePath is null) await File.WriteAllBytesAsync(fixture.SourcePath, [1, 2, 3, 4]);
            else if (!rotated) File.Copy(sourcePath, fixture.SourcePath);
            else
            {
                ProcessExecutionResult remux = await new SystemProcessRunner().RunAsync(new ProcessStartRequest("ffmpeg",
                    ["-v", "error", "-nostdin", "-display_rotation:v:0", "90", "-i", sourcePath, "-map", "0", "-c", "copy", fixture.SourcePath],
                    TimeSpan.FromMinutes(1), 1_048_576), CancellationToken.None);
                Assert.True(remux.IsSuccess, remux.ErrorMessage);
            }
            byte[] content = await File.ReadAllBytesAsync(fixture.SourcePath);
            fixture.Source = new(ArtifactId.New(), "OriginalVideo", "source.mp4",
                Convert.ToHexStringLower(SHA256.HashData(content)), content.Length, "{\"telegram\":{}}", null, "test");
            await fixture.Artifacts.RegisterAsync(fixture.Source, CancellationToken.None);
            if (multipleSources)
            {
                const string secondRelativePath = "source-b.mp4";
                byte[] secondContent = [5, 6, 7, 8];
                await File.WriteAllBytesAsync(Path.Combine(fixture.Root, secondRelativePath), secondContent);
                fixture.SecondSource = new ArtifactRecord(
                    ArtifactId.New(),
                    "OriginalVideo",
                    secondRelativePath,
                    Convert.ToHexStringLower(SHA256.HashData(secondContent)),
                    secondContent.Length,
                    "{\"telegram\":{}}",
                    null,
                    "test");
                await fixture.Artifacts.RegisterAsync(fixture.SecondSource, CancellationToken.None);
            }
            ArtifactId[] sourceIds = fixture.SecondSource is null
                ? [fixture.Source.ArtifactId]
                : [fixture.Source.ArtifactId, fixture.SecondSource.ArtifactId];
            fixture.Job = Assert.IsType<CreateJobResult.Succeeded>(await new CreateJobUseCase(new SqliteJobCreationStore(fixture.ConnectionString))
                .ExecuteAsync(new CreateJobCommand(sourceIds, "Prepare video", new ProductionSettings(null, false, false))));
            fixture.Source = (await fixture.Artifacts.GetAsync(fixture.Source.ArtifactId, CancellationToken.None))!;
            return fixture;
        }

        public Task<JobCreationRecord?> StateAsync() => new SqliteJobStateStore(ConnectionString).GetAsync(Job.JobId, CancellationToken.None);

        public Task<bool> ProcessAsync(IJobStateStore? stateStore = null, CancellationToken cancellationToken = default,
            IProcessRunner? runner = null, IStorageBudget? budget = null, IMezzanineBuilder? mezzanineBuilder = null)
        {
            IJobStageExecutor unsupported = new UnexpectedStage();
            runner ??= Processes;
            FfprobeMediaProbe probe = new(runner, "ffprobe");
            FfmpegDecodeVerifier decode = new(runner, "ffmpeg");
            PreparationStageExecutor preparation = new(Artifacts, probe,
                mezzanineBuilder ?? new FfmpegMezzanineBuilder(runner, probe, decode, "ffmpeg", budget),
                decode, Root, new MediaInputProfile(), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(5), 1_048_576, budget);
            ProductionStageExecutor executor = new(preparation, unsupported, unsupported, unsupported, unsupported);
            SequentialJobProcessor processor = new(new SqliteJobWorkQueue(ConnectionString),
                stateStore ?? new SqliteJobStateStore(ConnectionString), new SqliteStageAttemptStore(ConnectionString), executor);
            return processor.ProcessNextAsync("test", TimeSpan.FromTicks(1), TimeSpan.FromHours(1), 3, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InterruptSecondSourceBuilder : IMezzanineBuilder
    {
        private readonly Dictionary<string, int> calls = [];

        public int CallsFor(string fileName) => calls.GetValueOrDefault(fileName);

        public Task<MezzanineResult> BuildAsync(MezzanineRequest request, CancellationToken cancellationToken)
        {
            string fileName = Path.GetFileName(request.OriginalPath);
            calls[fileName] = CallsFor(fileName) + 1;
            Directory.CreateDirectory(Path.GetDirectoryName(request.MasterPath)!);
            if (fileName == "source-b.mp4" && calls[fileName] == 1)
            {
                return Task.FromResult(MezzanineResult.Failed("process.interrupted", "Synthetic interruption."));
            }

            return Task.FromResult(MezzanineResult.Succeeded(
                request.OriginalPath,
                false,
                new TimelineMapping(
                    request.OriginalArtifactId,
                    request.MasterArtifactId,
                    0,
                    request.OriginalMetadata.DurationMicroseconds,
                    0,
                    request.OriginalMetadata.DurationMicroseconds)));
        }
    }

    private sealed class UnexpectedStage : IJobStageExecutor
    {
        public Task<StageExecutionResult> ExecuteAsync(JobCreationRecord job, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected stage: {job.Progress.Stage}");
    }

    private sealed class MediaProcesses : IProcessRunner
    {
        public int Calls { get; private set; }
        public int Transcodes { get; private set; }
        public int Decodes { get; private set; }
        public bool VariableFrameRate { get; set; }
        public bool MasterRemainsVariableFrameRate { get; set; }
        public int Rotation { get; set; }
        public string VideoCodec { get; set; } = "h264";
        public string? FailedOperation { get; set; }
        public string ErrorCode { get; set; } = "process.exit_code";
        public CancellationTokenSource? CancelOnRun { get; set; }
        public bool ReceivedCancelableToken { get; private set; }

        public async Task<ProcessExecutionResult> RunAsync(ProcessStartRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedCancelableToken = cancellationToken.CanBeCanceled;
            CancelOnRun?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            string operation = request.FileName == "ffprobe" ? "probe" : request.Arguments.Contains("libx264") ? "normalize" : "decode";
            if (operation == "normalize") Transcodes++;
            if (operation == "decode") Decodes++;
            if (operation == FailedOperation)
                return new(ProcessExecutionStatus.NonZeroExitCode, 1, "", "Original process diagnostic", ErrorCode, "Original process diagnostic", false, false);
            if (operation == "normalize") await File.WriteAllBytesAsync(request.Arguments[^1], [5, 6, 7, 8], cancellationToken);
            bool original = request.Arguments[^1].EndsWith("source.mp4", StringComparison.Ordinal);
            string output = request.FileName == "ffprobe" ? $$"""
                {"format":{"format_name":"mov,mp4","duration":"25"},"streams":[
                  {"index":0,"codec_type":"video","codec_name":"{{VideoCodec}}","width":360,"height":640,
                   "avg_frame_rate":"30/1","r_frame_rate":"{{((original ? VariableFrameRate : MasterRemainsVariableFrameRate) ? "60/1" : "30/1")}}","pix_fmt":"yuv420p",
                   "side_data_list":[{"rotation":{{(original ? Rotation : 0)}}}]},
                  {"index":1,"codec_type":"audio","codec_name":"aac","sample_rate":"48000","channels":1}]}
                """ : "";
            return new(ProcessExecutionStatus.Succeeded, 0, output, "", null, null, false, false);
        }
    }

    private sealed class CancellationAfterNormalizeStartRunner : IProcessRunner
    {
        private readonly SystemProcessRunner inner = new();

        public async Task<ProcessExecutionResult> RunAsync(ProcessStartRequest request, CancellationToken cancellationToken)
        {
            if (request.FileName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)
                && request.Arguments.Contains("libx264", StringComparer.Ordinal))
            {
                using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(25));
                return await inner.RunAsync(request, cancellation.Token);
            }

            return await inner.RunAsync(request, cancellationToken);
        }
    }

    private sealed class InterruptedTransition(string connectionString) : IJobStateStore
    {
        public Task<JobCreationRecord?> GetAsync(JobId jobId, CancellationToken cancellationToken) =>
            new SqliteJobStateStore(connectionString).GetAsync(jobId, cancellationToken);

        public Task<JobStateTransitionResult> TryTransitionAsync(JobId jobId, JobStatus expectedStatus,
            ProductionStage expectedStage, JobProgress nextProgress, CancellationToken cancellationToken) =>
            throw new IOException("Simulated stop before persisting the stage transition.");
    }

    private sealed class RejectedBudget(string code) : IStorageBudget
    {
        public Task<StorageBudgetFailure?> CheckAsync(string directory, long additionalBytes, CancellationToken cancellationToken) =>
            Task.FromResult<StorageBudgetFailure?>(new(code, "Storage limit reached."));
    }
}
