using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Jobs.CreateJob;

/// <summary>
/// Представляет входные данные сценария создания одного задания.
/// </summary>
/// <param name="SourceArtifactId">Идентификатор исходного видеоартефакта.</param>
/// <param name="Comment">Комментарий пользователя или <see langword="null"/>.</param>
/// <param name="Settings">Настройки производства или <see langword="null"/>.</param>
public sealed record CreateJobCommand(
    ArtifactId SourceArtifactId,
    string? Comment,
    ProductionSettings? Settings,
    long? TelegramUserId = null);
