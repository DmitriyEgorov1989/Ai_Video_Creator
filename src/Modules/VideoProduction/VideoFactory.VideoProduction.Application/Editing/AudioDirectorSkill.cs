using System.Collections.Immutable;

namespace VideoFactory.VideoProduction.Application.Editing;

/// <summary>Результат Audio Director для речи и опциональной музыки.</summary>
/// <param name="SkillId">Стабильный идентификатор skill.</param>
/// <param name="SkillVersion">Версия алгоритма.</param>
/// <param name="Status">Статус решения.</param>
/// <param name="Reasons">Детерминированные причины решения.</param>
/// <param name="TargetLoudnessLufs">Целевая интегральная громкость речи в LUFS.</param>
/// <param name="TruePeakDbtp">Максимальный true peak в dBTP.</param>
/// <param name="FadeInFrames">Длительность входного fade в кадрах.</param>
/// <param name="FadeOutFrames">Длительность выходного fade в кадрах.</param>
/// <param name="Denoise">Нужно ли применить ограниченный denoise.</param>
/// <param name="MusicAssetId">Разрешённый music asset или <see langword="null"/>.</param>
/// <param name="Ducking">Ослабление музыки от 0 до 1 или <see langword="null"/> без музыки.</param>
public sealed record AudioDirectorDecision(
    string SkillId,
    string SkillVersion,
    EditingDecisionStatus Status,
    ImmutableArray<EditingDecisionReason> Reasons,
    decimal TargetLoudnessLufs,
    decimal TruePeakDbtp,
    int FadeInFrames,
    int FadeOutFrames,
    bool Denoise,
    string? MusicAssetId,
    decimal? Ducking)
    : EditingDecision(SkillId, SkillVersion, Status, Reasons);

/// <summary>Выбирает ограниченную и измеримую audio policy.</summary>
public sealed class AudioDirectorSkill : IEditingSkill
{
    /// <inheritdoc />
    public string Id => "audio-director";

    /// <inheritdoc />
    public string Version => "1.0.0";

    /// <inheritdoc />
    public ImmutableArray<string> Prerequisites => [];

    /// <inheritdoc />
    public EditingDecision Evaluate(EditingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool musicKnown = context.Settings.MusicEnabled
            && !string.IsNullOrWhiteSpace(context.MusicAssetId)
            && context.KnownAssetIds?.Contains(context.MusicAssetId) == true;
        ImmutableArray<EditingDecisionReason> reasons = musicKnown
            ? [new("audio.known_music_ducking", "Ducking references a known music asset.")]
            : [new("audio.speech_only", "Speech normalization is used without unverified music.")];
        return new AudioDirectorDecision(
            Id,
            Version,
            EditingDecisionStatus.Applied,
            reasons,
            TargetLoudnessLufs: -16m,
            TruePeakDbtp: -1m,
            FadeInFrames: context.Style?.FadeFrames ?? 6,
            FadeOutFrames: context.Style?.FadeFrames ?? 6,
            Denoise: context.Style?.Denoise ?? false,
            musicKnown ? context.MusicAssetId : null,
            musicKnown ? 0.5m : null);
    }
}
