namespace VideoFactory.Bot.Configuration;

/// <summary>
/// Настройки последовательной фоновой обработки Job.
/// </summary>
public sealed class WorkerOptions
{
    /// <summary>Имя секции настроек.</summary>
    public const string SectionName = "Worker";

    /// <summary>Период heartbeat активного Job в секундах.</summary>
    public int HeartbeatIntervalSeconds { get; init; } = 10;

    /// <summary>Возраст heartbeat в секундах для восстановления Job.</summary>
    public int LeaseTimeoutSeconds { get; init; } = 60;

    /// <summary>Максимальное количество попыток одного этапа.</summary>
    public int MaxStageAttempts { get; init; } = 3;

    /// <summary>Период резервного поиска Job в SQLite в секундах.</summary>
    public int PollIntervalSeconds { get; init; } = 5;
}
