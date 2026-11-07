namespace VideoFactory.VideoProduction.Domain.Jobs;

public sealed record ProductionSettings
{
    public int VariantCount { get; }

    public int? TargetDurationSeconds { get; }

    public bool CaptionsEnabled { get; }

    public bool MusicEnabled { get; }

    public ProductionSettings(
        int variantCount,
        int? targetDurationSeconds,
        bool captionsEnabled,
        bool musicEnabled)
    {
        if (variantCount is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(variantCount), "Variant count must be between 1 and 10.");
        }

        if (targetDurationSeconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetDurationSeconds),
                "Target duration must be positive when specified.");
        }

        VariantCount = variantCount;
        TargetDurationSeconds = targetDurationSeconds;
        CaptionsEnabled = captionsEnabled;
        MusicEnabled = musicEnabled;
    }
}
