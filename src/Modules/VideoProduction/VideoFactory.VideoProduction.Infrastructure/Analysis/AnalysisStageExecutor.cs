using System.Text.Json;
using System.Security.Cryptography;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Analysis;

/// <summary>Выполняет анализ подготовленного Master и сохраняет его ревизию.</summary>
public sealed class AnalysisStageExecutor : IJobStageExecutor
{
    private readonly IAnalysisService analysisService;
    private readonly IAnalysisArtifactPublisher artifactPublisher;
    private readonly IArtifactStore artifactStore;
    private readonly string workspaceRoot;

    /// <summary>Создаёт исполнитель этапа анализа.</summary>
    /// <param name="analysisService">Сервис анализа Master.</param>
    /// <param name="artifactPublisher">Publisher неизменяемой ревизии анализа.</param>
    /// <param name="artifactStore">Хранилище описаний Original и Master.</param>
    /// <param name="workspaceRoot">Корневой каталог локального хранилища.</param>
    public AnalysisStageExecutor(
        IAnalysisService analysisService,
        IAnalysisArtifactPublisher artifactPublisher,
        IArtifactStore artifactStore,
        string workspaceRoot)
    {
        this.analysisService = analysisService ?? throw new ArgumentNullException(nameof(analysisService));
        this.artifactPublisher = artifactPublisher ?? throw new ArgumentNullException(nameof(artifactPublisher));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(
        JobCreationRecord job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Progress.Stage != ProductionStage.Analysis)
        {
            return StageExecutionResult.Permanent(
                "analysis.invalid_stage",
                $"Analysis executor received stage '{job.Progress.Stage}'.");
        }


        if (job.Request.SourceAssetIds.Length > 1)
        {
            return await ExecuteMultiSourceAsync(job, cancellationToken);
        }

        ArtifactRecord? original = await artifactStore.GetAsync(job.Request.SourceAssetId, cancellationToken);
        if (original is null)
        {
            return StageExecutionResult.Permanent(
                "analysis.original_missing",
                "The Original artifact was not found.");
        }

        ArtifactRecord? master = await artifactStore.FindLatestAsync(job.JobId, "master-video", cancellationToken);
        if (master is null)
        {
            return StageExecutionResult.Permanent(
                "analysis.master_missing",
                "The prepared Master artifact was not found.");
        }

        string masterPath;
        try
        {
            masterPath = SafeFileTree.Resolve(workspaceRoot, master.RelativePath);
        }
        catch (IOException exception)
        {
            return StageExecutionResult.Permanent("analysis.master_path_invalid", exception.Message);
        }

        if (!File.Exists(masterPath))
        {
            return StageExecutionResult.Permanent("analysis.master_file_missing", "The prepared Master file does not exist.");
        }

        if (!TryReadDurations(master.MetadataJson, out long originalDuration, out long masterDuration))
        {
            return StageExecutionResult.Permanent(
                "analysis.timeline_metadata_missing",
                "Master metadata does not contain valid Original and Master durations in microseconds.");
        }

        ArtifactRecord? existing = await artifactStore.FindLatestAsync(job.JobId, "analysis-result", cancellationToken);
        if (existing is not null && await IsIntactAsync(existing, cancellationToken))
        {
            return StageExecutionResult.Success();
        }

        AnalysisExecutionResult analyzed = await analysisService.AnalyzeAsync(
            new AnalysisRequest(
                original.ArtifactId,
                master.ArtifactId,
                masterPath,
                originalDuration,
                masterDuration),
            cancellationToken);

        if (analyzed is AnalysisExecutionResult.Failed failed)
        {
            return failed.ErrorCode == "analysis.validation_failed"
                ? StageExecutionResult.Permanent(failed.ErrorCode, failed.ErrorMessage)
                : StageExecutionResult.Retryable(failed.ErrorCode, failed.ErrorMessage);
        }

        AnalysisResult analysis = ((AnalysisExecutionResult.Succeeded)analyzed).Analysis;
        AnalysisArtifactPublishResult published = await artifactPublisher.PublishAsync(
            job.JobId,
            analysis,
            cancellationToken);
        return published.IsSuccess
            ? StageExecutionResult.Success()
            : StageExecutionResult.Retryable(
                published.ErrorCode ?? "analysis.artifact_publish_failed",
                published.ErrorMessage ?? "Analysis artifact publication failed.");
    }

