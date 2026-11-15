using System.Text.Json.Serialization;

namespace VideoFactory.VideoProduction.Domain.Identifiers;

/// <summary>
/// Представляет типизированный идентификатор задания на производство видео.
/// </summary>
public readonly record struct JobId
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
    /// Создаёт идентификатор задания.
    /// </summary>
    /// <param name="value">Непустое значение идентификатора.</param>
    [JsonConstructor]
    public JobId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Job ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    /// <summary>
    /// Создаёт новый уникальный идентификатор задания.
    /// </summary>
    /// <returns>Новый непустой идентификатор задания.</returns>
    public static JobId New() => new(Guid.NewGuid());

    /// <summary>
    /// Возвращает значение идентификатора без дефисов.
    /// </summary>
    /// <returns>Строковое представление идентификатора в формате <c>N</c>.</returns>
    public override string ToString() => Value.ToString("N");
}
