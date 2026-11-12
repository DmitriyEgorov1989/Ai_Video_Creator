using Microsoft.Extensions.Options;
using VideoFactory.Bot.Configuration;
using VideoFactory.Bot.Diagnostics;
using VideoFactory.Bot.Logging;
using VideoFactory.Bot.Workers;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Jobs.State;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Stages;
using VideoFactory.VideoProduction.Infrastructure.Queue;
using VideoFactory.VideoProduction.Infrastructure.Storage;

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
        builder.Services.AddSingleton<IJobWorkQueue>(services =>
            new SqliteJobWorkQueue(BuildConnectionString(services)));
        builder.Services.AddSingleton<IJobStateStore>(services =>
            new SqliteJobStateStore(BuildConnectionString(services)));
        builder.Services.AddSingleton<IStageAttemptStore>(services =>
            new SqliteStageAttemptStore(BuildConnectionString(services)));
        builder.Services.AddSingleton<IJobStageExecutor, UnavailableJobStageExecutor>();
        builder.Services.AddSingleton<SequentialJobProcessor>();
        builder.Services.AddHostedService<SequentialJobWorker>();
        builder.Services.AddSingleton(serviceProvider =>
        {
            IHostEnvironment environment = serviceProvider.GetRequiredService<IHostEnvironment>();
            return new EnvironmentDiagnostic(builder.Configuration, environment.ContentRootPath);
        });

        using IHost host = builder.Build();
        ILogger logger = host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("VideoFactory.Startup");

        if (diagnosticMode)
        {
            return RunDiagnostics(host.Services, logger);
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
        _ = services.GetRequiredService<IOptions<AiApiOptions>>().Value;
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
        PathsOptions paths = services.GetRequiredService<IOptions<PathsOptions>>().Value;
        StorageOptions storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        IHostEnvironment environment = services.GetRequiredService<IHostEnvironment>();
        string workspaceRoot = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, paths.WorkspaceRoot));
        string dataRoot = Path.GetFullPath(Path.Combine(workspaceRoot, storage.DataRoot));
        return $"Data Source={Path.Combine(dataRoot, storage.DatabaseFileName)};Cache=Shared";
    }

    private static int RunDiagnostics(IServiceProvider services, ILogger logger)
    {
        EnvironmentDiagnostic diagnostic = services.GetRequiredService<EnvironmentDiagnostic>();
        using IDisposable scope = PipelineLogScope.Begin(logger, stage: "diagnostics");
        DiagnosticReport report = diagnostic.Run();

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
