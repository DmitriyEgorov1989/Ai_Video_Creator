namespace VideoFactory.VideoProduction.Domain.Timeline;

/// <summary>
/// Представляет полуоткрытый диапазон кадров выходной временной шкалы.
/// </summary>
public readonly record struct OutputRange
{
    /// <summary>
    /// Получает номер начального кадра включительно.
    /// </summary>
    public int StartFrame { get; }

    /// <summary>
    /// Получает номер конечного кадра исключительно.
    /// </summary>
    public int EndFrame { get; }

    /// <summary>
    /// Получает количество кадров в диапазоне.
    /// </summary>
    public int DurationFrames => EndFrame - StartFrame;

    /// <summary>
    /// Создаёт диапазон кадров выходной временной шкалы.
    /// </summary>
    /// <param name="startFrame">Неотрицательный номер начального кадра включительно.</param>
    /// <param name="endFrame">Номер конечного кадра исключительно, превышающий номер начального кадра.</param>
    public OutputRange(int startFrame, int endFrame)
    {
        if (startFrame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startFrame));
        }

        if (endFrame <= startFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(endFrame));
        }

        StartFrame = startFrame;
        EndFrame = endFrame;
    }

    /// <summary>
    /// Определяет, входит ли кадр в текущий полуоткрытый диапазон.
    /// </summary>
    /// <param name="frame">Номер проверяемого кадра.</param>
    /// <returns><see langword="true"/>, если кадр входит в диапазон; иначе <see langword="false"/>.</returns>
    public bool ContainsFrame(int frame) => frame >= StartFrame && frame < EndFrame;

    /// <summary>
    /// Определяет, полностью ли указанный диапазон находится внутри текущего диапазона.
    /// </summary>
    /// <param name="other">Проверяемый диапазон выходных кадров.</param>
    /// <returns><see langword="true"/>, если обе границы проверяемого диапазона находятся в текущем диапазоне; иначе <see langword="false"/>.</returns>
    public bool Contains(OutputRange other) =>
        other.StartFrame >= StartFrame && other.EndFrame <= EndFrame;
}
