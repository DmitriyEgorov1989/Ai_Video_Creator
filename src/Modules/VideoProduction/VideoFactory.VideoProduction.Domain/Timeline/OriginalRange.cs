namespace VideoFactory.VideoProduction.Domain.Timeline;

/// <summary>
/// Представляет полуоткрытый временной диапазон исходного медиа в микросекундах.
/// </summary>
public readonly record struct OriginalRange
{
    /// <summary>
    /// Получает начальную позицию диапазона включительно в микросекундах.
    /// </summary>
    public long StartMicroseconds { get; }

    /// <summary>
    /// Получает конечную позицию диапазона исключительно в микросекундах.
    /// </summary>
    public long EndMicroseconds { get; }

    /// <summary>
    /// Получает длительность диапазона в микросекундах.
    /// </summary>
    public long DurationMicroseconds => EndMicroseconds - StartMicroseconds;

    /// <summary>
    /// Создаёт временной диапазон исходного медиа.
    /// </summary>
    /// <param name="startMicroseconds">Неотрицательная начальная позиция включительно в микросекундах.</param>
    /// <param name="endMicroseconds">Конечная позиция исключительно в микросекундах, превышающая начальную.</param>
    public OriginalRange(long startMicroseconds, long endMicroseconds)
    {
        if (startMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startMicroseconds));
        }

        if (endMicroseconds <= startMicroseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(endMicroseconds));
        }

        StartMicroseconds = startMicroseconds;
        EndMicroseconds = endMicroseconds;
    }
}
