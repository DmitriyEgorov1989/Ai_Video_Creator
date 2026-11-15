using System.Text.Json.Serialization;

namespace VideoFactory.VideoProduction.Domain.Identifiers;

/// <summary>
/// Представляет типизированный идентификатор варианта монтажа.
/// </summary>
public readonly record struct VariantId
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
    /// Создаёт идентификатор варианта монтажа.
    /// </summary>
    /// <param name="value">Непустое значение идентификатора.</param>
    [JsonConstructor]
    public VariantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Variant ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    /// <summary>
    /// Создаёт новый уникальный идентификатор варианта монтажа.
    /// </summary>
    /// <returns>Новый непустой идентификатор варианта монтажа.</returns>
    public static VariantId New() => new(Guid.NewGuid());

    /// <summary>
    /// Возвращает значение идентификатора без дефисов.
    /// </summary>
    /// <returns>Строковое представление идентификатора в формате <c>N</c>.</returns>
    public override string ToString() => Value.ToString("N");
}
