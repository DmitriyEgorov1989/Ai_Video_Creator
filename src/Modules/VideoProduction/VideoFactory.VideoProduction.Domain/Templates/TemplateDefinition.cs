using System.Collections.Immutable;

namespace VideoFactory.VideoProduction.Domain.Templates;

public enum TemplateId
{
    Dynamic,
    Clean,
    Premium
}

public enum CaptionPreset
{
    None,
    Minimal,
    Emphasis
}

public enum ColorTheme
{
    Light,
    Dark
}

public sealed record TemplateProps(
    CaptionPreset Captions,
    ColorTheme Theme,
    string? Title);

public sealed record TemplateDefinition
{
    public TemplateId Id { get; }

    public ImmutableHashSet<CaptionPreset> AllowedCaptionPresets { get; }

    public bool SupportsTitle { get; }

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