    private async Task<StageExecutionResult> ExecuteMultiSourceAsync(
        JobCreationRecord job,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ArtifactRecord> masters = await artifactStore.ListAsync(
            job.JobId, "master-video", cancellationToken);
        ArtifactRecord? existing = await artifactStore.FindLatestAsync(job.JobId, "analysis-result", cancellationToken);
        if (existing?.ToolVersion == "analysis-v3"
            && MatchesMasterLineage(existing, job.Request.SourceAssetIds, masters)
            && await IsIntactAsync(existing, cancellationToken))
            return StageExecutionResult.Success();

        IReadOnlyList<ArtifactRecord> sourceResults = await artifactStore.ListAsync(
            job.JobId, "analysis-source-result", cancellationToken);
        List<AnalysisResult> results = [];
        foreach (ArtifactId originalId in job.Request.SourceAssetIds)
        {
            ArtifactRecord? original = await artifactStore.GetAsync(originalId, cancellationToken);
            if (original is null)
                return StageExecutionResult.Permanent("analysis.original_missing", "An Original artifact was not found.");
            ArtifactRecord? master = masters.LastOrDefault(item => item.ParentArtifactId == originalId);
            if (master is null)
                return StageExecutionResult.Permanent("analysis.master_missing", "A prepared Master artifact was not found.");
            ArtifactRecord? cachedArtifact = sourceResults.LastOrDefault(item => item.ParentArtifactId == master.ArtifactId);
            AnalysisResult? cached = cachedArtifact is null
                ? null
                : await ReadSourceResultAsync(cachedArtifact, original.ArtifactId, master, cancellationToken);
            if (cached is not null)
            {
                results.Add(cached);
                continue;
            }
            StageExecutionResult? failure = await AnalyzeSourceAsync(original, master, results, job.JobId, cancellationToken);
            if (failure is not null) return failure;
        }

        MultiSourceAnalysisResult aggregate = CreateAggregateRevision(results);
        AnalysisArtifactPublishResult published = await artifactPublisher.PublishAggregateAsync(
            job.JobId, aggregate, cancellationToken);
        return published.IsSuccess
            ? StageExecutionResult.Success()
            : StageExecutionResult.Retryable(
                published.ErrorCode ?? "analysis.artifact_publish_failed",
                published.ErrorMessage ?? "Analysis artifact publication failed.");
    }

    private async Task<StageExecutionResult?> AnalyzeSourceAsync(
        ArtifactRecord original,
        ArtifactRecord master,
        List<AnalysisResult> results,
        JobId jobId,
        CancellationToken cancellationToken)
    {
        string masterPath;
        try
        {
            masterPath = SafeFileTree.Resolve(workspaceRoot, master.RelativePath);
        }
        catch (IOException exception)
        {
            return StageExecutionResult.Permanent("analysis.master_path_invalid", exception.Message);
        }
        if (!File.Exists(masterPath))
            return StageExecutionResult.Permanent("analysis.master_file_missing", "The prepared Master file does not exist.");
        if (!TryReadDurations(master.MetadataJson, out long originalDuration, out long masterDuration))
            return StageExecutionResult.Permanent("analysis.timeline_metadata_missing", "Master metadata does not contain valid durations.");

        AnalysisExecutionResult analyzed = await analysisService.AnalyzeAsync(
            new AnalysisRequest(original.ArtifactId, master.ArtifactId, masterPath, originalDuration, masterDuration),
            cancellationToken);
        if (analyzed is AnalysisExecutionResult.Failed failed)
            return failed.ErrorCode == "analysis.validation_failed"
                ? StageExecutionResult.Permanent(failed.ErrorCode, failed.ErrorMessage)
                : StageExecutionResult.Retryable(failed.ErrorCode, failed.ErrorMessage);
        AnalysisResult analysis = ((AnalysisExecutionResult.Succeeded)analyzed).Analysis;
        AnalysisArtifactPublishResult published = await artifactPublisher.PublishSourceAsync(
            jobId, analysis, master.Sha256, cancellationToken);
        if (!published.IsSuccess)
            return StageExecutionResult.Retryable(
                published.ErrorCode ?? "analysis.artifact_publish_failed",
                published.ErrorMessage ?? "Source analysis artifact publication failed.");
        results.Add(analysis);
        return null;
    }

