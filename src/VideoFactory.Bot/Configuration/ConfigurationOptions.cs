namespace VideoFactory.Bot.Configuration;

/// <summary>
/// Параметры подключения Telegram-бота и ограничения доступа к нему.
/// Экземпляр параметров не должен быть <see langword="null"/>.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>
    /// Имя секции конфигурации Telegram; значение не может быть <see langword="null"/>.
    /// </summary>
    public const string SectionName = "Telegram";

    /// <summary>
    /// Получает токен Telegram-бота; значение не может быть <see langword="null"/>,
    /// а отсутствие настроенного токена представлено пустой строкой.
    /// </summary>
    public string BotToken { get; init; } = string.Empty;

    /// <summary>
    /// Получает идентификатор единственного пользователя Telegram, которому разрешён доступ;
    /// тип значения не допускает <see langword="null"/>.
    /// </summary>
    public long AllowedUserId { get; init; }
}

/// <summary>
/// Параметры путей рабочего окружения приложения.
/// Экземпляр параметров не должен быть <see langword="null"/>.
/// </summary>
public sealed class PathsOptions
{
    /// <summary>
    /// Имя секции конфигурации путей; значение не может быть <see langword="null"/>.
    /// </summary>
    public const string SectionName = "Paths";

    /// <summary>
    /// Получает корневой каталог рабочего окружения; значение не может быть
    /// <see langword="null"/>.
    /// </summary>
    public string WorkspaceRoot { get; init; } = ".";
}

/// <summary>
/// Параметры локального хранилища данных приложения.
/// Экземпляр параметров не должен быть <see langword="null"/>.
/// </summary>
public sealed class StorageOptions
{
    /// <summary>
    /// Имя секции конфигурации хранилища; значение не может быть <see langword="null"/>.
    /// </summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// Получает корневой каталог данных; значение не может быть <see langword="null"/>.
    /// </summary>
    public string DataRoot { get; init; } = "data";
}

/// <summary>
/// Ограничения на входные файлы.
/// Экземпляр параметров не должен быть <see langword="null"/>.
/// </summary>
public sealed class LimitsOptions
{
    /// <summary>
    /// Имя секции конфигурации ограничений; значение не может быть <see langword="null"/>.
    /// </summary>
    public const string SectionName = "Limits";

    /// <summary>
    /// Получает максимально допустимый размер входного файла в байтах;
    /// тип значения не допускает <see langword="null"/>.
    /// </summary>
    public long MaxInputFileBytes { get; init; } = 524_288_000;

    /// <summary>
    /// Получает максимально допустимую длительность входного видео в секундах;
    /// тип значения не допускает <see langword="null"/>.
    /// </summary>
    public int MaxInputDurationSeconds { get; init; } = 1_800;

}

/// <summary>
/// Параметры запуска исполняемых файлов FFmpeg и FFprobe.
/// Экземпляр параметров не должен быть <see langword="null"/>.
/// </summary>
public sealed class FfmpegOptions
{
    /// <summary>
    /// Имя секции конфигурации FFmpeg; значение не может быть <see langword="null"/>.
    /// </summary>
    public const string SectionName = "FFmpeg";

    /// <summary>
    /// Получает путь или имя исполняемого файла FFmpeg; значение не может быть
    /// <see langword="null"/>.
    /// </summary>
    public string FfmpegPath { get; init; } = "ffmpeg";

    /// <summary>
    /// Получает путь или имя исполняемого файла FFprobe; значение не может быть
    /// <see langword="null"/>.
    /// </summary>
    public string FfprobePath { get; init; } = "ffprobe";
}

/// <summary>
/// Параметры запуска Python-обработчика транскрипции Whisper.
/// Экземпляр параметров не должен быть <see langword="null"/>.
/// </summary>
public sealed class WhisperOptions
{
    /// <summary>
    /// Имя секции конфигурации Whisper; значение не может быть <see langword="null"/>.
    /// </summary>
    public const string SectionName = "Whisper";

    /// <summary>
    /// Получает путь или имя исполняемого файла Python; значение не может быть
    /// <see langword="null"/>.
    /// </summary>
    public string PythonPath { get; init; } = "python";

    /// <summary>
    /// Получает путь к Python-обработчику Whisper; значение не может быть
    /// <see langword="null"/>.
    /// </summary>
    public string WorkerPath { get; init; } = "scripts/whisper-worker.py";
}

/// <summary>
/// Параметры запуска Node.js-хоста рендеринга Remotion.
/// Экземпляр параметров не должен быть <see langword="null"/>.
/// </summary>
public sealed class RemotionOptions
{
    /// <summary>
    /// Имя секции конфигурации Remotion; значение не может быть <see langword="null"/>.
    /// </summary>
    public const string SectionName = "Remotion";

    /// <summary>
    /// Получает путь или имя исполняемого файла Node.js; значение не может быть
    /// <see langword="null"/>.
    /// </summary>
    public string NodePath { get; init; } = "node";

    /// <summary>
    /// Получает путь к точке входа хоста рендеринга; значение не может быть
    /// <see langword="null"/>.
    /// </summary>
    public string RenderHostPath { get; init; } = "render-host/dist/index.js";
}

/// <summary>
/// Параметры подключения к внешнему API искусственного интеллекта.
/// Экземпляр параметров не должен быть <see langword="null"/>.
/// </summary>
public sealed class AiApiOptions
{
    /// <summary>
    /// Имя секции конфигурации API искусственного интеллекта; значение не может быть
    /// <see langword="null"/>.
    /// </summary>
    public const string SectionName = "AiApi";

    /// <summary>
    /// Получает идентификатор поставщика API; значение не может быть <see langword="null"/>,
    /// а отсутствие настроенного поставщика представлено пустой строкой.
    /// </summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Получает ключ доступа к API; значение не может быть <see langword="null"/>,
    /// а отсутствие настроенного ключа представлено пустой строкой.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>
    /// Получает идентификатор модели; значение не может быть <see langword="null"/>,
    /// а отсутствие настроенной модели представлено пустой строкой.
    /// </summary>
    public string Model { get; init; } = string.Empty;
}
