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

    /// <summary>
    /// Получает минимальный интервал между progress-сообщениями в секундах;
    /// значение должно быть больше нуля.
    /// </summary>
    public int ProgressMessageIntervalSeconds { get; init; } = 5;
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

    /// <summary>
    /// Получает безопасное имя файла SQLite-базы; значение не может быть <see langword="null"/>.
    /// </summary>
    public string DatabaseFileName { get; init; } = "jobs.sqlite";

    /// <summary>Минимальный остаток свободного места после операции в байтах.</summary>
    public long MinimumFreeBytes { get; init; } = 1_073_741_824;

    /// <summary>Максимальный размер файлов одного Job, включая временные, в байтах.</summary>
    public long JobQuotaBytes { get; init; } = 5_368_709_120;

    /// <summary>Срок хранения каталогов успешно завершённых Job в сутках UTC.</summary>
    public int CompletedJobRetentionDays { get; init; } = 30;
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
/// Профиль нормализации исходного звука.
/// </summary>
public sealed class AudioNormalizationOptions
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "AudioNormalization";

    /// <summary>Целевая частота дискретизации в герцах.</summary>
    public int SampleRate { get; init; } = 48_000;

    /// <summary>Целевая схема каналов.</summary>
    public string ChannelLayout { get; init; } = "stereo";

    /// <summary>Целевая громкость в LUFS.</summary>
    public double LoudnessLufs { get; init; } = -16;

    /// <summary>Ограничение true peak в dBTP.</summary>
    public double TruePeakDbtp { get; init; } = -1;
}

/// <summary>
/// Ожидаемый технический профиль итогового MP4.
/// </summary>
public sealed class OutputQualityOptions
{
    /// <summary>Имя секции конфигурации.</summary>
    public const string SectionName = "OutputQuality";

    /// <summary>Ожидаемая ширина видео в пикселях.</summary>
    public int ExpectedWidth { get; init; } = 1080;

    /// <summary>Ожидаемая высота видео в пикселях.</summary>
    public int ExpectedHeight { get; init; } = 1920;

    /// <summary>Числитель ожидаемой частоты кадров.</summary>
    public int ExpectedFrameRateNumerator { get; init; } = 30;

    /// <summary>Знаменатель ожидаемой частоты кадров.</summary>
    public int ExpectedFrameRateDenominator { get; init; } = 1;

    /// <summary>Ожидаемый pixel format или пустое значение для отсутствия ограничения.</summary>
    public string ExpectedPixelFormat { get; init; } = "yuv420p";

    /// <summary>Допустимое расхождение длительности в кадрах.</summary>
    public int DurationToleranceFrames { get; init; } = 1;

    /// <summary>Требуется ли audio stream в итоговом файле.</summary>
    public bool RequireAudio { get; init; } = true;

    /// <summary>Максимальный размер доставки в Telegram в байтах.</summary>
    public long MaxTelegramBytes { get; init; } = 50_000_000;

    /// <summary>Максимальное число повторных encode после превышения лимита.</summary>
    public int MaxDeliveryEncodeAttempts { get; init; } = 1;
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
