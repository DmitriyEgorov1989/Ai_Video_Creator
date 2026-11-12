namespace VideoFactory.VideoProduction.Application.Storage;

/// <summary>
/// Представляет результат регистрации неизменяемого Artifact.
/// </summary>
public abstract record ArtifactRegistrationResult
{
    /// <summary>
    /// Артефакт зарегистрирован впервые.
    /// </summary>
    /// <param name="Record">Зарегистрированное описание артефакта.</param>
    public sealed record Created(ArtifactRecord Record) : ArtifactRegistrationResult;

    /// <summary>
    /// Артефакт уже зарегистрирован с тем же идентификатором.
    /// </summary>
    /// <param name="Record">Существующее описание артефакта.</param>
    public sealed record Existing(ArtifactRecord Record) : ArtifactRegistrationResult;

    /// <summary>
    /// Регистрация отклонена из-за ожидаемой ошибки входа.
    /// </summary>
    /// <param name="Message">Понятное описание причины отказа.</param>
    public sealed record Rejected(string Message) : ArtifactRegistrationResult;
}
