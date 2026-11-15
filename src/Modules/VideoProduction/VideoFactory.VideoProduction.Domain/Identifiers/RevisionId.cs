using System.Text.Json.Serialization;

namespace VideoFactory.VideoProduction.Domain.Identifiers;

/// <summary>
/// Представляет типизированный идентификатор ревизии артефакта или плана.
/// </summary>
public readonly record struct RevisionId
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
    /// Создаёт идентификатор ревизии.
    /// </summary>
    /// <param name="value">Непустое значение идентификатора.</param>
    [JsonConstructor]
    public RevisionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Revision ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    /// <summary>
    /// Создаёт новый уникальный идентификатор ревизии.
    /// </summary>
    /// <returns>Новый непустой идентификатор ревизии.</returns>
    public static RevisionId New() => new(Guid.NewGuid());

    /// <summary>
    /// Возвращает значение идентификатора без дефисов.
    /// </summary>
    /// <returns>Строковое представление идентификатора в формате <c>N</c>.</returns>
    public override string ToString() => Value.ToString("N");
}
