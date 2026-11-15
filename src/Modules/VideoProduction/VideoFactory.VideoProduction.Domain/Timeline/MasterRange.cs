using System.Text.Json.Serialization;

namespace VideoFactory.VideoProduction.Domain.Timeline;

/// <summary>
/// Представляет полуоткрытый временной диапазон нормализованного master-видео в микросекундах.
/// </summary>
public readonly record struct MasterRange
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
    /// Создаёт временной диапазон нормализованного master-видео.
    /// </summary>
    /// <param name="startMicroseconds">Неотрицательная начальная позиция включительно в микросекундах.</param>
    /// <param name="endMicroseconds">Конечная позиция исключительно в микросекундах, превышающая начальную.</param>
    [JsonConstructor]
    public MasterRange(long startMicroseconds, long endMicroseconds)
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

    /// <summary>
    /// Определяет, полностью ли указанный диапазон находится внутри текущего диапазона.
    /// </summary>
    /// <param name="other">Проверяемый диапазон нормализованного master-видео.</param>
    /// <returns><see langword="true"/>, если обе границы проверяемого диапазона находятся в текущем диапазоне; иначе <see langword="false"/>.</returns>
    public bool Contains(MasterRange other) =>
        other.StartMicroseconds >= StartMicroseconds
        && other.EndMicroseconds <= EndMicroseconds;
}
