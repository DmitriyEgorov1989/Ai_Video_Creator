using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.TelegramIntake;

/// <summary>Состояние устойчивой Telegram intake session.</summary>
public enum TelegramIntakeSessionStatus
{
    /// <summary>Набор материалов открыт.</summary>
    Open,

    /// <summary>Набор завершён и связан с Job.</summary>
    Completed,

    /// <summary>Набор отменён пользователем.</summary>
    Cancelled
}

/// <summary>Состояние одного Telegram media item.</summary>
public enum TelegramIntakeItemStatus
{
    /// <summary>Слот и байты зарезервированы, загрузка ещё не подтверждена.</summary>
    Pending,

    /// <summary>Файл проверен и зарегистрирован как Original artifact.</summary>
    Accepted,

    /// <summary>Загрузка или проверка завершилась ожидаемой ошибкой.</summary>
    Failed
}

/// <summary>Запрос резервирования одного Telegram media message.</summary>
/// <param name="UserId">Разрешённый Telegram user ID.</param>
/// <param name="ChatId">Telegram chat ID.</param>
/// <param name="MessageId">Telegram message ID, определяющий порядок внутри chat.</param>
/// <param name="FileUniqueId">Telegram file unique ID или <see langword="null"/>, если он отсутствует.</param>
/// <param name="DeclaredSizeBytes">Заявленный положительный размер файла в байтах или <see langword="null"/>.</param>
/// <param name="MaximumFileBytes">Максимально допустимый размер одного файла в байтах.</param>
/// <param name="TemporaryRelativePath">Безопасный относительный путь временной загрузки.</param>
public sealed record IntakeReservation(
    long UserId,
    long ChatId,
    long MessageId,
    string? FileUniqueId,
    long? DeclaredSizeBytes,
    long MaximumFileBytes,
    string TemporaryRelativePath);

/// <summary>Результат резервирования Telegram media item.</summary>
public abstract record IntakeReservationResult
{
    /// <summary>Новый media item зарезервирован.</summary>
    /// <param name="SessionId">Идентификатор открытой intake session.</param>
    /// <param name="ReservedBytes">Зарезервированный объём в байтах.</param>
    public sealed record Reserved(Guid SessionId, long ReservedBytes) : IntakeReservationResult;

    /// <summary>Сообщение или файл уже присутствует в сессии.</summary>
    /// <param name="SessionId">Идентификатор существующей intake session.</param>
    /// <param name="MessageId">Идентификатор существующего сообщения.</param>
    public sealed record Duplicate(Guid SessionId, long MessageId) : IntakeReservationResult;

    /// <summary>Резервирование ожидаемо отклонено.</summary>
    /// <param name="Code">Стабильный код отказа.</param>
    /// <param name="Message">Безопасное диагностическое сообщение.</param>
    public sealed record Rejected(string Code, string Message) : IntakeReservationResult;
}

/// <summary>Подтверждение проверенного Telegram media item.</summary>
/// <param name="SessionId">Идентификатор intake session.</param>
/// <param name="MessageId">Telegram message ID.</param>
/// <param name="ArtifactId">Идентификатор зарегистрированного Original artifact.</param>
/// <param name="Sha256">SHA-256 фактического файла.</param>
/// <param name="SizeBytes">Фактический положительный размер в байтах.</param>
/// <param name="DurationMicroseconds">Положительная проверенная длительность в микросекундах.</param>
public sealed record IntakeAcceptance(
    Guid SessionId,
    long MessageId,
    ArtifactId ArtifactId,
    string Sha256,
    long SizeBytes,
    long DurationMicroseconds);

/// <summary>Результат подтверждения Telegram media item.</summary>
public abstract record IntakeAcceptanceResult
{
    /// <summary>Item успешно подтверждён.</summary>
    public sealed record Accepted : IntakeAcceptanceResult;

    /// <summary>Подтверждение ожидаемо отклонено.</summary>
    /// <param name="Code">Стабильный код отказа.</param>
    /// <param name="Message">Безопасное диагностическое сообщение.</param>
    public sealed record Rejected(string Code, string Message) : IntakeAcceptanceResult;
}

