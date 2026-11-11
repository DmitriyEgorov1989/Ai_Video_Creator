namespace VideoFactory.VideoProduction.Domain.Identifiers;

/// <summary>
/// Представляет типизированный идентификатор сегмента временной шкалы.
/// </summary>
public readonly record struct SegmentId
{
    /// <summary>
    /// Получает значение идентификатора.
    /// </summary>
    public Guid Value { get; }

    /// <summary>
    /// Получает признак отсутствующего значения идентификатора у структуры по умолчанию.
    /// </summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>
    /// Создаёт идентификатор сегмента.
    /// </summary>
    /// <param name="value">Непустое значение идентификатора.</param>
    public SegmentId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Segment ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    /// <summary>
    /// Создаёт новый уникальный идентификатор сегмента.
    /// </summary>
    /// <returns>Новый непустой идентификатор сегмента.</returns>
    public static SegmentId New() => new(Guid.NewGuid());

    /// <summary>
    /// Возвращает значение идентификатора без дефисов.
    /// </summary>
    /// <returns>Строковое представление идентификатора в формате <c>N</c>.</returns>
    public override string ToString() => Value.ToString("N");
}
