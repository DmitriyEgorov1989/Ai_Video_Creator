using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Storage;

/// <summary>
/// Описывает опубликованный файл Artifact.
/// </summary>
/// <param name="ArtifactId">Идентификатор артефакта.</param>
/// <param name="RelativePath">Относительный путь от корня workspace.</param>
/// <param name="Sha256">SHA-256 опубликованного файла.</param>
/// <param name="SizeBytes">Размер опубликованного файла в байтах.</param>
public sealed record StoredArtifactFile(
    ArtifactId ArtifactId,
    string RelativePath,
    string Sha256,
    long SizeBytes);
