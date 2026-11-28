using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Acceptance;

/// <summary>Итог проверки доказательств приёмки.</summary>
public enum AcceptanceEvidenceOutcome
{
    /// <summary>Все обязательные доказательства согласованы.</summary>
    Passed,
    /// <summary>Задание ещё не достигло терминального состояния.</summary>
    InProgress,
    /// <summary>Задание отсутствует, завершилось неуспешно или имеет несогласованные доказательства.</summary>
    Failed
}

/// <summary>Режим количества исходников, фактически связанный с заданием.</summary>
public enum AcceptanceSourceMode
{
    /// <summary>Один исходный файл.</summary>
    SingleSource,
    /// <summary>Два или более исходных файлов.</summary>
    MultiSource
}

/// <summary>Безопасный результат одной проверки без пользовательских данных.</summary>
/// <param name="Code">Стабильный машинный код проверки.</param>
/// <param name="Passed">Признак успешной проверки.</param>
public sealed record AcceptanceEvidenceCheck(string Code, bool Passed);

/// <summary>Факты о зарегистрированном обязательном артефакте и его файле.</summary>
/// <param name="ArtifactType">Тип артефакта.</param>
/// <param name="ArtifactId">Идентификатор артефакта.</param>
/// <param name="StoredSha256">Зарегистрированный SHA-256 или <see langword="null"/>.</param>
/// <param name="StoredSizeBytes">Зарегистрированный размер в байтах или <see langword="null"/>.</param>
/// <param name="Exists">Существует ли безопасно разрешённый обычный файл.</param>
/// <param name="ActualSha256">Пересчитанный SHA-256 или <see langword="null"/>, если файл недоступен.</param>
/// <param name="ActualSizeBytes">Фактический размер в байтах или <see langword="null"/>, если файл недоступен.</param>
public sealed record AcceptanceArtifactEvidence(
    string ArtifactType,
    string ArtifactId,
    string? StoredSha256,
    long? StoredSizeBytes,
    bool Exists,
    string? ActualSha256,
    long? ActualSizeBytes);

