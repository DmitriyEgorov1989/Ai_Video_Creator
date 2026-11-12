namespace VideoFactory.VideoProduction.Application.Jobs.CreateJob;

/// <summary>
/// Определяет ожидаемую причину отказа при создании задания.
/// </summary>
public enum CreateJobErrorCode
{
    /// <summary>
    /// Идентификатор исходного артефакта не задан.
    /// </summary>
    InvalidSourceArtifactId,

    /// <summary>
    /// Комментарий пользователя пуст или состоит из пробелов.
    /// </summary>
    EmptyComment,

    /// <summary>
    /// Настройки производства не переданы.
    /// </summary>
    MissingSettings,

    /// <summary>
    /// Исходный артефакт не найден в persistence seam.
    /// </summary>
    SourceArtifactNotFound
}

/// <summary>
/// Представляет типизированную ожидаемую ошибку сценария создания задания.
/// </summary>
/// <param name="Code">Код ошибки.</param>
public sealed record CreateJobError(CreateJobErrorCode Code);
