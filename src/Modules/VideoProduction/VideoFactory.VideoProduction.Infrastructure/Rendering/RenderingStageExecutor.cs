using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Rendering;

/// <summary>
/// Выполняет этап Rendering для единственного варианта задания.
/// </summary>
public sealed class RenderingStageExecutor : IJobStageExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IEditPlanRevisionStore planStore;
    private readonly IArtifactStore artifactStore;
    private readonly IRenderHostAdapter renderHost;
    private readonly string workspaceRoot;
    private readonly IAudioStemBuilder audioStemBuilder;
    private readonly IMediaProbe mediaProbe;
    private readonly TimeSpan renderTimeout;
    private readonly int maxOutputBytes;

    /// <summary>Создаёт исполнитель Rendering.</summary>
    public RenderingStageExecutor(
        IEditPlanRevisionStore planStore,
        IArtifactStore artifactStore,
        IRenderHostAdapter renderHost,
        IAudioStemBuilder audioStemBuilder,
        IMediaProbe mediaProbe,
        string workspaceRoot,
        TimeSpan renderTimeout,
        int maxOutputBytes,
        int audioSampleRate,
        string audioChannelLayout,
        double audioLoudnessLufs,
        double audioTruePeakDbtp)
    {
        this.planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.renderHost = renderHost ?? throw new ArgumentNullException(nameof(renderHost));
        this.audioStemBuilder = audioStemBuilder ?? throw new ArgumentNullException(nameof(audioStemBuilder));
        this.mediaProbe = mediaProbe ?? throw new ArgumentNullException(nameof(mediaProbe));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (renderTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(renderTimeout));
        if (maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        this.renderTimeout = renderTimeout;
        this.maxOutputBytes = maxOutputBytes;
        AudioSampleRate = audioSampleRate;
        AudioChannelLayout = audioChannelLayout;
        AudioLoudnessLufs = audioLoudnessLufs;
        AudioTruePeakDbtp = audioTruePeakDbtp;
    }

    private int AudioSampleRate { get; }
    private string AudioChannelLayout { get; }
    private double AudioLoudnessLufs { get; }
    private double AudioTruePeakDbtp { get; }

    /// <inheritdoc />
    public async Task<StageExecutionResult> ExecuteAsync(
        JobCreationRecord job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Progress.Stage != ProductionStage.Rendering)
        {
            return StageExecutionResult.Permanent(
                "render.invalid_stage",
                $"Rendering executor received stage '{job.Progress.Stage}'.");
        }

        if (job.CurrentApprovedEditPlanRevisionId is null)
        {
            return StageExecutionResult.Permanent(
                "render.plan_missing",
                "Rendering requires an approved edit plan.");
        }

        EditPlanRevision? revision = await planStore.GetAsync(
            job.CurrentApprovedEditPlanRevisionId.Value,
            cancellationToken);
        if (revision is null || revision.JobId != job.JobId || revision.VariantId != job.VariantId)
        {
            return StageExecutionResult.Permanent(
                "render.plan_not_found",
                "The approved edit plan does not belong to this job variant.");
        }

        ArtifactRecord? source = await artifactStore.GetAsync(
            job.Request.SourceAssetId,
            cancellationToken);
        if (source is null)
        {
            return StageExecutionResult.Permanent(
                "render.source_artifact_missing",
                "The source artifact required by the edit plan was not found.");
        }

        string sourcePath = ResolveWorkspacePath(source.RelativePath);
        if (!File.Exists(sourcePath))
        {
            return StageExecutionResult.Permanent(
                "render.source_file_missing",
                "The source artifact file does not exist.");
        }

        string attemptDirectory = Path.Combine(
            workspaceRoot,
            "data",
            "jobs",
            job.JobId.ToString(),
            "renders",
            job.VariantId.ToString(),
            $"attempt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(attemptDirectory);
        string requestPath = Path.Combine(attemptDirectory, "request.json");
        string outputPath = Path.Combine(attemptDirectory, "output.rendering.mp4");

        MediaProbeResult probe = await mediaProbe.ProbeAsync(
            new ProbeRequest(sourcePath, TimeSpan.FromSeconds(30), maxOutputBytes),
            cancellationToken);
        if (!probe.IsSuccess)
        {
            return StageExecutionResult.Retryable(
                probe.ErrorCode ?? "render.source_probe_failed",
                probe.ErrorMessage ?? "Source probe failed.");
        }

        string? audioPath = null;
        if (probe.Metadata!.AudioStreams.Length > 0)
        {
            audioPath = Path.Combine(attemptDirectory, "audio-master.m4a");
            AudioStemResult stem = await audioStemBuilder.BuildAsync(
                new AudioStemRequest(
                    sourcePath,
                    audioPath,
                    probe.Metadata,
                    TimeSpan.FromMinutes(10),
                    maxOutputBytes,
                    AudioSampleRate,
                    AudioChannelLayout,
                    AudioLoudnessLufs,
                    AudioTruePeakDbtp),
                cancellationToken);
            if (!stem.IsCreated)
            {
                return StageExecutionResult.Retryable(
                    stem.ErrorCode ?? "render.audio_stem_failed",
                    stem.ErrorMessage ?? "Audio stem was not created.");
            }
        }

        string requestJson = BuildRequestJson(job, revision.Plan, sourcePath, audioPath, outputPath);
        await File.WriteAllTextAsync(requestPath, requestJson, Encoding.UTF8, cancellationToken);

        RenderHostResult result = await renderHost.RenderAsync(
            new RenderHostRequest(requestPath, outputPath, renderTimeout, maxOutputBytes),
            cancellationToken);
        if (result.Kind != RenderHostResultKind.Succeeded)
        {
            return result.ErrorCode == "process.cancelled"
                ? StageExecutionResult.Cancelled(result.ErrorCode, result.ErrorMessage)
                : StageExecutionResult.Retryable(result.ErrorCode ?? "render.failed", result.ErrorMessage ?? "Render failed.");
        }

        ArtifactId artifactId = ArtifactId.New();
        string relativePath = Path.GetRelativePath(workspaceRoot, outputPath).Replace(Path.DirectorySeparatorChar, '/');
        FileInfo output = new(outputPath);
        await using FileStream outputStream = new(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(outputStream, cancellationToken)).ToLowerInvariant();
        ArtifactRecord record = new(
            artifactId,
            "render-output",
            relativePath,
            hash,
            output.Length,
            JsonSerializer.Serialize(new { jobId = job.JobId, variantId = job.VariantId, durationFrames = result.DurationFrames }, JsonOptions),
            job.Request.SourceAssetId,
            "remotion-render-host",
            job.JobId);
        ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(record, cancellationToken);
        return registration is ArtifactRegistrationResult.Rejected rejected
            ? StageExecutionResult.Permanent("render.artifact_rejected", rejected.Message)
            : StageExecutionResult.Success();
    }

    private string ResolveWorkspacePath(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = workspaceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? workspaceRoot
            : workspaceRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Artifact path escaped workspace root.");
        }

        return path;
    }

    private static string BuildRequestJson(
        JobCreationRecord job,
        EditPlan plan,
        string sourcePath,
        string? audioPath,
        string outputPath)
    {
        string[] segmentIds = plan.Video.Clips
            .Select(clip => clip.SegmentId.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        object request = new
        {
            schemaVersion = 1,
            jobId = job.JobId.ToString(),
            variantId = job.VariantId.ToString(),
            plan = new
            {
                schemaVersion = plan.SchemaVersion,
                jobId = plan.JobId.ToString(),
                variantId = plan.VariantId.ToString(),
                revisionId = plan.RevisionId.ToString(),
                analysisRevisionId = plan.AnalysisRevisionId.ToString(),
                seed = plan.Seed,
                output = new
                {
                    width = plan.Output.Width,
                    height = plan.Output.Height,
                    frameRate = new { numerator = plan.Output.FrameRate.Numerator, denominator = plan.Output.FrameRate.Denominator },
                    durationFrames = plan.Output.DurationFrames
                },
                template = plan.Template.ToString().ToLowerInvariant(),
                templateProps = new
                {
                    captions = plan.TemplateProps.Captions.ToString().ToLowerInvariant(),
                    theme = plan.TemplateProps.Theme.ToString().ToLowerInvariant(),
                    title = plan.TemplateProps.Title
                },
                video = new
                {
                    clips = plan.Video.Clips.Select(clip => new
                    {
                        segmentId = clip.SegmentId.ToString(),
                        mapping = new { },
                        output = new { startFrame = clip.Output.StartFrame, endFrame = clip.Output.EndFrame },
                        crop = clip.Crop.ToString().ToLowerInvariant()
                    })
                },
                audio = new
                {
                    sourceClips = plan.Audio.SourceClips.Select(clip => new
                    {
                        segmentId = clip.SegmentId.ToString(),
                        output = new { startFrame = clip.Output.StartFrame, endFrame = clip.Output.EndFrame },
                        gain = clip.Gain
                    }),
                    musicClips = Array.Empty<object>()
                },
                captions = new { items = plan.Captions.Items },
                overlays = new { items = plan.Overlays.Items.OfType<TextOverlay>().Select(item => new { type = "text", text = item.Text, output = item.Output }) }
            },
            assets = segmentIds.Select(id => new { id, path = sourcePath })
                .Concat(audioPath is null
                    ? []
                    : [new { id = "__audio_master", path = audioPath }]),
            outputPath
        };
        return JsonSerializer.Serialize(request, JsonOptions);
    }
}
