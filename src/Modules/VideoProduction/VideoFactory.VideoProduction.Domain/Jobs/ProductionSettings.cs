namespace VideoFactory.VideoProduction.Domain.Jobs;

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
    /// Создаёт проверенные настройки производства.
    /// </summary>
    /// <param name="targetDurationSeconds">Положительная целевая длительность в секундах или <see langword="null" />, если длительность не задана.</param>
    /// <param name="captionsEnabled">Признак необходимости субтитров.</param>
    /// <param name="musicEnabled">Признак необходимости музыкального сопровождения.</param>
    public ProductionSettings(
        int? targetDurationSeconds,
        bool captionsEnabled,
        bool musicEnabled)
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
    }
}
