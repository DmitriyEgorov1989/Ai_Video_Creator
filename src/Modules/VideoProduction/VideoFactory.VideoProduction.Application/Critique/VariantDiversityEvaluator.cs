using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Critique;

/// <summary>Фиксирует различия двух вариантов по объективным осям монтажа.</summary>
/// <param name="Hook">Первый сегмент вариантов отличается.</param>
/// <param name="SegmentSet">Набор идентификаторов сегментов отличается без учёта порядка.</param>
/// <param name="SegmentOrder">Последовательность идентификаторов сегментов отличается.</param>
/// <param name="Duration">Полная длительность выходной шкалы в кадрах отличается.</param>
/// <param name="CropProfile">Последовательность способов кадрирования отличается.</param>
public sealed record VariantDiversityDifferences(
    bool Hook,
    bool SegmentSet,
    bool SegmentOrder,
    bool Duration,
    bool CropProfile);

/// <summary>Содержит решение о разнообразии варианта и различия по каждой проверенной оси.</summary>
/// <param name="Differences">Различия по объективным осям; не могут быть <see langword="null"/>.</param>
/// <param name="RequiredDifferenceCount">Минимальное число отличающихся осей для принятия варианта.</param>
public sealed record VariantDiversityResult(
    VariantDiversityDifferences Differences,
    int RequiredDifferenceCount)
{
    /// <summary>Возвращает число объективных осей, по которым варианты отличаются.</summary>
    public int DifferenceCount =>
        (Differences.Hook ? 1 : 0)
        + (Differences.SegmentSet ? 1 : 0)
        + (Differences.SegmentOrder ? 1 : 0)
        + (Differences.Duration ? 1 : 0)
        + (Differences.CropProfile ? 1 : 0);

    /// <summary>Показывает, достигнут ли требуемый уровень разнообразия.</summary>
    public bool IsAccepted => DifferenceCount >= RequiredDifferenceCount;
}

/// <summary>Сравнивает два монтажных плана по объективным признакам разнообразия вариантов.</summary>
public sealed class VariantDiversityEvaluator
{
    private const int AxisCount = 5;
    private readonly int minimumDifferentAxes;

    /// <summary>Создаёт evaluator с заданным минимальным числом отличающихся осей.</summary>
    /// <param name="minimumDifferentAxes">Требуемое число осей от 1 до 5; по умолчанию равно 2.</param>
    public VariantDiversityEvaluator(int minimumDifferentAxes = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumDifferentAxes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumDifferentAxes, AxisCount);
        this.minimumDifferentAxes = minimumDifferentAxes;
    }

    /// <summary>Сравнивает новый вариант с существующим планом, не изменяя их.</summary>
    /// <param name="candidate">Новый монтажный план; не может быть <see langword="null"/>.</param>
    /// <param name="existing">Существующий монтажный план; не может быть <see langword="null"/>.</param>
    /// <returns>Типизированное решение и различия по каждой проверенной оси.</returns>
    public VariantDiversityResult Evaluate(EditPlan candidate, EditPlan existing)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);

        SegmentId[] candidateSegments = [.. candidate.Video.Clips.Select(clip => clip.SegmentId)];
        SegmentId[] existingSegments = [.. existing.Video.Clips.Select(clip => clip.SegmentId)];
        VariantDiversityDifferences differences = new(
            Hook: HooksDiffer(candidateSegments, existingSegments),
            SegmentSet: !candidateSegments.ToHashSet().SetEquals(existingSegments),
            SegmentOrder: !candidateSegments.SequenceEqual(existingSegments),
            Duration: candidate.Output.DurationFrames != existing.Output.DurationFrames,
            CropProfile: !candidate.Video.Clips.Select(clip => clip.Crop)
                .SequenceEqual(existing.Video.Clips.Select(clip => clip.Crop)));

        return new VariantDiversityResult(differences, minimumDifferentAxes);
    }

    private static bool HooksDiffer(SegmentId[] candidate, SegmentId[] existing) =>
        candidate.Length != 0 && existing.Length != 0
            ? candidate[0] != existing[0]
            : candidate.Length != existing.Length;
}
