using VideoFactory.VideoProduction.Application.TelegramIntake;

namespace VideoFactory.Bot.Telegram;

/// <summary>Восстанавливает прерванные Telegram downloads перед запуском polling.</summary>
public sealed class TelegramIntakeRecoveryService : IHostedService
{
    private readonly ITelegramIntakeStore intakeStore;
    private readonly string workspaceRoot;
    private readonly ILogger<TelegramIntakeRecoveryService>? logger;

    /// <summary>Создаёт startup recovery для указанного рабочего каталога.</summary>
    /// <param name="intakeStore">Устойчивое хранилище intake; не <see langword="null"/>.</param>
    /// <param name="workspaceRoot">Непустой абсолютный или относительный рабочий каталог.</param>
    /// <param name="logger">Logger или <see langword="null"/>, если журналирование не требуется.</param>
    public TelegramIntakeRecoveryService(
        ITelegramIntakeStore intakeStore,
        string workspaceRoot,
        ILogger<TelegramIntakeRecoveryService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(intakeStore);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot));
        this.intakeStore = intakeStore;
        this.workspaceRoot = Path.GetFullPath(workspaceRoot);
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        PendingRecoveryResult recovery = await intakeStore.RecoverPendingAsync(cancellationToken);
        int deleted = 0;
        foreach (string relativePath in recovery.TemporaryRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) continue;
            string absolutePath = Path.GetFullPath(Path.Combine(
                workspaceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string relativeToRoot = Path.GetRelativePath(workspaceRoot, absolutePath);
            if (relativeToRoot.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeToRoot)) continue;
            try
            {
                if (File.Exists(absolutePath))
                {
                    File.Delete(absolutePath);
                    deleted++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning("Failed to delete a recovered Telegram intake temporary file: {ErrorType}", exception.GetType().Name);
            }
        }

        logger?.LogInformation("Telegram intake recovery completed; deleted {DeletedFileCount} temporary files", deleted);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
