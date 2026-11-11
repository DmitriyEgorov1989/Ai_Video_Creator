using System.Collections.Immutable;

namespace VideoFactory.VideoProduction.Domain.Templates;

/// <summary>
/// Идентифицирует фиксированный шаблон визуализации монтажного плана.
/// </summary>
public enum TemplateId
{
    /// <summary>
    /// Динамичный шаблон с выразительной подачей.
    /// </summary>
    Dynamic,

    /// <summary>
    /// Лаконичный шаблон с минимальным визуальным оформлением.
    /// </summary>
    Clean,

    /// <summary>
    /// Премиальный шаблон со сдержанным оформлением.
    /// </summary>
    Premium
}

/// <summary>
/// Определяет способ визуального оформления субтитров.
/// </summary>
public enum CaptionPreset
{
    /// <summary>
    /// Не отображает субтитры.
    /// </summary>
    None,

    /// <summary>
    /// Отображает субтитры в минималистичном стиле.
    /// </summary>
    Minimal,

    /// <summary>
    /// Отображает субтитры с визуальным выделением ключевых элементов.
    /// </summary>
    Emphasis
}

/// <summary>
/// Определяет базовую цветовую тему шаблона.
/// </summary>
public enum ColorTheme
{
    /// <summary>
    /// Использует светлое цветовое оформление.
    /// </summary>
    Light,

    /// <summary>
    /// Использует тёмное цветовое оформление.
    /// </summary>
    Dark
}

/// <summary>
/// Содержит параметры визуализации выбранного шаблона.
/// </summary>
/// <param name="Captions">Способ оформления субтитров.</param>
/// <param name="Theme">Цветовая тема шаблона.</param>
/// <param name="Title">Текст заголовка или <see langword="null" />, если заголовок отсутствует.</param>
public sealed record TemplateProps(
    CaptionPreset Captions,
    ColorTheme Theme,
    string? Title);

/// <summary>
/// Описывает возможности фиксированного шаблона визуализации.
/// </summary>
public sealed record TemplateDefinition
{
    /// <summary>
    /// Получает идентификатор шаблона.
    /// </summary>
    /// <value>Определённый идентификатор фиксированного шаблона.</value>
    public TemplateId Id { get; }

    /// <summary>
    /// Получает допустимые способы оформления субтитров.
    /// </summary>
    /// <value>Непустое множество допустимых значений.</value>
    public ImmutableHashSet<CaptionPreset> AllowedCaptionPresets { get; }

    /// <summary>
    /// Получает признак поддержки заголовка шаблоном.
    /// </summary>
    /// <value><see langword="true" />, если шаблон поддерживает заголовок; иначе <see langword="false" />.</value>
    public bool SupportsTitle { get; }

    /// <summary>
    /// Создаёт проверенное описание возможностей шаблона.
    /// </summary>
    /// <param name="id">Идентификатор шаблона.</param>
    /// <param name="allowedCaptionPresets">Ненулевое и непустое множество допустимых способов оформления субтитров.</param>
    /// <param name="supportsTitle">Признак поддержки заголовка шаблоном.</param>
    public TemplateDefinition(
        TemplateId id,
        ImmutableHashSet<CaptionPreset> allowedCaptionPresets,
        bool supportsTitle)
    {
        if (!Enum.IsDefined(id)) throw new ArgumentOutOfRangeException(nameof(id));
        ArgumentNullException.ThrowIfNull(allowedCaptionPresets);
        if (allowedCaptionPresets.Count == 0)
            throw new ArgumentException("At least one caption preset is required.", nameof(allowedCaptionPresets));
        if (allowedCaptionPresets.Any(preset => !Enum.IsDefined(preset)))
            throw new ArgumentException("Caption presets must be defined values.", nameof(allowedCaptionPresets));

        Id = id;
        AllowedCaptionPresets = allowedCaptionPresets;
        SupportsTitle = supportsTitle;
    }
}