/// <summary>Read-only снимок фактов, полученный из одного согласованного состояния persistence.</summary>
public sealed record AcceptanceEvidenceSnapshot
{
    /// <summary>Идентификатор запрошенного задания.</summary>
    public required JobId JobId { get; init; }
    /// <summary>Признак наличия задания.</summary>
    public bool Found { get; init; } = true;
    /// <summary>Сохранённый статус или <see langword="null"/>, если задание отсутствует.</summary>
    public JobStatus? JobStatus { get; init; } = Domain.Jobs.JobStatus.Completed;
    /// <summary>Сохранённый этап или <see langword="null"/>, если задание отсутствует.</summary>
    public ProductionStage? JobStage { get; init; } = ProductionStage.Delivery;
    /// <summary>Идентификатор активного варианта или <see langword="null"/>.</summary>
    public string? VariantId { get; init; }
    /// <summary>Идентификатор утверждённой ревизии или <see langword="null"/>.</summary>
    public string? ApprovedRevisionId { get; init; }
    /// <summary>Сохранённый hash плана или <see langword="null"/>.</summary>
    public string? PlanHash { get; init; }
    /// <summary>Повторно вычисленный hash плана или <see langword="null"/>.</summary>
    public string? RecomputedPlanHash { get; init; }
    /// <summary>Точный упорядоченный список skill IDs.</summary>
    public IReadOnlyList<string> SkillIds { get; init; } = [];
    /// <summary>Hash аудита из manifest или <see langword="null"/>.</summary>
    public string? AuditHash { get; init; }
    /// <summary>Повторно вычисленный hash аудита или <see langword="null"/>.</summary>
    public string? RecomputedAuditHash { get; init; }
    /// <summary>Признак совпадения Job, Variant и revision во всех документах.</summary>
    public bool CorrelationMatches { get; init; }
    /// <summary>Признак использования Manifest V2.</summary>
    public bool ManifestV2 { get; init; }
    /// <summary>Признак совпадения ссылок manifest с зарегистрированными артефактами.</summary>
    public bool ManifestLinksMatch { get; init; }
    /// <summary>Признак полной связной и принятой critic/remount chain.</summary>
    public bool CriticChainValid { get; init; }
    /// <summary>Признак успешного QA.</summary>
    public bool QaPassed { get; init; }
    /// <summary>Признак успешного FFprobe gate из QA report.</summary>
    public bool MediaProbePassed { get; init; }
    /// <summary>Признак успешного полного decode gate из QA report.</summary>
    public bool FullDecodePassed { get; init; }
    /// <summary>Признак успешных frame assertions из critic report.</summary>
    public bool FrameAssertionsPassed { get; init; }
    /// <summary>Признак успешных audio assertions из QA report.</summary>
    public bool AudioAssertionsPassed { get; init; }
    /// <summary>Признак успешной доставки final artifact.</summary>
    public bool DeliverySucceeded { get; init; }
    /// <summary>Количество подтверждённых доставок final artifact.</summary>
    public int SuccessfulDeliveryCount { get; init; }
    /// <summary>SHA-256 прочитанного upload-потока или <see langword="null"/>.</summary>
    public string? UploadedSha256 { get; init; }
    /// <summary>Размер прочитанного upload-потока в байтах или <see langword="null"/>.</summary>
    public long? UploadedSizeBytes { get; init; }
    /// <summary>Суммарная фактическая стоимость Job в USD или <see langword="null"/>, если она не записана.</summary>
    public decimal? ActualCostUsd { get; init; }
    /// <summary>Признак наличия непустой пользовательской инструкции без раскрытия текста.</summary>
    public bool InstructionPresent { get; init; }
    /// <summary>Признак непрерывного порядка исходников от нуля.</summary>
    public bool SourceOrderValid { get; init; }
    /// <summary>Признак допустимого сохранённого стиля.</summary>
    public bool StyleValid { get; init; }
    /// <summary>Количество упорядоченных исходников.</summary>
    public int SourceCount { get; init; }
    /// <summary>Заявленный режим количества исходников.</summary>
    public AcceptanceSourceMode SourceMode { get; init; }
    /// <summary>Обязательные артефакты, достижимые из Manifest V2.</summary>
    public IReadOnlyList<AcceptanceArtifactEvidence> Artifacts { get; init; } = [];
    /// <summary>Безопасный код ошибки чтения или <see langword="null"/>.</summary>
    public string? ReadErrorCode { get; init; }

    /// <summary>Создаёт минимальный completed-снимок для поведенческих тестов.</summary>
    /// <param name="jobId">Идентификатор задания.</param>
    /// <returns>Completed-снимок; вызывающий код заполняет проверяемые факты.</returns>
    public static AcceptanceEvidenceSnapshot Completed(JobId jobId) => new() { JobId = jobId };
}

/// <summary>Безопасный machine-readable отчёт приёмки.</summary>
/// <param name="SchemaVersion">Версия контракта отчёта.</param>
/// <param name="JobId">Идентификатор задания.</param>
/// <param name="Outcome">Итог проверки.</param>
/// <param name="VariantId">Активный Variant или <see langword="null"/>.</param>
/// <param name="ApprovedRevisionId">Утверждённая ревизия или <see langword="null"/>.</param>
/// <param name="PlanHash">Сохранённый hash плана или <see langword="null"/>.</param>
/// <param name="RecomputedPlanHash">Повторно вычисленный hash плана или <see langword="null"/>.</param>
/// <param name="AuditHash">Hash аудита или <see langword="null"/>.</param>
/// <param name="RecomputedAuditHash">Повторно вычисленный hash аудита или <see langword="null"/>.</param>
/// <param name="SkillIds">Точный список skill IDs без payload решений.</param>
/// <param name="SourceCount">Количество исходников.</param>
/// <param name="SourceMode">Режим количества исходников.</param>
/// <param name="SuccessfulDeliveryCount">Количество подтверждённых доставок.</param>
/// <param name="UploadedSha256">SHA-256 прочитанного upload-потока или <see langword="null"/>.</param>
/// <param name="UploadedSizeBytes">Размер прочитанного upload-потока или <see langword="null"/>.</param>
/// <param name="ActualCostUsd">Суммарная фактическая стоимость Job в USD или <see langword="null"/>.</param>
/// <param name="Artifacts">Безопасные факты об обязательных артефактах без путей.</param>
/// <param name="Checks">Результаты стабильных проверок.</param>
public sealed record AcceptanceEvidenceReport(
    int SchemaVersion,
    JobId JobId,
    AcceptanceEvidenceOutcome Outcome,
    string? VariantId,
    string? ApprovedRevisionId,
    string? PlanHash,
    string? RecomputedPlanHash,
    string? AuditHash,
    string? RecomputedAuditHash,
    IReadOnlyList<string> SkillIds,
    int SourceCount,
    AcceptanceSourceMode SourceMode,
    int SuccessfulDeliveryCount,
    string? UploadedSha256,
    long? UploadedSizeBytes,
    decimal? ActualCostUsd,
    IReadOnlyList<AcceptanceArtifactEvidence> Artifacts,
    IReadOnlyList<AcceptanceEvidenceCheck> Checks);