/// <summary>Результат изменения параметров intake session.</summary>
public abstract record IntakeSessionUpdateResult
{
    /// <summary>Параметры session сохранены.</summary>
    /// <param name="Session">Актуальное состояние открытой session.</param>
    public sealed record Updated(TelegramIntakeSession Session) : IntakeSessionUpdateResult;

    /// <summary>Изменение ожидаемо отклонено.</summary>
    /// <param name="Code">Стабильный код отказа.</param>
    /// <param name="Message">Безопасное диагностическое сообщение.</param>
    public sealed record Rejected(string Code, string Message) : IntakeSessionUpdateResult;
}

/// <summary>Результат восстановления незавершённых intake downloads.</summary>
/// <param name="TemporaryRelativePaths">Безопасные относительные пути временных файлов, которые следует удалить.</param>
public sealed record PendingRecoveryResult(ImmutableArray<string> TemporaryRelativePaths);

/// <summary>Запрос явного завершения Telegram intake session.</summary>
/// <param name="UserId">Разрешённый Telegram user ID.</param>
/// <param name="ChatId">Telegram chat ID.</param>
/// <param name="DoneMessageId">Идентификатор сообщения `/done` для идемпотентного receipt.</param>
public sealed record IntakeCompletion(long UserId, long ChatId, long DoneMessageId);

/// <summary>Результат явного завершения Telegram intake session.</summary>
public abstract record IntakeCompletionResult
{
    /// <summary>Новый Job создан.</summary>
    /// <param name="JobId">Идентификатор Job.</param>
    /// <param name="VariantId">Идентификатор единственного Variant.</param>
    public sealed record Created(JobId JobId, VariantId VariantId) : IntakeCompletionResult;

    /// <summary>Идемпотентно найден ранее созданный Job.</summary>
    /// <param name="JobId">Идентификатор Job.</param>
    /// <param name="VariantId">Идентификатор единственного Variant.</param>
    public sealed record Existing(JobId JobId, VariantId VariantId) : IntakeCompletionResult;

    /// <summary>Завершение ожидаемо отклонено.</summary>
    /// <param name="Code">Стабильный код отказа.</param>
    /// <param name="Message">Безопасное диагностическое сообщение.</param>
    public sealed record Rejected(string Code, string Message) : IntakeCompletionResult;
}

/// <summary>Результат отмены открытой Telegram intake session.</summary>
public abstract record IntakeCancellationResult
{
    /// <summary>Открытая session отменена.</summary>
    /// <param name="TemporaryRelativePaths">Безопасные относительные пути всех файлов отменённой session для удаления.</param>
    public sealed record Cancelled(ImmutableArray<string> TemporaryRelativePaths) : IntakeCancellationResult;

    /// <summary>Открытая session уже отсутствовала.</summary>
    /// <param name="TemporaryRelativePaths">Пустой набор временных путей.</param>
    public sealed record AlreadyClosed(ImmutableArray<string> TemporaryRelativePaths) : IntakeCancellationResult;
}

/// <summary>Сохранённый Telegram media item.</summary>
/// <param name="MessageId">Telegram message ID.</param>
/// <param name="FileUniqueId">Telegram file unique ID или <see langword="null"/>.</param>
/// <param name="Status">Текущее состояние item.</param>
/// <param name="ArtifactId">Original artifact ID или <see langword="null"/> до подтверждения.</param>
/// <param name="ReservedBytes">Зарезервированный объём в байтах.</param>
/// <param name="SizeBytes">Фактический размер или <see langword="null"/> до подтверждения.</param>
/// <param name="DurationMicroseconds">Проверенная длительность или <see langword="null"/> до подтверждения.</param>
/// <param name="TemporaryRelativePath">Безопасный относительный путь временной загрузки.</param>
public sealed record TelegramIntakeItem(
    long MessageId,
    string? FileUniqueId,
    TelegramIntakeItemStatus Status,
    ArtifactId? ArtifactId,
    long ReservedBytes,
    long? SizeBytes,
    long? DurationMicroseconds,
    string TemporaryRelativePath);

