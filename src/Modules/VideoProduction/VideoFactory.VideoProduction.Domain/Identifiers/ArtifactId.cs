namespace VideoFactory.VideoProduction.Domain.Identifiers;

/// <summary>
/// Представляет типизированный идентификатор артефакта задания.
/// </summary>
public readonly record struct ArtifactId
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
    /// Создаёт идентификатор артефакта.
    /// </summary>
    /// <param name="value">Непустое значение идентификатора.</param>
    public ArtifactId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Artifact ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    /// <summary>
    /// Создаёт новый уникальный идентификатор артефакта.
    /// </summary>
    /// <returns>Новый непустой идентификатор артефакта.</returns>
    public static ArtifactId New() => new(Guid.NewGuid());

    /// <summary>
    /// Возвращает значение идентификатора без дефисов.
    /// </summary>
    /// <returns>Строковое представление идентификатора в формате <c>N</c>.</returns>
    public override string ToString() => Value.ToString("N");
}
