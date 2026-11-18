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

            AnalysisResult? analysis = await ReadAnalysisAsync(analysisArtifact, cancellationToken);
            if (analysis is null)
            {
                return StageExecutionResult.Permanent(
                    "planning.analysis_invalid",
                    "The saved analysis result is invalid.");
            }

            if (analysis.OriginalAssetId != job.Request.SourceAssetId
                || analysisArtifact.ParentArtifactId != analysis.MasterAssetId)
            {
                return StageExecutionResult.Permanent(
                    "planning.analysis_source_mismatch",
                    "The analysis result belongs to another source artifact.");
            }

            ArtifactRecord? master = await artifactStore.GetAsync(analysis.MasterAssetId, cancellationToken);
            ArtifactRecord? original = await artifactStore.GetAsync(analysis.OriginalAssetId, cancellationToken);
            if (master is null || original is null || master.ParentArtifactId != original.ArtifactId
                || master.JobId != job.JobId)
            {
                return StageExecutionResult.Permanent(
                    "planning.asset_missing",
                    "An artifact referenced by the analysis result was not found.");
            }

            IReadOnlyDictionary<ArtifactId, MediaAsset> assets = CreateAssets(original, master);
            if (!AnalysisValidator.Validate(analysis, assets).IsEmpty)
            {
                return StageExecutionResult.Permanent("planning.analysis_invalid", "The saved analysis result failed validation.");
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
                    jobLimitUsd),
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

    private async Task<AnalysisResult?> ReadAnalysisAsync(
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
        return await JsonSerializer.DeserializeAsync<AnalysisResult>(stream, JsonOptions, cancellationToken);
    }

    private static IReadOnlyDictionary<ArtifactId, MediaAsset> CreateAssets(
        ArtifactRecord original,
        ArtifactRecord master)
    {
        using JsonDocument document = JsonDocument.Parse(master.MetadataJson ?? throw new JsonException("Master metadata is missing."));
        JsonElement root = document.RootElement;
        MediaMetadata masterMetadata = ReadMetadata(root, "mediaMetadata");
        MediaMetadata originalMetadata = ReadMetadata(root, "originalMetadata");

        return new Dictionary<ArtifactId, MediaAsset>
        {
            [original.ArtifactId] = new MediaAsset(original.ArtifactId, originalMetadata),
            [master.ArtifactId] = new MediaAsset(master.ArtifactId, masterMetadata)
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
}
