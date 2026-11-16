using Microsoft.Extensions.Options;
using VideoFactory.Bot.Configuration;
using VideoFactory.Bot.Diagnostics;
using VideoFactory.Bot.Logging;
using VideoFactory.Bot.Telegram;
using VideoFactory.Bot.Workers;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Infrastructure.Queue;
using VideoFactory.VideoProduction.Infrastructure.Processes;
using VideoFactory.VideoProduction.Infrastructure.Media;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using VideoFactory.VideoProduction.Infrastructure.Analysis;
using VideoFactory.VideoProduction.Infrastructure.Rendering;
using VideoFactory.VideoProduction.Infrastructure.Pipeline;
using VideoFactory.VideoProduction.Infrastructure.QualityAssurance;
using VideoFactory.VideoProduction.Infrastructure.Packaging;
using VideoFactory.VideoProduction.Infrastructure.Delivery;
using VideoFactory.VideoProduction.Infrastructure.Planning;
using VideoFactory.VideoProduction.Application.Delivery;
using Telegram.Bot;

namespace VideoFactory.Bot;

public static class VideoFactoryHost
{
    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool diagnosticMode = args is ["diagnose"];
        bool cleanupMode = args.Length > 0 && args[0] == "cleanup";
        if (cleanupMode && args.Skip(1).Any(argument => argument != "--apply"
            && !(argument.StartsWith("--", StringComparison.Ordinal) && argument.Contains('='))))
        {
            Console.Error.WriteLine("Use 'cleanup [--apply]' followed by --Section:Key=value configuration switches.");
            return 2;
        }
        if (!diagnosticMode && (cleanupMode ? args.Skip(1) : args).Any(argument => !argument.StartsWith("-", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("Unknown command. Use 'diagnose', 'cleanup [--apply]' or host configuration switches.");
            return 2;
        }

        string[] hostArgs = diagnosticMode ? [] : cleanupMode ? args.Skip(1).Where(arg => arg != "--apply").ToArray() : args;
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(hostArgs);
        string? dotEnvPath = DotEnvLoader.Find(builder.Environment.ContentRootPath);
        if (dotEnvPath is not null)
        {
            DotEnvLoader.Add(builder.Configuration, dotEnvPath);
        }
        ConfigureLogging(builder);
        builder.Services.AddVideoFactoryConfiguration(
            builder.Configuration,
            validateOnStart: !diagnosticMode && !cleanupMode);
        builder.Services.AddSingleton<IJobSignal, InMemoryJobSignal>();
        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddSingleton<IStorageBudget>(services =>
        {
            StorageOptions options = services.GetRequiredService<IOptions<StorageOptions>>().Value;
            return new FileSystemStorageBudget(new AvailableDiskSpace(), options.MinimumFreeBytes, options.JobQuotaBytes, BuildWorkspaceRoot(services));
        });
        builder.Services.AddSingleton<IRenderHostAdapter>(services =>
        {
            RemotionOptions remotion = services.GetRequiredService<IOptions<RemotionOptions>>().Value;
            return new RenderHostProcessAdapter(
                services.GetRequiredService<IProcessRunner>(),
                remotion.NodePath,
                remotion.RenderHostPath);
        });
        builder.Services.AddSingleton<IAnalysisService, AnalysisService>();
        builder.Services.AddSingleton<HttpClient>();
        builder.Services.AddSingleton<IEditPlanProvider>(services =>
        {
            AiApiOptions ai = services.GetRequiredService<IOptions<AiApiOptions>>().Value;
            HttpClient client = services.GetRequiredService<HttpClient>();
            return new DeepSeekEditPlanProvider(client, new DeepSeekProviderOptions
            {
                ApiKey = ai.ApiKey,
                BaseAddress = new Uri(ai.BaseAddress),
                Model = ai.Model,
                InputCacheHitPriceUsdPerMillionTokens = ai.InputCacheHitPriceUsdPerMillionTokens,
                InputCacheMissPriceUsdPerMillionTokens = ai.InputCacheMissPriceUsdPerMillionTokens,
                OutputPriceUsdPerMillionTokens = ai.OutputPriceUsdPerMillionTokens,
                MaxOutputTokens = ai.MaxOutputTokens,
                TimeoutSeconds = ai.TimeoutSeconds
            });
        });
        builder.Services.AddSingleton<IAudioActivityDetector>(services =>
        {
            FfmpegOptions ffmpeg = services.GetRequiredService<IOptions<FfmpegOptions>>().Value;
            ProcessTimeoutOptions timeouts = services.GetRequiredService<IOptions<ProcessTimeoutOptions>>().Value;
            return new FfmpegAudioActivityDetector(
                services.GetRequiredService<IProcessRunner>(),
                ffmpeg.FfmpegPath,
                TimeSpan.FromSeconds(timeouts.DecodeSeconds),
                timeouts.MaxOutputBytes);
        });
        builder.Services.AddSingleton<ISceneBoundaryDetector>(services =>
        {
            FfmpegOptions ffmpeg = services.GetRequiredService<IOptions<FfmpegOptions>>().Value;
            ProcessTimeoutOptions timeouts = services.GetRequiredService<IOptions<ProcessTimeoutOptions>>().Value;
            return new FfmpegSceneBoundaryDetector(
                services.GetRequiredService<IProcessRunner>(),
                ffmpeg.FfmpegPath,
                TimeSpan.FromSeconds(timeouts.DecodeSeconds),
                timeouts.MaxOutputBytes);
        });
        builder.Services.AddSingleton<IWhisperTranscriber>(services =>
        {
            WhisperOptions whisper = services.GetRequiredService<IOptions<WhisperOptions>>().Value;
            ProcessTimeoutOptions timeouts = services.GetRequiredService<IOptions<ProcessTimeoutOptions>>().Value;
            return new WhisperProcessTranscriber(
                services.GetRequiredService<IProcessRunner>(),
                whisper.PythonPath,
                whisper.WorkerPath,
                TimeSpan.FromSeconds(timeouts.NormalizeSeconds),
                timeouts.MaxOutputBytes);
        });
        builder.Services.AddSingleton<IMediaProbe>(services =>
        {
            FfmpegOptions options = services
                .GetRequiredService<IOptions<FfmpegOptions>>().Value;
            return new FfprobeMediaProbe(
                services.GetRequiredService<IProcessRunner>(),
                options.FfprobePath);
        });
        builder.Services.AddSingleton<IMediaDecodeVerifier>(services =>
        {
            FfmpegOptions options = services
                .GetRequiredService<IOptions<FfmpegOptions>>().Value;
            return new FfmpegDecodeVerifier(
                services.GetRequiredService<IProcessRunner>(),
                options.FfmpegPath);
        });
        builder.Services.AddSingleton<IOutputQualityAssurance>(services =>
        {
            ProcessTimeoutOptions timeouts = services
                .GetRequiredService<IOptions<ProcessTimeoutOptions>>().Value;
            return new OutputQualityAssurance(
                services.GetRequiredService<IMediaProbe>(),
                services.GetRequiredService<IMediaDecodeVerifier>(),
                TimeSpan.FromSeconds(timeouts.ProbeSeconds),
                timeouts.MaxOutputBytes);
        });
        builder.Services.AddSingleton<IMezzanineBuilder>(services =>
        {
            FfmpegOptions options = services
                .GetRequiredService<IOptions<FfmpegOptions>>().Value;
            return new FfmpegMezzanineBuilder(
                services.GetRequiredService<IProcessRunner>(),
                services.GetRequiredService<IMediaProbe>(),
                services.GetRequiredService<IMediaDecodeVerifier>(),
                options.FfmpegPath,
                services.GetRequiredService<IStorageBudget>());
        });
        builder.Services.AddSingleton<IAudioStemBuilder>(services =>
        {
            FfmpegOptions options = services
                .GetRequiredService<IOptions<FfmpegOptions>>().Value;
            return new FfmpegAudioStemBuilder(
                services.GetRequiredService<IProcessRunner>(),
                options.FfmpegPath);
        });
        builder.Services.AddSingleton<IJobWorkQueue>(services =>
            new SqliteJobWorkQueue(BuildConnectionString(services)));
        builder.Services.AddSingleton<IJobStateStore>(services =>
            new SqliteJobStateStore(BuildConnectionString(services)));
        builder.Services.AddSingleton<IJobCreationStore>(services =>
            new SqliteJobCreationStore(BuildConnectionString(services)));
        builder.Services.AddSingleton<IStageAttemptStore>(services =>
            new SqliteStageAttemptStore(BuildConnectionString(services)));
        builder.Services.AddSingleton<IArtifactStore>(services =>
            new SqliteArtifactStore(BuildConnectionString(services)));
        builder.Services.AddSingleton<IDeliveryReceiptStore>(services =>
            new SqliteDeliveryReceiptStore(BuildConnectionString(services)));
        builder.Services.AddSingleton<IEditPlanRevisionStore>(services =>
            new SqliteEditPlanRevisionStore(BuildConnectionString(services)));
         builder.Services.AddSingleton<IPlanningAttemptStore>(services =>
             new SqlitePlanningAttemptStore(BuildConnectionString(services)));
         builder.Services.AddSingleton<IPlanningApprovalStore>(services =>
             new SqlitePlanningApprovalStore(BuildConnectionString(services)));
         builder.Services.AddSingleton<IPlanningCostPolicy>(services =>
         {
             PlanningOptions options = services.GetRequiredService<IOptions<PlanningOptions>>().Value;
             return new PlanningCostPolicy(new PlanningCostPolicyOptions(options.JobLimitUsd));
         });
         builder.Services.AddSingleton<PlanningApprovalUseCase>(services =>
             new PlanningApprovalUseCase(
                 services.GetRequiredService<IPlanningApprovalStore>(),
                 services.GetRequiredService<IJobStateStore>(),
                 services.GetRequiredService<IJobSignal>()));
        builder.Services.AddSingleton<ILocalArtifactStorage>(services =>
            new LocalArtifactStorage(BuildWorkspaceRoot(services)));
        builder.Services.AddSingleton<IAnalysisArtifactPublisher, AnalysisArtifactPublisher>();
        builder.Services.AddSingleton<TelegramBotClient>(services =>
            new TelegramBotClient(
                services.GetRequiredService<IOptions<TelegramOptions>>().Value.BotToken));
        builder.Services.AddSingleton<ITelegramIntakeSessionStore, InMemoryTelegramIntakeSessionStore>();
        builder.Services.AddSingleton<ITelegramFileDownloader>(services =>
            new TelegramFileDownloader(
                services.GetRequiredService<TelegramBotClient>(),
                BuildWorkspaceRoot(services),
                services.GetRequiredService<IStorageBudget>(),
                services.GetRequiredService<IMediaProbe>(),
                new MediaInputProfile
                {
                    MaxFileBytes = services.GetRequiredService<IOptions<LimitsOptions>>().Value.MaxInputFileBytes,
                    MaxDurationSeconds = services.GetRequiredService<IOptions<LimitsOptions>>().Value.MaxInputDurationSeconds
                }));
        builder.Services.AddSingleton<ITelegramResponder, TelegramBotResponder>();
        builder.Services.AddSingleton<IVideoDelivery, TelegramVideoDelivery>();
        builder.Services.AddSingleton<ICreateJobUseCase>(services =>
            new CreateJobUseCaseAdapter(
                new CreateJobUseCase(
                    services.GetRequiredService<IJobCreationStore>(),
                    services.GetRequiredService<IJobSignal>())));
        builder.Services.AddSingleton(services => new TelegramUpdateHandler(
            services.GetRequiredService<IOptions<TelegramOptions>>().Value,
            services.GetRequiredService<IOptions<LimitsOptions>>().Value,
            services.GetRequiredService<ITelegramFileDownloader>(),
            services.GetRequiredService<IArtifactStore>(),
            services.GetRequiredService<ICreateJobUseCase>(),
             services.GetRequiredService<ITelegramIntakeSessionStore>(),
             services.GetRequiredService<ITelegramResponder>(),
             services.GetRequiredService<ILogger<TelegramUpdateHandler>>(),
             services.GetRequiredService<PlanningApprovalUseCase>()));
        builder.Services.AddHostedService<TelegramPollingService>();
        builder.Services.AddSingleton<PreparationStageExecutor>(services =>
        {
            ProcessTimeoutOptions timeouts = services.GetRequiredService<IOptions<ProcessTimeoutOptions>>().Value;
            LimitsOptions limits = services.GetRequiredService<IOptions<LimitsOptions>>().Value;
            return new PreparationStageExecutor(
                services.GetRequiredService<IArtifactStore>(),
                services.GetRequiredService<IMediaProbe>(),
                services.GetRequiredService<IMezzanineBuilder>(),
                services.GetRequiredService<IMediaDecodeVerifier>(),
                BuildWorkspaceRoot(services),
                new MediaInputProfile { MaxFileBytes = limits.MaxInputFileBytes, MaxDurationSeconds = limits.MaxInputDurationSeconds },
                TimeSpan.FromSeconds(timeouts.ProbeSeconds),
                TimeSpan.FromSeconds(timeouts.NormalizeSeconds),
                TimeSpan.FromSeconds(timeouts.DecodeSeconds),
                timeouts.MaxOutputBytes,
                services.GetRequiredService<IStorageBudget>());
        });
        builder.Services.AddSingleton<AnalysisStageExecutor>(services =>
            new AnalysisStageExecutor(
                services.GetRequiredService<IAnalysisService>(),
                services.GetRequiredService<IAnalysisArtifactPublisher>(),
                services.GetRequiredService<IArtifactStore>(),
                BuildWorkspaceRoot(services)));
        builder.Services.AddSingleton<RenderingStageExecutor>(services =>
            new RenderingStageExecutor(
                services.GetRequiredService<IEditPlanRevisionStore>(),
                services.GetRequiredService<IArtifactStore>(),
                services.GetRequiredService<IRenderHostAdapter>(),
                services.GetRequiredService<IAudioStemBuilder>(),
                services.GetRequiredService<IMediaProbe>(),
                BuildWorkspaceRoot(services),
                TimeSpan.FromSeconds(services.GetRequiredService<IOptions<ProcessTimeoutOptions>>().Value.RenderSeconds),
                services.GetRequiredService<IOptions<ProcessTimeoutOptions>>().Value.MaxOutputBytes,
                services.GetRequiredService<IOptions<AudioNormalizationOptions>>().Value.SampleRate,
                services.GetRequiredService<IOptions<AudioNormalizationOptions>>().Value.ChannelLayout,
                services.GetRequiredService<IOptions<AudioNormalizationOptions>>().Value.LoudnessLufs,
                 services.GetRequiredService<IOptions<AudioNormalizationOptions>>().Value.TruePeakDbtp,
                 services.GetRequiredService<IStorageBudget>()));
        builder.Services.AddSingleton<VerificationStageExecutor>(services =>
        {
            OutputQualityOptions options = services.GetRequiredService<IOptions<OutputQualityOptions>>().Value;
            return new VerificationStageExecutor(
                services.GetRequiredService<IEditPlanRevisionStore>(),
                services.GetRequiredService<IArtifactStore>(),
                services.GetRequiredService<IOutputQualityAssurance>(),
                BuildWorkspaceRoot(services),
                new OutputQualityOptionsProfile(
                    options.ExpectedWidth,
                    options.ExpectedHeight,
                    options.DurationToleranceFrames,
                    options.RequireAudio,
                    string.IsNullOrWhiteSpace(options.ExpectedPixelFormat) ? null : options.ExpectedPixelFormat,
                    options.MaxTelegramBytes));
        });
        builder.Services.AddSingleton<PackagingStageExecutor>(services =>
            new PackagingStageExecutor(
                services.GetRequiredService<IArtifactStore>(),
                BuildWorkspaceRoot(services),
                services.GetRequiredService<IOptions<OutputQualityOptions>>().Value.MaxTelegramBytes,
                services.GetRequiredService<IStorageBudget>()));
        builder.Services.AddSingleton<DeliveryStageExecutor>(services =>
        {
            TelegramOptions telegram = services.GetRequiredService<IOptions<TelegramOptions>>().Value;
            return new DeliveryStageExecutor(
                services.GetRequiredService<IArtifactStore>(),
                services.GetRequiredService<IVideoDelivery>(),
                services.GetRequiredService<IDeliveryReceiptStore>(),
                BuildWorkspaceRoot(services),
                telegram.AllowedUserId);
        });
        builder.Services.AddSingleton<IJobStageExecutor>(services =>
            new ProductionStageExecutor(
                services.GetRequiredService<PreparationStageExecutor>(),
                services.GetRequiredService<AnalysisStageExecutor>(),
                services.GetRequiredService<RenderingStageExecutor>(),
                services.GetRequiredService<VerificationStageExecutor>(),
                services.GetRequiredService<PackagingStageExecutor>(),
                services.GetRequiredService<DeliveryStageExecutor>()));
        builder.Services.AddSingleton<SequentialJobProcessor>();
        builder.Services.AddHostedService<SequentialJobWorker>();
        builder.Services.AddSingleton(serviceProvider =>
        {
            IHostEnvironment environment = serviceProvider.GetRequiredService<IHostEnvironment>();
            return new EnvironmentDiagnostic(
                builder.Configuration,
                environment.ContentRootPath,
                serviceProvider.GetRequiredService<IProcessRunner>());
        });

        using IHost host = builder.Build();
        ILogger logger = host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("VideoFactory.Startup");

        if (diagnosticMode)
        {
             return await RunDiagnosticsAsync(host.Services, logger, cancellationToken);
        }

        if (cleanupMode)
        {
            try
            {
                StorageOptions storage = host.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
                string root = BuildWorkspaceRoot(host.Services);
                string database = Path.Combine(root, storage.DataRoot, storage.DatabaseFileName);
                if (!File.Exists(database))
                {
                    logger.LogError("Cleanup requires an existing migrated database.");
                    return 1;
                }
                IJobCleanup cleanup = new SqliteJobCleanup(BuildConnectionString(host.Services), root,
                    storage.CompletedJobRetentionDays, TimeProvider.System);
                CleanupReport report = await cleanup.RunAsync(args.Contains("--apply", StringComparer.Ordinal), cancellationToken);
                logger.LogInformation("Cleanup: apply={Apply}, candidates={Candidates}, deleted={Deleted}, errors={Errors}, freedBytes={FreedBytes}",
                    args.Contains("--apply", StringComparer.Ordinal), report.Candidates, report.Deleted, report.Errors, report.FreedBytes);
                return report.Errors == 0 ? 0 : 1;
            }
            catch (OptionsValidationException) { return 2; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                logger.LogError("Cleanup failed: {ErrorType}", exception.GetType().Name);
                return 1;
            }
        }

        try
        {
            ValidateConfiguration(host.Services);
            await InitializeStorageAsync(host.Services, cancellationToken);
            await host.RunAsync(cancellationToken);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (OptionsValidationException exception)
        {
            foreach (string failure in exception.Failures)
            {
                logger.LogCritical("Invalid configuration: {Failure}", failure);
            }

            return 2;
        }
    }

    private static void ValidateConfiguration(IServiceProvider services)
    {
        _ = services.GetRequiredService<IOptions<TelegramOptions>>().Value;
        _ = services.GetRequiredService<IOptions<PathsOptions>>().Value;
        _ = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        _ = services.GetRequiredService<IOptions<LimitsOptions>>().Value;
        _ = services.GetRequiredService<IOptions<WorkerOptions>>().Value;
        _ = services.GetRequiredService<IOptions<FfmpegOptions>>().Value;
        _ = services.GetRequiredService<IOptions<WhisperOptions>>().Value;
        _ = services.GetRequiredService<IOptions<RemotionOptions>>().Value;
        _ = services.GetRequiredService<IOptions<AudioNormalizationOptions>>().Value;
         _ = services.GetRequiredService<IOptions<AiApiOptions>>().Value;
         _ = services.GetRequiredService<IOptions<PlanningOptions>>().Value;
        _ = services.GetRequiredService<IOptions<ProcessTimeoutOptions>>().Value;
    }

    private static async Task InitializeStorageAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        PathsOptions paths = services.GetRequiredService<IOptions<PathsOptions>>().Value;
        StorageOptions storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        IHostEnvironment environment = services.GetRequiredService<IHostEnvironment>();
        string workspaceRoot = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, paths.WorkspaceRoot));
        string dataRoot = Path.GetFullPath(Path.Combine(workspaceRoot, storage.DataRoot));
        Directory.CreateDirectory(dataRoot);
        string databasePath = Path.Combine(dataRoot, storage.DatabaseFileName);
        string connectionString = $"Data Source={databasePath};Cache=Shared";
        await new SqliteMigrationRunner(connectionString).InitializeAsync(cancellationToken);
    }

    private static string BuildConnectionString(IServiceProvider services)
    {
        StorageOptions storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        string workspaceRoot = BuildWorkspaceRoot(services);
        string dataRoot = Path.GetFullPath(Path.Combine(workspaceRoot, storage.DataRoot));
        return $"Data Source={Path.Combine(dataRoot, storage.DatabaseFileName)};Cache=Shared";
    }

    private static string BuildWorkspaceRoot(IServiceProvider services)
    {
        PathsOptions paths = services.GetRequiredService<IOptions<PathsOptions>>().Value;
        IHostEnvironment environment = services.GetRequiredService<IHostEnvironment>();
        return Path.GetFullPath(Path.Combine(environment.ContentRootPath, paths.WorkspaceRoot));
    }

    private static async Task<int> RunDiagnosticsAsync(
        IServiceProvider services,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        EnvironmentDiagnostic diagnostic = services.GetRequiredService<EnvironmentDiagnostic>();
        using IDisposable scope = PipelineLogScope.Begin(logger, stage: "diagnostics");
         DiagnosticReport report = await diagnostic.RunAsync(cancellationToken);

        foreach (DiagnosticCheck check in report.Checks)
        {
            LogDiagnosticCheck(logger, check);
        }

        logger.LogInformation(
            "Environment diagnostics completed with {FailedCount} failed checks and {WarningCount} warnings",
            report.Checks.Count(check => check.Status == DiagnosticStatus.Failed),
            report.Checks.Count(check => check.Status == DiagnosticStatus.Warning));

        return report.IsSuccessful ? 0 : 1;
    }

    private static void LogDiagnosticCheck(ILogger logger, DiagnosticCheck check)
    {
        LogLevel level = check.Status switch
        {
            DiagnosticStatus.Passed => LogLevel.Information,
            DiagnosticStatus.Warning => LogLevel.Warning,
            DiagnosticStatus.Failed => LogLevel.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(check), check.Status, "Unknown diagnostic status.")
        };

        logger.Log(
            level,
            "Environment check {CheckName} has status {Status}: {DiagnosticMessage}",
            check.Name,
            check.Status,
            check.Message);
    }

    private static void ConfigureLogging(HostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffK";
            options.UseUtcTimestamp = true;
        });
    }
}
