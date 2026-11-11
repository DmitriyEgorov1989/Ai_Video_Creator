namespace VideoFactory.VideoProduction.Domain.Jobs;

/// <summary>
/// Определяет режим подготовки вариантов видео.
/// </summary>
public enum ProductionMode
{
    /// <summary>
    /// Запускает сокращённый процесс без согласования брифа и предварительного просмотра.
    /// </summary>
    Fast,

    /// <summary>
    /// Запускает процесс с согласованием брифа и предварительного просмотра.
    /// </summary>
    Director,

    /// <summary>
    /// Откладывает выбор итогового режима до завершения анализа.
    /// </summary>
    Auto
}
