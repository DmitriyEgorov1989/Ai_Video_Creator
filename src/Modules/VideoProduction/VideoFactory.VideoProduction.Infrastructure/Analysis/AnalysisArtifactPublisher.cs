using System.Text.Json;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Infrastructure.Analysis;

/// <summary>Сохраняет AnalysisResult через атомарное локальное хранилище.</summary>
public sealed class AnalysisArtifactPublisher : IAnalysisArtifactPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly ILocalArtifactStorage localStorage;
    private readonly IArtifactStore artifactStore;

    /// <summary>Создаёт publisher результата анализа.</summary>
    public AnalysisArtifactPublisher(
        ILocalArtifactStorage localStorage,
        IArtifactStore artifactStore)
    {
        this.localStorage = localStorage;
        this.artifactStore = artifactStore;
    }

    /// <inheritdoc />
    public async Task<AnalysisArtifactPublishResult> PublishAsync(
        JobId jobId,
        AnalysisResult analysis,
        CancellationToken cancellationToken)
    {
        if (jobId.IsEmpty)
        {
            return new(false, null, "analysis.job_id_empty", "Job ID cannot be empty.");
        }

        ArtifactId artifactId = CreateArtifactId(jobId, analysis.RevisionId);
        await using MemoryStream content = new();
        await JsonSerializer.SerializeAsync(content, analysis, JsonOptions, cancellationToken);
        content.Position = 0;

        ArtifactPublishResult published = await localStorage.PublishAsync(
            jobId,
            artifactId,
            content,
            cancellationToken);
        if (published is not ArtifactPublishResult.Succeeded succeeded)
        {
            return new(false, null, "analysis.artifact_publish_failed", ((ArtifactPublishResult.Rejected)published).Message);
        }

        ArtifactRegistrationResult registered = await artifactStore.RegisterAsync(
            new ArtifactRecord(
                artifactId,
                "analysis-result",
                succeeded.File.RelativePath,
                succeeded.File.Sha256,
                succeeded.File.SizeBytes,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = analysis.SchemaVersion,
                    featureSchemaVersion = analysis.Features.SchemaVersion,
                    analysis.RevisionId
                }),
                analysis.MasterAssetId,
                analysis.SchemaVersion >= 2 || !analysis.Features.Segments.IsEmpty
                    ? "analysis-v2"
                    : "analysis-v1",
                jobId),
            cancellationToken);

        return registered switch
        {
            ArtifactRegistrationResult.Created _ => new(true, artifactId, null, null),
            ArtifactRegistrationResult.Existing _ => new(true, artifactId, null, null),
            ArtifactRegistrationResult.Rejected rejected => new(false, null, "analysis.artifact_register_failed", rejected.Message),
            _ => new(false, null, "analysis.artifact_register_failed", "Unknown artifact registration result.")
        };
    }

    /// <inheritdoc />
    public Task<AnalysisArtifactPublishResult> PublishSourceAsync(
        JobId jobId,
        AnalysisResult analysis,
        string? masterSha256,
        CancellationToken cancellationToken) =>
        PublishCoreAsync(
            jobId,
            analysis.RevisionId,
            analysis,
            "analysis-source-result",
            analysis.MasterAssetId,
            "analysis-source-v2",
            JsonSerializer.Serialize(new
            {
                schemaVersion = analysis.SchemaVersion,
                featureSchemaVersion = analysis.Features.SchemaVersion,
                analysis.RevisionId,
                analysis.OriginalAssetId,
                analysis.MasterAssetId,
                masterSha256
            }),
            cancellationToken);

    /// <inheritdoc />
    public async Task<AnalysisArtifactPublishResult> PublishAggregateAsync(
        JobId jobId,
        MultiSourceAnalysisResult analysis,
        CancellationToken cancellationToken)
    {
        List<object> masters = [];
        foreach (AnalysisSource source in analysis.Sources)
        {
            ArtifactRecord? master = await artifactStore.GetAsync(source.MasterAssetId, cancellationToken);
            masters.Add(new { masterAssetId = source.MasterAssetId, masterSha256 = master?.Sha256 });
        }
        return await PublishCoreAsync(
            jobId,
            analysis.RevisionId,
            analysis,
            "analysis-result",
            null,
            "analysis-v3",
            JsonSerializer.Serialize(new { schemaVersion = 3, analysis.RevisionId, masters }),
            cancellationToken);
    }

    private async Task<AnalysisArtifactPublishResult> PublishCoreAsync<T>(
        JobId jobId,
        RevisionId revisionId,
        T analysis,
        string artifactType,
        ArtifactId? parentArtifactId,
        string toolVersion,
        string metadataJson,
        CancellationToken cancellationToken)
    {
        if (jobId.IsEmpty)
            return new(false, null, "analysis.job_id_empty", "Job ID cannot be empty.");
        ArtifactId artifactId = CreateArtifactId(jobId, revisionId);
        await using MemoryStream content = new();
        await JsonSerializer.SerializeAsync(content, analysis, JsonOptions, cancellationToken);
        content.Position = 0;
        ArtifactPublishResult published = await localStorage.PublishAsync(
            jobId, artifactId, content, cancellationToken);
        if (published is not ArtifactPublishResult.Succeeded succeeded)
            return new(false, null, "analysis.artifact_publish_failed", ((ArtifactPublishResult.Rejected)published).Message);
        ArtifactRegistrationResult registered = await artifactStore.RegisterAsync(
            new ArtifactRecord(
                artifactId,
                artifactType,
                succeeded.File.RelativePath,
                succeeded.File.Sha256,
                succeeded.File.SizeBytes,
                metadataJson,
                parentArtifactId,
                toolVersion,
                jobId),
            cancellationToken);
        return registered switch
        {
            ArtifactRegistrationResult.Created _ => new(true, artifactId, null, null),
            ArtifactRegistrationResult.Existing _ => new(true, artifactId, null, null),
            ArtifactRegistrationResult.Rejected rejected => new(false, null, "analysis.artifact_register_failed", rejected.Message),
            _ => new(false, null, "analysis.artifact_register_failed", "Unknown artifact registration result.")
        };
    }

    private static ArtifactId CreateArtifactId(JobId jobId, RevisionId revisionId)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"analysis:{jobId}:{revisionId}"));
        return new ArtifactId(new Guid(hash[..16]));
    }
}