/// <summary>Читает безопасный снимок acceptance evidence из внешнего хранилища.</summary>
public interface IAcceptanceEvidenceSource
{
    /// <summary>Читает факты задания без приватных полей.</summary>
    /// <param name="jobId">Идентификатор задания.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Снимок; ожидаемые ошибки представлены в его полях.</returns>
    Task<AcceptanceEvidenceSnapshot> ReadAsync(JobId jobId, CancellationToken cancellationToken);
}

/// <summary>Проверяет согласованность полного набора acceptance evidence.</summary>
public sealed class AcceptanceEvidenceCollector
{
    private readonly IAcceptanceEvidenceSource source;
    private static readonly string[] RequiredSkillIds =
    [
        "multi-clip-director", "semantic-story", "hook-builder", "smart-cut", "best-shot",
        "pacing-director", "speech-tightening", "caption-director", "visual-director",
        "smart-reframe", "audio-director"
    ];

    /// <summary>Создаёт collector поверх read-only источника фактов.</summary>
    /// <param name="source">Источник фактов; не может быть <see langword="null"/>.</param>
    public AcceptanceEvidenceCollector(IAcceptanceEvidenceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
    }

    /// <summary>Собирает и проверяет evidence конкретного задания.</summary>
    /// <param name="jobId">Идентификатор задания.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Безопасный отчёт с итогом Passed, InProgress или Failed.</returns>
    public async Task<AcceptanceEvidenceReport> CollectAsync(JobId jobId, CancellationToken cancellationToken)
    {
        AcceptanceEvidenceSnapshot snapshot = await source.ReadAsync(jobId, cancellationToken);
        List<AcceptanceEvidenceCheck> checks = [];
        if (!snapshot.Found)
            return Report(snapshot, AcceptanceEvidenceOutcome.Failed, [new("job.not_found", false)]);
        if (snapshot.ReadErrorCode is not null)
            return Report(snapshot, AcceptanceEvidenceOutcome.Failed, [new(snapshot.ReadErrorCode, false)]);
        if (snapshot.JobStatus is JobStatus.Queued or JobStatus.Running or JobStatus.AwaitingConfirmation)
            return Report(snapshot, AcceptanceEvidenceOutcome.InProgress, [new("job.completed", false)]);

        Add(checks, "job.completed", snapshot.JobStatus == JobStatus.Completed);
        Add(checks, "job.delivery_stage", snapshot.JobStage == ProductionStage.Delivery);
        Add(checks, "correlation.matches", snapshot.CorrelationMatches);
        Add(checks, "plan.hash.matches", HashesMatch(snapshot.PlanHash, snapshot.RecomputedPlanHash));
        Add(checks, "audit.skill_ids.exact", snapshot.SkillIds.SequenceEqual(RequiredSkillIds, StringComparer.Ordinal));
        Add(checks, "audit.hash.matches", HashesMatch(snapshot.AuditHash, snapshot.RecomputedAuditHash));
        Add(checks, "manifest.v2", snapshot.ManifestV2);
        Add(checks, "manifest.links.match", snapshot.ManifestLinksMatch);
        Add(checks, "critic.chain.accepted", snapshot.CriticChainValid);
        Add(checks, "qa.passed", snapshot.QaPassed);
        Add(checks, "media.ffprobe.passed", snapshot.MediaProbePassed);
        Add(checks, "media.full_decode.passed", snapshot.FullDecodePassed);
        Add(checks, "media.frame_assertions.passed", snapshot.FrameAssertionsPassed);
        Add(checks, "media.audio_assertions.passed", snapshot.AudioAssertionsPassed);
        Add(checks, "delivery.succeeded", snapshot.DeliverySucceeded);
        Add(checks, "delivery.succeeded.exactly_one", snapshot.SuccessfulDeliveryCount == 1);
        AcceptanceArtifactEvidence? final = snapshot.Artifacts.LastOrDefault(a => a.ArtifactType == "final-mp4");
        Add(checks, "delivery.upload.sha256.matches", final is not null
            && HashesMatch(final.ActualSha256, snapshot.UploadedSha256));
        Add(checks, "delivery.upload.size.matches", final is not null
            && final.ActualSizeBytes > 0 && final.ActualSizeBytes == snapshot.UploadedSizeBytes);
        Add(checks, "cost.actual.recorded", snapshot.ActualCostUsd >= 0m);
        Add(checks, "intake.instruction.present", snapshot.InstructionPresent);
        Add(checks, "sources.order.valid", snapshot.SourceOrderValid);
        Add(checks, "intake.style.valid", snapshot.StyleValid);
        Add(checks, "sources.mode.matches", snapshot.SourceCount > 0 &&
            (snapshot.SourceMode == AcceptanceSourceMode.SingleSource && snapshot.SourceCount == 1
             || snapshot.SourceMode == AcceptanceSourceMode.MultiSource && snapshot.SourceCount >= 2));

        foreach (AcceptanceArtifactEvidence artifact in snapshot.Artifacts)
        {
            string prefix = $"artifact.{artifact.ArtifactType}";
            Add(checks, $"{prefix}.missing", artifact.Exists);
            Add(checks, $"{prefix}.size", artifact.Exists && artifact.StoredSizeBytes > 0
                && artifact.StoredSizeBytes == artifact.ActualSizeBytes);
            Add(checks, $"{prefix}.sha256", artifact.Exists
                && HashesMatch(artifact.StoredSha256, artifact.ActualSha256));
        }

        Add(checks, "artifact.final-mp4.required", snapshot.Artifacts.Any(a => a.ArtifactType == "final-mp4"));
        Add(checks, "artifact.manifest.required", snapshot.Artifacts.Any(a => a.ArtifactType == "manifest"));
        return Report(snapshot, checks.All(check => check.Passed)
            ? AcceptanceEvidenceOutcome.Passed : AcceptanceEvidenceOutcome.Failed, checks);
    }

    private static void Add(List<AcceptanceEvidenceCheck> checks, string code, bool passed) =>
        checks.Add(new AcceptanceEvidenceCheck(code, passed));

    private static bool HashesMatch(string? expected, string? actual) =>
        !string.IsNullOrWhiteSpace(expected) && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static AcceptanceEvidenceReport Report(
        AcceptanceEvidenceSnapshot snapshot,
        AcceptanceEvidenceOutcome outcome,
        IReadOnlyList<AcceptanceEvidenceCheck> checks) => new(
            2, snapshot.JobId, outcome, snapshot.VariantId, snapshot.ApprovedRevisionId,
            snapshot.PlanHash, snapshot.RecomputedPlanHash, snapshot.AuditHash, snapshot.RecomputedAuditHash,
            snapshot.SkillIds, snapshot.SourceCount, snapshot.SourceMode,
            snapshot.SuccessfulDeliveryCount, snapshot.UploadedSha256, snapshot.UploadedSizeBytes,
            snapshot.ActualCostUsd, snapshot.Artifacts, checks);
}
