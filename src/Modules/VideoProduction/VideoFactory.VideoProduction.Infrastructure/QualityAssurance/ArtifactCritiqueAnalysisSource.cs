using System.Security.Cryptography;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.QualityAssurance;

/// <summary>Читает неизменяемый Analysis artifact и проверяет его размер и SHA-256.</summary>
public sealed class ArtifactCritiqueAnalysisSource : ICritiqueAnalysisSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IArtifactStore artifactStore;
    private readonly string workspaceRoot;

    /// <summary>Создаёт источник Analysis из локального artifact store.</summary>
    public ArtifactCritiqueAnalysisSource(IArtifactStore artifactStore, string workspaceRoot)
    {
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    }

    /// <inheritdoc />
    public async Task<MultiSourceAnalysisResult?> ReadAsync(
        JobId jobId,
        CancellationToken cancellationToken)
    {
        ArtifactRecord? artifact = await artifactStore.FindLatestAsync(
            jobId,
            "analysis-result",
            cancellationToken);
        if (artifact is null || artifact.SizeBytes is not > 0 || string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            return null;
        }

        string path = SafeFileTree.Resolve(workspaceRoot, artifact.RelativePath);
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.SizeBytes) return null;
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
        stream.Position = 0;

        try
        {
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement version)
                || !version.TryGetInt32(out int schemaVersion))
            {
                return null;
            }

            if (schemaVersion == 3)
            {
                return document.RootElement.Deserialize<MultiSourceAnalysisResult>(JsonOptions);
            }

            AnalysisResult? legacy = document.RootElement.Deserialize<AnalysisResult>(JsonOptions);
            return legacy is null ? null : AnalysisContractAdapter.ToMultiSource(legacy);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
