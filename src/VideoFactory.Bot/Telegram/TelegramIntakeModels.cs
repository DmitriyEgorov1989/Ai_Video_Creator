using VideoFactory.VideoProduction.Application.Planning;

namespace VideoFactory.Bot.Telegram;

/// <summary>
/// Нормализованное входное событие Telegram без зависимости application-кода от SDK.
/// </summary>
public sealed record TelegramUpdate(
    long UserId,
    long ChatId,
    string? Text,
    TelegramMediaInput? Media)
{
    /// <summary>Идентификатор Telegram update или сообщения, если адаптер его предоставляет.</summary>
    public long UpdateId { get; init; }
}

/// <summary>Нормализованное нажатие inline-кнопки подтверждения стоимости.</summary>
public sealed record TelegramCallbackQuery(
    long UserId,
    long ChatId,
    string Data);

/// <summary>Адаптер обработки решения Telegram-пользователя.</summary>
public interface IPlanningApprovalHandler
{
    /// <summary>Применяет callback подтверждения или отказа.</summary>
    Task<PlanningApprovalResult> HandleAsync(
        TelegramCallbackQuery query,
        CancellationToken cancellationToken);
}

/// <summary>
/// Описание видеофайла, заявленное Telegram до скачивания.
/// </summary>
public sealed record TelegramMediaInput(
    string FileId,
    string? FileUniqueId,
    long? SizeBytes,
    string? MimeType,
    string? FileName);

/// <summary>
/// Описание локально сохранённого Telegram-файла.
/// </summary>
public sealed record TelegramDownloadedFile(
    string RelativePath,
    string Sha256,
    long SizeBytes,
    long DurationMicroseconds = 1);

/// <summary>Скачивает Telegram-файл в локальное хранилище.</summary>
public interface ITelegramFileDownloader
{
    /// <summary>Сохраняет и проверяет файл; возвращает успех либо ожидаемый отказ, никогда не <see langword="null"/>.</summary>
    Task<TelegramDownloadResult> DownloadAsync(
        TelegramMediaInput input,
        string relativePath,
        long reservedBytes,
        Func<long, CancellationToken, Task<long?>> tryExpandReservation,
        CancellationToken cancellationToken);
}

/// <summary>Типизированный результат загрузки без исключений для ожидаемых отказов.</summary>
public abstract record TelegramDownloadResult
{
    /// <summary>Успешно проверенный локальный файл; <paramref name="File"/> не <see langword="null"/>.</summary>
    public sealed record Succeeded(TelegramDownloadedFile File) : TelegramDownloadResult;

    /// <summary>Отказ с безопасным кодом и сообщением; значения не <see langword="null"/>.</summary>
    public sealed record Rejected(string Code, string Message) : TelegramDownloadResult;
}

/// <summary>Отправляет пользователю текстовый ответ.</summary>
public interface ITelegramResponder
{
    /// <summary>Отправляет сообщение в чат.</summary>
    Task SendTextAsync(long chatId, string text, CancellationToken cancellationToken);
}
