namespace VideoFactory.VideoProduction.Application.Critique;

/// <summary>Содержит субъективную оценку качества монтажа, не являющуюся объективным дефектом.</summary>
public sealed record SubjectiveCritique
{
    /// <summary>Создаёт субъективную оценку по шкале от 0 до 100 баллов.</summary>
    /// <param name="score">Оценка в баллах включительно от 0 до 100.</param>
    public SubjectiveCritique(int score)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(score, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(score, 100);
        Score = score;
    }

    /// <summary>Возвращает субъективную оценку в баллах включительно от 0 до 100.</summary>
    public int Score { get; }
}

/// <summary>Определяет, нужен ли повторный монтаж по подтверждённым объективным замечаниям.</summary>
public sealed class RemountPolicy
{
    private readonly int maxRemounts;

    /// <summary>Создаёт политику с максимальным числом повторных монтажей.</summary>
    /// <param name="maxRemounts">Допустимое число повторных монтажей; не может быть отрицательным.</param>
    public RemountPolicy(int maxRemounts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxRemounts);
        this.maxRemounts = maxRemounts;
    }

    /// <summary>Определяет необходимость повторного монтажа без превышения установленного лимита.</summary>
    /// <param name="subjectiveCritique">Субъективная оценка; не может быть <see langword="null"/>.</param>
    /// <param name="objectiveFindings">Подтверждённые объективные замечания; не может быть <see langword="null"/>.</param>
    /// <param name="completedRemounts">Число уже выполненных повторных монтажей; не может быть отрицательным.</param>
    /// <returns><see langword="true"/>, если требуется и разрешён повторный монтаж.</returns>
    public bool RequiresRemount(
        SubjectiveCritique subjectiveCritique,
        IReadOnlyCollection<CriticFinding> objectiveFindings,
        int completedRemounts)
    {
        ArgumentNullException.ThrowIfNull(subjectiveCritique);
        ArgumentNullException.ThrowIfNull(objectiveFindings);
        ArgumentOutOfRangeException.ThrowIfNegative(completedRemounts);

        return completedRemounts < maxRemounts
            && objectiveFindings.Any(finding => finding.IsRepairable);
    }
}
