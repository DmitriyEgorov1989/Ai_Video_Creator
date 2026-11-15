using System.Security.Cryptography;
using Telegram.Bot;
using Telegram.Bot.Types;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;

namespace VideoFactory.Bot.Telegram;

/// <summary>
/// Адаптирует application use case создания Job к Telegram intake seam.
/// </summary>
public sealed class CreateJobUseCaseAdapter : ICreateJobUseCase
{
    private readonly CreateJobUseCase useCase;

    /// <summary>Создаёт адаптер для application use case.</summary>
    public CreateJobUseCaseAdapter(CreateJobUseCase useCase)
    {
        ArgumentNullException.ThrowIfNull(useCase);
        this.useCase = useCase;
    }

    /// <inheritdoc />
    public Task<CreateJobResult> ExecuteAsync(
        CreateJobCommand command,
        CancellationToken cancellationToken) =>
        useCase.ExecuteAsync(command, cancellationToken);
}

/// <summary>
/// Сохраняет Telegram-файлы в рабочем каталоге с безопасным именем.
/// </summary>
public sealed class TelegramFileDownloader : ITelegramFileDownloader
{
    private readonly TelegramBotClient bot;
    private readonly string workspaceRoot;

    /// <summary>Создаёт downloader Telegram.</summary>
    public TelegramFileDownloader(TelegramBotClient bot, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot));
        }

        this.bot = bot;
        this.workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <inheritdoc />
    public async Task<TelegramDownloadedFile> DownloadAsync(
        TelegramMediaInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.FileId))
        {
            throw new ArgumentException("Telegram file ID cannot be empty.", nameof(input));
        }

        global::Telegram.Bot.Types.TGFile file = await bot.GetFile(input.FileId, cancellationToken);
        if (string.IsNullOrWhiteSpace(file.FilePath))
        {
            throw new InvalidOperationException("Telegram file path was empty.");
        }

        string relativePath = $"jobs/intake/{Guid.NewGuid():N}.mp4";
        string absolutePath = Path.GetFullPath(
            Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string? directory = Path.GetDirectoryName(absolutePath);
        if (directory is null)
        {
            throw new InvalidOperationException("Telegram intake directory could not be resolved.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await bot.DownloadFile(file, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, absolutePath);

            await using FileStream stream = new(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return new TelegramDownloadedFile(
                relativePath,
                Convert.ToHexString(hash).ToLowerInvariant(),
                stream.Length);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>Отправляет текстовые ответы через Telegram Bot API.</summary>
public sealed class TelegramBotResponder : ITelegramResponder
{
    private readonly TelegramBotClient bot;

    /// <summary>Создаёт responder Telegram.</summary>
    public TelegramBotResponder(TelegramBotClient bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        this.bot = bot;
    }

    /// <inheritdoc />
    public Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken) =>
        bot.SendMessage(chatId, text, cancellationToken: cancellationToken);
}

/// <summary>Доставляет проверенный MP4 через Telegram Bot API.</summary>
public sealed class TelegramVideoDelivery : IVideoDelivery
{
    private readonly TelegramBotClient bot;

    /// <summary>Создаёт Telegram delivery adapter.</summary>
    public TelegramVideoDelivery(TelegramBotClient bot)
    {
        this.bot = bot ?? throw new ArgumentNullException(nameof(bot));
    }

    /// <inheritdoc />
    public async Task<VideoDeliveryResult> SendAsync(
        VideoDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.FilePath))
        {
            return VideoDeliveryResult.Failed("delivery.file_missing", "The delivery file does not exist.");
        }

        try
        {
            await using FileStream stream = new(request.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Message message = await bot.SendVideo(
                request.RecipientId,
                InputFile.FromStream(stream, Path.GetFileName(request.FilePath)),
                caption: request.Caption,
                cancellationToken: cancellationToken);
            return VideoDeliveryResult.Succeeded(message.Id.ToString());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return VideoDeliveryResult.Failed("delivery.telegram_failed", exception.Message);
        }
    }
}
