using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Validation;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Planning;

/// <summary>
/// Выполняет Planning на основании сохранённого результата Analysis.
/// </summary>
public sealed class PlanningStageExecutor : IJobStageExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EditPlanPlanner planner;
    private readonly IArtifactStore artifactStore;
    private readonly string workspaceRoot;
    private readonly decimal jobLimitUsd;

    /// <summary>Создаёт исполнитель этапа планирования.</summary>
    /// <param name="planner">Application planner монтажного плана.</param>
    /// <param name="artifactStore">Хранилище входных и аналитических артефактов.</param>
    /// <param name="workspaceRoot">Корневой каталог локальных артефактов.</param>
    /// <param name="jobLimitUsd">Лимит стоимости задания в долларах США.</param>
    public PlanningStageExecutor(
        EditPlanPlanner planner,
        IArtifactStore artifactStore,
        string workspaceRoot,
        decimal jobLimitUsd)
    {
        this.planner = planner ?? throw new ArgumentNullException(nameof(planner));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (jobLimitUsd < 0m) throw new ArgumentOutOfRangeException(nameof(jobLimitUsd));
        this.jobLimitUsd = jobLimitUsd;
    }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(
        JobCreationRecord job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Progress.Stage != ProductionStage.Planning)
        {
            return StageExecutionResult.Permanent(
                "planning.invalid_stage",
                $"Planning executor received stage '{job.Progress.Stage}'.");
        }

        try
        {
            ArtifactRecord? analysisArtifact = await artifactStore.FindLatestAsync(
                job.JobId,
                "analysis-result",
                cancellationToken);
            if (analysisArtifact is null)
            {
                return StageExecutionResult.Permanent(
                    "planning.analysis_missing",
                    "The saved analysis result was not found.");
            }

            StoredAnalysis? storedAnalysis = await ReadAnalysisAsync(analysisArtifact, cancellationToken);
            if (storedAnalysis is null)
            {
                return StageExecutionResult.Permanent(
                    "planning.analysis_invalid",
                    "The saved analysis result is invalid.");
            }

            AnalysisResult analysis = storedAnalysis.Compatibility;
            MultiSourceAnalysisResult? multiSourceAnalysis = storedAnalysis.MultiSource;
            if (multiSourceAnalysis is null
                && (analysis.OriginalAssetId != job.Request.SourceAssetId
                    || analysisArtifact.ParentArtifactId != analysis.MasterAssetId))
            {
                return StageExecutionResult.Permanent(
                    "planning.analysis_source_mismatch",
                    "The analysis result belongs to another source artifact.");
            }

            IReadOnlyList<(ArtifactId OriginalId, ArtifactId MasterId)> sourcePairs = multiSourceAnalysis is null
                ? [(analysis.OriginalAssetId, analysis.MasterAssetId)]
                : [.. multiSourceAnalysis.Sources.Select(source => (source.OriginalAssetId, source.MasterAssetId))];
            if (multiSourceAnalysis is not null
                && !sourcePairs.Select(pair => pair.OriginalId).SequenceEqual(job.Request.SourceAssetIds))
            {
                return StageExecutionResult.Permanent(
                    "planning.analysis_source_mismatch",
                    "The analysis source order differs from the Job source order.");
            }

            List<(ArtifactRecord Original, ArtifactRecord Master)> sourceArtifacts = [];
            foreach ((ArtifactId originalId, ArtifactId masterId) in sourcePairs)
            {
                ArtifactRecord? master = await artifactStore.GetAsync(masterId, cancellationToken);
                ArtifactRecord? original = await artifactStore.GetAsync(originalId, cancellationToken);
                if (master is null || original is null || master.ParentArtifactId != original.ArtifactId
                    || master.JobId != job.JobId)
                {
                    return StageExecutionResult.Permanent(
                        "planning.asset_missing",
                        "An artifact referenced by the analysis result was not found.");
                }
                sourceArtifacts.Add((original, master));
            }

            IReadOnlyDictionary<ArtifactId, MediaAsset> assets = CreateAssets(sourceArtifacts);
            ImmutableArray<ValidationIssue> analysisIssues = multiSourceAnalysis is null
                ? AnalysisValidator.Validate(analysis, assets)
                : AnalysisValidator.Validate(multiSourceAnalysis, assets);
            if (!analysisIssues.IsEmpty)
            {
                return StageExecutionResult.Permanent(
                    "planning.analysis_invalid",
                    "The saved analysis result failed validation.");
            }
            PlanningResult result = await planner.PlanAsync(
                new PlanningRequest(
                    job.JobId,
                    job.VariantId,
                    analysis,
                    job.Request.Comment,
                    job.Request.Settings,
                    assets,
                    CreateTemplates(),
                    job.TelegramUserId,
                    jobLimitUsd,
                    multiSourceAnalysis),
                cancellationToken);

            return result switch
            {
                PlanningResult.Created => StageExecutionResult.Success(
                    "planning.created",
                    "A new approved edit plan revision was created."),
                PlanningResult.Existing => StageExecutionResult.Success(
                    "planning.existing",
                    "An existing approved edit plan revision was reused."),
                PlanningResult.WaitingForApproval waiting => StageExecutionResult.WaitingForApproval(
                    "planning.cost_approval_required",
                    waiting.Message,
                    waiting.EstimatedCostUsd,
                    waiting.Version),
                PlanningResult.Rejected rejected => StageExecutionResult.Permanent(
                    rejected.ErrorCode,
                    rejected.ErrorMessage),
                _ => StageExecutionResult.Permanent(
                    "planning.result_unknown",
                    "Planning returned an unknown result.")
            };
        }
        catch (JsonException exception)
        {
            return StageExecutionResult.Permanent("planning.analysis_invalid", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return StageExecutionResult.Permanent("planning.input_invalid", exception.Message);
        }
    }

    private async Task<StoredAnalysis?> ReadAnalysisAsync(
        ArtifactRecord artifact,
        CancellationToken cancellationToken)
    {
        string path = SafeFileTree.Resolve(workspaceRoot, artifact.RelativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        if (artifact.SizeBytes is not > 0 || string.IsNullOrWhiteSpace(artifact.Sha256)
            || new FileInfo(path).Length != artifact.SizeBytes)
        {
            return null;
        }
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase)) return null;
        stream.Position = 0;
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement version)
            || !version.TryGetInt32(out int schemaVersion))
            return null;
        if (schemaVersion == 3)
        {
            MultiSourceAnalysisResult? aggregate = document.RootElement.Deserialize<MultiSourceAnalysisResult>(JsonOptions);
            if (aggregate is null
                || aggregate.Sources.IsDefaultOrEmpty
                || aggregate.Sources.Select(source => source.OriginalAssetId).Distinct().Count() != aggregate.Sources.Length
                || aggregate.Sources.Select(source => source.MasterAssetId).Distinct().Count() != aggregate.Sources.Length)
                return null;
            return new StoredAnalysis(CreateCompatibilityAnalysis(aggregate), aggregate);
        }

        AnalysisResult? legacy = document.RootElement.Deserialize<AnalysisResult>(JsonOptions);
        return legacy is null ? null : new StoredAnalysis(legacy, null);
    }

    private static IReadOnlyDictionary<ArtifactId, MediaAsset> CreateAssets(
        IEnumerable<(ArtifactRecord Original, ArtifactRecord Master)> sourceArtifacts)
    {
        Dictionary<ArtifactId, MediaAsset> assets = [];
        foreach ((ArtifactRecord original, ArtifactRecord master) in sourceArtifacts)
        {
            using JsonDocument document = JsonDocument.Parse(master.MetadataJson ?? throw new JsonException("Master metadata is missing."));
            JsonElement root = document.RootElement;
            assets.Add(original.ArtifactId, new MediaAsset(original.ArtifactId, ReadMetadata(root, "originalMetadata")));
            assets.Add(master.ArtifactId, new MediaAsset(master.ArtifactId, ReadMetadata(root, "mediaMetadata")));
        }
        return assets;
    }

    private static AnalysisResult CreateCompatibilityAnalysis(MultiSourceAnalysisResult analysis)
    {
        AnalysisSource first = analysis.Sources[0];
        return new AnalysisResult(
            2,
            analysis.RevisionId,
            first.OriginalAssetId,
            first.MasterAssetId,
            [.. analysis.Sources.SelectMany(source => source.Speech)],
            first.Pauses,
            first.Scenes,
            analysis.AllowedSegments)
        {
            Features = new AnalysisFeatureSet([.. analysis.Sources.SelectMany(source => source.Features.Segments)])
            {
                FocalObservations = [.. analysis.Sources.SelectMany(source => source.Features.FocalObservations)]
            }
        };
    }

    private static MediaMetadata ReadMetadata(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            throw new JsonException($"Master metadata does not contain {propertyName}.");
        return value.Deserialize<MediaMetadata>(JsonOptions)
            ?? throw new JsonException($"Master metadata contains invalid {propertyName}.");
    }

    private static IReadOnlyDictionary<TemplateId, TemplateDefinition> CreateTemplates() =>
        new Dictionary<TemplateId, TemplateDefinition>
        {
            [TemplateId.Dynamic] = new TemplateDefinition(
                TemplateId.Dynamic,
                [CaptionPreset.None, CaptionPreset.Minimal, CaptionPreset.Emphasis],
                supportsTitle: true)
        };

    private sealed record StoredAnalysis(AnalysisResult Compatibility, MultiSourceAnalysisResult? MultiSource);
}