    private static bool MatchesMasterLineage(
        ArtifactRecord aggregate,
        IReadOnlyList<ArtifactId> sourceIds,
        IReadOnlyList<ArtifactRecord> masters)
    {
        if (aggregate.MetadataJson is null) return false;
        try
        {
            using JsonDocument metadata = JsonDocument.Parse(aggregate.MetadataJson);
            if (!metadata.RootElement.TryGetProperty("masters", out JsonElement stored)
                || stored.ValueKind != JsonValueKind.Array
                || stored.GetArrayLength() != sourceIds.Count)
                return false;
            int index = 0;
            foreach (JsonElement item in stored.EnumerateArray())
            {
                ArtifactRecord? master = masters.LastOrDefault(candidate => candidate.ParentArtifactId == sourceIds[index++]);
                if (master is null
                    || !item.TryGetProperty("masterAssetId", out JsonElement id)
                    || !item.TryGetProperty("masterSha256", out JsonElement hash)
                    || !string.Equals(id.GetString(), master.ArtifactId.ToString(), StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(hash.GetString(), master.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<AnalysisResult?> ReadSourceResultAsync(
        ArtifactRecord artifact,
        ArtifactId originalId,
        ArtifactRecord master,
        CancellationToken cancellationToken)
    {
        if (!await IsIntactAsync(artifact, cancellationToken) || artifact.MetadataJson is null) return null;
        try
        {
            using JsonDocument metadata = JsonDocument.Parse(artifact.MetadataJson);
            if (!metadata.RootElement.TryGetProperty("masterSha256", out JsonElement inputHash)
                || inputHash.ValueKind != JsonValueKind.String
                || !string.Equals(inputHash.GetString(), master.Sha256, StringComparison.OrdinalIgnoreCase))
                return null;
            string path = SafeFileTree.Resolve(workspaceRoot, artifact.RelativePath);
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            AnalysisResult? result = await JsonSerializer.DeserializeAsync<AnalysisResult>(
                stream, cancellationToken: cancellationToken);
            return result?.OriginalAssetId == originalId && result.MasterAssetId == master.ArtifactId
                ? result
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MultiSourceAnalysisResult CreateAggregateRevision(IReadOnlyList<AnalysisResult> results)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join(':', results.Select(result => result.RevisionId.ToString()))));
        return new MultiSourceAnalysisResult(
            3,
            new RevisionId(new Guid(hash[..16])),
            [.. results.Select(result => new AnalysisSource(
                result.OriginalAssetId,
                result.MasterAssetId,
                result.Speech,
                result.Pauses,
                result.Scenes,
                result.Features))],
            [.. results.SelectMany(result => result.AllowedSegments)]);
    }

    private async Task<bool> IsIntactAsync(ArtifactRecord artifact, CancellationToken cancellationToken)
    {
        if (artifact.SizeBytes is not > 0 || string.IsNullOrWhiteSpace(artifact.Sha256))
        {
            return false;
        }

        string path;
        try
        {
            path = SafeFileTree.Resolve(workspaceRoot, artifact.RelativePath);
        }
        catch (IOException)
        {
            return false;
        }

        if (!File.Exists(path) || new FileInfo(path).Length != artifact.SizeBytes)
        {
            return false;
        }

        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadDurations(
        string? metadataJson,
        out long originalDuration,
        out long masterDuration)
    {
        originalDuration = 0;
        masterDuration = 0;
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(metadataJson);
            JsonElement root = document.RootElement;
            return TryReadDuration(root, "originalMetadata", out originalDuration)
                && TryReadDuration(root, "mediaMetadata", out masterDuration);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadDuration(JsonElement root, string propertyName, out long duration)
    {
        duration = 0;
        return root.TryGetProperty(propertyName, out JsonElement metadata)
            && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty("durationMicroseconds", out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out duration)
            && duration > 0;
    }
}
