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
        if (!diagnosticMode && args.Any(argument => !argument.StartsWith("-", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("Unknown command. Use 'diagnose' or pass host configuration switches.");
            return 2;
        }

        string[] hostArgs = diagnosticMode ? [] : args;
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(hostArgs);
        ConfigureLogging(builder);
        builder.Services.AddVideoFactoryConfiguration(
            builder.Configuration,
            validateOnStart: !diagnosticMode);
        builder.Services.AddSingleton<IJobSignal, InMemoryJobSignal>();
        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddSingleton<IRenderHostAdapter>(services =>
        {
            RemotionOptions remotion = services.GetRequiredService<IOptions<RemotionOptions>>().Value;
            return new RenderHostProcessAdapter(
                services.GetRequiredService<IProcessRunner>(),
                remotion.NodePath,
                remotion.RenderHostPath);
        });
        builder.Services.AddSingleton<IAnalysisService, AnalysisService>();
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
        builder.Services.AddSingleton<IMezzanineBuilder>(services =>
        {
            FfmpegOptions options = services
                .GetRequiredService<IOptions<FfmpegOptions>>().Value;
            return new FfmpegMezzanineBuilder(
                services.GetRequiredService<IProcessRunner>(),
                services.GetRequiredService<IMediaProbe>(),
                services.GetRequiredService<IMediaDecodeVerifier>(),
                options.FfmpegPath);
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
        builder.Services.AddSingleton<IEditPlanRevisionStore>(services =>
            new SqliteEditPlanRevisionStore(BuildConnectionString(services)));
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
                BuildWorkspaceRoot(services)));
        builder.Services.AddSingleton<ITelegramResponder, TelegramBotResponder>();
        builder.Services.AddSingleton<ICreateJobUseCase>(services =>
            new CreateJobUseCaseAdapter(
                new CreateJobUseCase(
                    services.GetRequiredService<IJobCreationStore>(),
                    services.GetRequiredService<IJobSignal>())));
        builder.Services.AddSingleton<TelegramUpdateHandler>();
        builder.Services.AddHostedService<TelegramPollingService>();
        builder.Services.AddSingleton<IJobStageExecutor>(services =>
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
                services.GetRequiredService<IOptions<AudioNormalizationOptions>>().Value.TruePeakDbtp));
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
