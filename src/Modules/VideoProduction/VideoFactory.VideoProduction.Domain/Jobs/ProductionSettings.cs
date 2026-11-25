namespace VideoFactory.VideoProduction.Domain.Jobs;

/// <summary>
/// Определяет темп монтажа в ограниченном MVP-контракте.
/// </summary>
public enum EditTempo
{
    /// <summary>Спокойный темп.</summary>
    Calm,

    /// <summary>Динамичный темп.</summary>
    Dynamic
}

/// <summary>
/// Определяет запрошенный пользователем стиль Reels без зависимости Domain от Application profile catalog.
/// </summary>
public enum RequestedReelsStyle
{
    /// <summary>Детерминированный автоматический выбор по результату анализа.</summary>
    Auto,

    /// <summary>Сдержанный профессиональный стиль.</summary>
    ExpertClean,

    /// <summary>Динамичный стиль с повышенной визуальной плотностью.</summary>
    DynamicViral,

    /// <summary>Кинематографичный стиль.</summary>
    Cinematic,

    /// <summary>Стиль премиальной рекламы.</summary>
    PremiumAds
}

/// <summary>
/// Задаёт пользовательские настройки производства одного видео.
/// </summary>
public sealed record ProductionSettings
{
    /// <summary>
    /// Получает целевую длительность итогового видео.
    /// </summary>
    /// <value>Положительная длительность в секундах или <see langword="null" />, если длительность не задана.</value>
    public int? TargetDurationSeconds { get; }

    /// <summary>
    /// Получает признак необходимости субтитров.
    /// </summary>
    /// <value><see langword="true" />, если субтитры должны быть включены; иначе <see langword="false" />.</value>
    public bool CaptionsEnabled { get; }

    /// <summary>
    /// Получает признак необходимости музыкального сопровождения.
    /// </summary>
    /// <value><see langword="true" />, если музыка должна быть включена; иначе <see langword="false" />.</value>
    public bool MusicEnabled { get; }

    /// <summary>
    /// Получает темп монтажа.
    /// </summary>
    /// <value>Допустимый темп монтажа.</value>
    public EditTempo Tempo { get; }

    /// <summary>
    /// Получает запрошенный стиль Reels.
    /// </summary>
    /// <value>Допустимый стиль; <see cref="RequestedReelsStyle.Auto"/> разрешается после анализа.</value>
    public RequestedReelsStyle RequestedStyle { get; }

    /// <summary>
    /// Создаёт проверенные настройки производства.
    /// </summary>
    /// <param name="targetDurationSeconds">Положительная целевая длительность в секундах или <see langword="null" />, если длительность не задана.</param>
    /// <param name="captionsEnabled">Признак необходимости субтитров.</param>
    /// <param name="musicEnabled">Признак необходимости музыкального сопровождения.</param>
    /// <param name="tempo">Темп монтажа.</param>
    /// <param name="requestedStyle">Запрошенный стиль Reels.</param>
    public ProductionSettings(
        int? targetDurationSeconds,
        bool captionsEnabled,
        bool musicEnabled,
        EditTempo tempo = EditTempo.Dynamic,
        RequestedReelsStyle requestedStyle = RequestedReelsStyle.Auto)
    {
        if (targetDurationSeconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetDurationSeconds),
                "Target duration must be positive when specified.");
        }

        TargetDurationSeconds = targetDurationSeconds;
        CaptionsEnabled = captionsEnabled;
        MusicEnabled = musicEnabled;
        if (!Enum.IsDefined(tempo))
        {
            throw new ArgumentOutOfRangeException(nameof(tempo));
        }

        Tempo = tempo;
        if (!Enum.IsDefined(requestedStyle))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedStyle));
        }

        RequestedStyle = requestedStyle;
    }
}
