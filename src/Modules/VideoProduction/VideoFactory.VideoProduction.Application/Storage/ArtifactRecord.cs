using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Storage;

/// <summary>
/// Описывает неизменяемый файл, зарегистрированный в хранилище артефактов.
/// </summary>
/// <param name="ArtifactId">Идентификатор артефакта.</param>
/// <param name="ArtifactType">Тип артефакта.</param>
/// <param name="RelativePath">Безопасный относительный путь к файлу.</param>
/// <param name="Sha256">SHA-256 файла или <see langword="null"/>, если файл ещё не опубликован.</param>
/// <param name="SizeBytes">Размер файла в байтах или <see langword="null"/>.</param>
/// <param name="MetadataJson">Технические metadata в JSON или <see langword="null"/>.</param>
/// <param name="ParentArtifactId">Родительский Artifact или <see langword="null"/>.</param>
/// <param name="ToolVersion">Версия инструмента или <see langword="null"/>.</param>
/// <param name="JobId">Идентификатор Job или <see langword="null"/> для ещё не связанного артефакта.</param>
/// <param name="VariantId">Идентификатор Variant или <see langword="null"/> для общего артефакта Job.</param>
public sealed record ArtifactRecord(
    ArtifactId ArtifactId,
    string ArtifactType,
    string RelativePath,
    string? Sha256,
    long? SizeBytes,
    string? MetadataJson,
    ArtifactId? ParentArtifactId,
    string? ToolVersion,
    JobId? JobId = null,
    VariantId? VariantId = null);