/// <summary>Устойчивая Telegram intake session.</summary>
/// <param name="SessionId">Стабильный идентификатор session и completion identity.</param>
/// <param name="UserId">Telegram user ID.</param>
/// <param name="ChatId">Telegram chat ID.</param>
/// <param name="Status">Текущее состояние session.</param>
/// <param name="Instruction">Инструкция пользователя или <see langword="null"/>.</param>
/// <param name="RequestedStyle">Запрошенный стиль.</param>
/// <param name="TargetDurationSeconds">Целевая длительность в секундах или <see langword="null"/>.</param>
/// <param name="Items">Items, упорядоченные по Telegram message ID.</param>
/// <param name="JobId">Связанный Job ID или <see langword="null"/> до завершения.</param>
public sealed record TelegramIntakeSession(
    Guid SessionId,
    long UserId,
    long ChatId,
    TelegramIntakeSessionStatus Status,
    string? Instruction,
    RequestedReelsStyle RequestedStyle,
    int? TargetDurationSeconds,
    ImmutableArray<TelegramIntakeItem> Items,
    JobId? JobId);

/// <summary>Сохраняет состояние Telegram intake независимо от Telegram SDK.</summary>
public interface ITelegramIntakeStore
{
    /// <summary>Резервирует слот и байты перед скачиванием media.</summary>
    Task<IntakeReservationResult> ReserveAsync(IntakeReservation request, CancellationToken cancellationToken);

    /// <summary>Подтверждает проверенный и зарегистрированный Original artifact.</summary>
    Task<IntakeAcceptanceResult> AcceptAsync(IntakeAcceptance request, CancellationToken cancellationToken);

    /// <summary>Идемпотентно помечает незавершённую загрузку ошибочной и освобождает reservation.</summary>
    /// <param name="sessionId">Непустой идентификатор intake session.</param>
    /// <param name="messageId">Положительный Telegram message ID.</param>
    /// <param name="errorCode">Непустой стабильный код ожидаемой ошибки.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    Task FailAsync(Guid sessionId, long messageId, string errorCode, CancellationToken cancellationToken);

    /// <summary>Атомарно увеличивает reservation до требуемого размера, если session quota позволяет.</summary>
    /// <returns>Новый лимит в байтах или <see langword="null"/>, если item не Pending либо quota исчерпана.</returns>
    Task<long?> TryExpandReservationAsync(
        Guid sessionId,
        long messageId,
        long requiredBytes,
        CancellationToken cancellationToken);

    /// <summary>Сохраняет непустую пользовательскую инструкцию.</summary>
    Task<IntakeSessionUpdateResult> SetInstructionAsync(
        long userId,
        long chatId,
        string instruction,
        CancellationToken cancellationToken);

    /// <summary>Сохраняет допустимый запрошенный стиль.</summary>
    Task<IntakeSessionUpdateResult> SetStyleAsync(
        long userId,
        long chatId,
        RequestedReelsStyle style,
        CancellationToken cancellationToken);

    /// <summary>Сохраняет целевую длительность от 5 до 60 секунд.</summary>
    Task<IntakeSessionUpdateResult> SetDurationAsync(
        long userId,
        long chatId,
        int durationSeconds,
        CancellationToken cancellationToken);

    /// <summary>Переводит оставшиеся после restart Pending items в Failed и освобождает reservations.</summary>
    Task<PendingRecoveryResult> RecoverPendingAsync(CancellationToken cancellationToken);

    /// <summary>Атомарно завершает session и создаёт либо находит один Job.</summary>
    Task<IntakeCompletionResult> CompleteAsync(
        IntakeCompletion completion,
        CancellationToken cancellationToken);

    /// <summary>Идемпотентно отменяет открытую session и освобождает Pending reservations.</summary>
    Task<IntakeCancellationResult> CancelAsync(
        long userId,
        long chatId,
        CancellationToken cancellationToken);

    /// <summary>Возвращает открытую session или <see langword="null"/>, если её нет.</summary>
    Task<TelegramIntakeSession?> GetOpenAsync(long userId, long chatId, CancellationToken cancellationToken);
}
