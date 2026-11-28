using System.Security.Cryptography;
using Telegram.Bot;
using Telegram.Bot.Types;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Application.Media;

namespace VideoFactory.Bot.Telegram;

/// <summary>
/// Сохраняет Telegram-файлы в рабочем каталоге с безопасным именем.
/// </summary>
public sealed class TelegramFileDownloader : ITelegramFileDownloader
{
    private readonly TelegramBotClient bot;
    private readonly string workspaceRoot;
    private readonly IStorageBudget budget;
    private readonly IMediaProbe probe;
    private readonly MediaInputProfile profile;

    /// <summary>Создаёт downloader Telegram.</summary>
    public TelegramFileDownloader(TelegramBotClient bot, string workspaceRoot, IStorageBudget budget, IMediaProbe probe, MediaInputProfile profile)
    {
        ArgumentNullException.ThrowIfNull(bot);
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot));
        }

        this.bot = bot;
        this.workspaceRoot = Path.GetFullPath(workspaceRoot);
        this.budget = budget;
        this.probe = probe;
        this.profile = profile;
    }

    /// <inheritdoc />
    public async Task<TelegramDownloadResult> DownloadAsync(
        TelegramMediaInput input,
        string relativePath,
        long reservedBytes,
        Func<long, CancellationToken, Task<long?>> tryExpandReservation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.FileId))
        {
            throw new ArgumentException("Telegram file ID cannot be empty.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("Relative path cannot be empty.", nameof(relativePath));
        if (reservedBytes <= 0 || reservedBytes > profile.MaxFileBytes)
            throw new ArgumentOutOfRangeException(nameof(reservedBytes));
        ArgumentNullException.ThrowIfNull(tryExpandReservation);
        string absolutePath = Path.GetFullPath(
            Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relativeToRoot = Path.GetRelativePath(workspaceRoot, absolutePath);
        if (relativeToRoot.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeToRoot))
            throw new ArgumentException("Relative path must remain inside the workspace.", nameof(relativePath));
        string? directory = Path.GetDirectoryName(absolutePath);
        if (directory is null)
        {
            throw new InvalidOperationException("Telegram intake directory could not be resolved.");
        }

        string temporaryPath = absolutePath + ".tmp";
        bool published = false;
        try
        {
            StorageBudgetFailure? capacity = await budget.CheckAsync(directory, reservedBytes, cancellationToken);
            if (capacity is not null) return new TelegramDownloadResult.Rejected(capacity.Code, capacity.Message);
            global::Telegram.Bot.Types.TGFile file = await bot.GetFile(input.FileId, cancellationToken);
            if (string.IsNullOrWhiteSpace(file.FilePath))
                return new TelegramDownloadResult.Rejected("telegram.file_path_missing", "Telegram did not return a file path.");
            Directory.CreateDirectory(directory);
            await using (FileStream output = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using LimitedDownloadStream limited = new(
                    output,
                    reservedBytes,
                    tryExpandReservation,
                    budget,
                    directory);
                await bot.DownloadFile(file, limited, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            MediaProbeResult media = await probe.ProbeAsync(new ProbeRequest(temporaryPath, TimeSpan.FromSeconds(30), 65_536), cancellationToken);
            if (!media.IsSuccess)
                return new TelegramDownloadResult.Rejected(media.ErrorCode ?? "media.probe_failed", "The uploaded file is not a supported video.");
            MediaInputProfileResult validation = new MediaInputProfileValidator().Validate(temporaryPath, media.Metadata!, profile);
            if (!validation.IsSupported)
                return new TelegramDownloadResult.Rejected(validation.ErrorCode!, "The uploaded file is outside the MVP profile.");
            File.Move(temporaryPath, absolutePath);

            await using FileStream stream = new(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            published = true;
            return new TelegramDownloadResult.Succeeded(new TelegramDownloadedFile(
                relativePath,
                Convert.ToHexString(hash).ToLowerInvariant(),
                stream.Length,
                media.Metadata!.DurationMicroseconds));
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or global::Telegram.Bot.Exceptions.RequestException)
        {
            string code = exception is global::Telegram.Bot.Exceptions.ApiRequestException api
                ? $"telegram.api_{api.ErrorCode}" : exception.GetType().Name;
            return new TelegramDownloadResult.Rejected(code, exception.Message);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            if (!published && File.Exists(absolutePath)) File.Delete(absolutePath);
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
            using SHA256 sha256 = SHA256.Create();
            await using CryptoStream upload = new(stream, sha256, CryptoStreamMode.Read, leaveOpen: true);
            Message message = await bot.SendVideo(
                request.RecipientId,
                InputFile.FromStream(upload, Path.GetFileName(request.FilePath)),
                caption: request.Caption,
                cancellationToken: cancellationToken);
            string uploadedSha256 = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
            return VideoDeliveryResult.Succeeded(message.Id.ToString(), uploadedSha256, stream.Position);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            string code = exception is global::Telegram.Bot.Exceptions.ApiRequestException api
                ? $"telegram.api_{api.ErrorCode}" : exception.GetType().Name;
            return VideoDeliveryResult.Failed(code, exception.Message);
        }
    }
}
