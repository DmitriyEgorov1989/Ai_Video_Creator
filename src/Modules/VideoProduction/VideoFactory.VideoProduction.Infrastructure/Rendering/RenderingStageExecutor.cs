using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Rendering;

/// <summary>
/// Выполняет этап Rendering для единственного варианта задания.
/// </summary>
public sealed class RenderingStageExecutor : IJobStageExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly IEditPlanRevisionStore planStore;
    private readonly IArtifactStore artifactStore;
    private readonly IRenderHostAdapter renderHost;
    private readonly string workspaceRoot;
    private readonly IAudioStemBuilder audioStemBuilder;
    private readonly IMediaProbe mediaProbe;
    private readonly TimeSpan renderTimeout;
    private readonly int maxOutputBytes;
    private readonly long maxTelegramBytes;
    private readonly OutputEncodingProfile encodingProfile;
    private readonly IStorageBudget storageBudget;

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
        long maxTelegramBytes,
        int audioSampleRate,
        string audioChannelLayout,
        double audioLoudnessLufs,
        double audioTruePeakDbtp,
        OutputEncodingProfile encodingProfile,
        IStorageBudget? storageBudget = null)
    {
        this.planStore = planStore ?? throw new ArgumentNullException(nameof(planStore));
        this.artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        this.renderHost = renderHost ?? throw new ArgumentNullException(nameof(renderHost));
        this.audioStemBuilder = audioStemBuilder ?? throw new ArgumentNullException(nameof(audioStemBuilder));
        this.mediaProbe = mediaProbe ?? throw new ArgumentNullException(nameof(mediaProbe));
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? throw new ArgumentNullException(nameof(workspaceRoot)));
        if (renderTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(renderTimeout));
        if (maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        if (maxTelegramBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTelegramBytes));
        this.renderTimeout = renderTimeout;
        this.maxOutputBytes = maxOutputBytes;
        this.maxTelegramBytes = maxTelegramBytes;
        this.encodingProfile = encodingProfile ?? throw new ArgumentNullException(nameof(encodingProfile));
        this.encodingProfile.Validate();
        this.storageBudget = storageBudget ?? StorageBudgetRunner.Default;
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
        string directory = SafeFileTree.Resolve(workspaceRoot, $"data/jobs/{job.JobId}");
        ArtifactRecord? source = await artifactStore.GetAsync(job.Request.SourceAssetId, cancellationToken);
        long sourceSize = source?.SizeBytes ?? 0;
        EditPlanRevision? revision = job.CurrentApprovedEditPlanRevisionId is null ? null
            : await planStore.GetAsync(job.CurrentApprovedEditPlanRevisionId.Value, cancellationToken);
        ArtifactRecord? existing = await artifactStore.FindLatestAsync(job.JobId, "render-output", cancellationToken);
        if (job.Progress.Stage == ProductionStage.Rendering && existing is not null && revision is not null
            && existing.ParentArtifactId == job.Request.SourceAssetId && existing.MetadataJson is not null)
        {
            using JsonDocument metadata = JsonDocument.Parse(existing.MetadataJson);
            if (metadata.RootElement.TryGetProperty("planRevisionId", out JsonElement id)
                && id.GetString() == revision.RevisionId.ToString())
            {
                string path = ResolveWorkspacePath(existing.RelativePath);
                if (File.Exists(path))
                {
                    await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
                    if (file.Length == existing.SizeBytes && hash == existing.Sha256) return StageExecutionResult.Success();
                }
            }
        }
        if (revision is null)
        {
            return StageExecutionResult.Permanent("render.plan_missing", "Rendering requires an approved edit plan.");
        }

        RenderSizeEstimate sizeEstimate = RenderSizeEstimator.Estimate(
            revision.Plan.Output.DurationFrames,
            revision.Plan.Output.FrameRate,
            encodingProfile);
        if (sizeEstimate.EstimatedBytes > maxTelegramBytes)
        {
            return StageExecutionResult.Permanent(
                "render.telegram_size_estimate_exceeded",
                $"Estimated render size {sizeEstimate.EstimatedBytes} bytes for {sizeEstimate.DurationSeconds:0.###} seconds "
                + $"at {encodingProfile.VideoBitrateBitsPerSecond} video bits/s and "
                + $"{encodingProfile.AudioBitrateBitsPerSecond} audio bits/s exceeds Telegram limit {maxTelegramBytes} bytes.");
        }

        long estimate = checked(sizeEstimate.EstimatedBytes + sourceSize + 1_048_576);
        return await StorageBudgetRunner.RunAsync(storageBudget, directory, estimate,
            token => ExecuteCoreAsync(job, token), failure => failure.Code == "storage.job_quota_exceeded"
                ? StageExecutionResult.Permanent(failure.Code, failure.Message)
                : StageExecutionResult.Retryable(failure.Code, failure.Message), cancellationToken);
    }

    private async Task<StageExecutionResult> ExecuteCoreAsync(JobCreationRecord job, CancellationToken cancellationToken)
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

        Dictionary<ArtifactId, string> masterPaths = [];
        foreach (ArtifactId masterAssetId in revision.Plan.Video.Clips
                     .Select(clip => clip.Mapping.MasterAssetId)
                     .Distinct())
        {
            ArtifactRecord? master = await artifactStore.GetAsync(masterAssetId, cancellationToken);
            if (master is null)
            {
                return StageExecutionResult.Permanent(
                    "render.master_artifact_missing",
                    "A master artifact required by the edit plan was not found.");
            }

            string masterPath = ResolveWorkspacePath(master.RelativePath);
            if (!File.Exists(masterPath))
            {
                return StageExecutionResult.Permanent(
                    "render.master_file_missing",
                    "A master artifact file required by the edit plan does not exist.");
            }

            masterPaths.Add(masterAssetId, masterPath);
        }

        if (masterPaths.Count > 1)
        {
            return StageExecutionResult.Permanent(
                "render.multiple_master_assets_unsupported",
                "Edit Plan V1 rendering supports one master artifact.");
        }

        string renderSourcePath = masterPaths.Count == 1
            ? masterPaths.Values.Single()
            : sourcePath;

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
            new ProbeRequest(renderSourcePath, TimeSpan.FromSeconds(30), maxOutputBytes),
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
            AudioDirectorDecision? audioDecision = revision.Presentation?.Audio;
            audioPath = Path.Combine(attemptDirectory, "audio-master.m4a");
            AudioStemResult stem = await audioStemBuilder.BuildAsync(
                new AudioStemRequest(
                    renderSourcePath,
                    audioPath,
                    probe.Metadata,
                    TimeSpan.FromMinutes(10),
                    maxOutputBytes,
                    AudioSampleRate,
                    AudioChannelLayout,
                    audioDecision is null ? AudioLoudnessLufs : (double)audioDecision.TargetLoudnessLufs,
                    audioDecision is null ? AudioTruePeakDbtp : (double)audioDecision.TruePeakDbtp,
                    audioDecision?.Denoise ?? false),
                cancellationToken);
            if (!stem.IsCreated)
            {
                return StageExecutionResult.Retryable(
                    stem.ErrorCode ?? "render.audio_stem_failed",
                    stem.ErrorMessage ?? "Audio stem was not created.");
            }
        }

        IReadOnlyDictionary<ArtifactId, string> renderAssets = masterPaths.Count == 0
            ? new Dictionary<ArtifactId, string> { [job.Request.SourceAssetId] = sourcePath }
            : masterPaths;
        FrameRate masterFrameRate = probe.Metadata.VideoStreams[0].AverageFrameRate ?? new FrameRate(30, 1);
        string requestJson = BuildRequestJson(job, revision, renderAssets, masterFrameRate, audioPath, outputPath, encodingProfile);
        await File.WriteAllTextAsync(requestPath, requestJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

        RenderHostResult result = await renderHost.RenderAsync(
            new RenderHostRequest(requestPath, outputPath, renderTimeout, maxOutputBytes),
            cancellationToken);
        if (result.Kind != RenderHostResultKind.Succeeded)
        {
            string message = result.ErrorMessage ?? "Render failed.";
            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                message = $"{message}{Environment.NewLine}{result.StandardError}";
            }
            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                message = $"{message}{Environment.NewLine}stdout:{Environment.NewLine}{result.StandardOutput}";
            }

            return result.ErrorCode == "process.cancelled"
                ? StageExecutionResult.Cancelled(result.ErrorCode, message)
                : StageExecutionResult.Retryable(result.ErrorCode ?? "render.failed", message);
        }

        ArtifactId artifactId = ArtifactId.New();
        cancellationToken.ThrowIfCancellationRequested();
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
            JsonSerializer.Serialize(new { jobId = job.JobId, variantId = job.VariantId, planRevisionId = revision.RevisionId.ToString(), durationFrames = result.DurationFrames }, JsonOptions),
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
        return SafeFileTree.Resolve(workspaceRoot, relativePath);
    }

    private static string BuildRequestJson(
        JobCreationRecord job,
        EditPlanRevision revision,
        IReadOnlyDictionary<ArtifactId, string> renderAssets,
        FrameRate sourceFrameRate,
        string? audioPath,
        string outputPath,
        OutputEncodingProfile encodingProfile)
    {
        EditPlan plan = revision.Plan;
        object serializedPlan = revision.Presentation is null
            ? BuildV1Plan(plan)
            : BuildV2Plan(plan, revision.Presentation);
        object request = new
        {
            schemaVersion = 1,
            jobId = job.JobId.ToString(),
            variantId = job.VariantId.ToString(),
            plan = serializedPlan,
            assets = renderAssets.Select(asset => new
                {
                    id = asset.Key.ToString(),
                    path = asset.Value,
                    frameRate = (object?)new { numerator = sourceFrameRate.Numerator, denominator = sourceFrameRate.Denominator }
                })
                .Concat(audioPath is null
                    ? []
                    : [new
                    {
                        id = "__audio_master",
                        path = audioPath,
                        frameRate = (object?)null
                    }]),
            outputPath,
            encoding = new
            {
                videoBitrateBitsPerSecond = encodingProfile.VideoBitrateBitsPerSecond,
                audioBitrateBitsPerSecond = encodingProfile.AudioBitrateBitsPerSecond
            }
        };
        return JsonSerializer.Serialize(request, JsonOptions);
    }

    private static object BuildV1Plan(EditPlan plan) => new
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
                mapping = new
                {
                    originalAssetId = clip.Mapping.OriginalAssetId.ToString(),
                    original = new { startMicroseconds = clip.Mapping.Original.StartMicroseconds, endMicroseconds = clip.Mapping.Original.EndMicroseconds },
                    masterAssetId = clip.Mapping.MasterAssetId.ToString(),
                    master = new { startMicroseconds = clip.Mapping.Master.StartMicroseconds, endMicroseconds = clip.Mapping.Master.EndMicroseconds }
                },
                output = new { startFrame = clip.Output.StartFrame, endFrame = clip.Output.EndFrame },
                crop = clip.Crop switch
                {
                    CropPreset.Fit => "fit",
                    CropPreset.CenterCrop => "centerCrop",
                    _ => throw new InvalidOperationException($"Unsupported crop preset '{clip.Crop}'.")
                }
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
    };

    private static object BuildV2Plan(EditPlan plan, ReelsPresentationPlan presentation)
    {
        VideoClip firstClip = plan.Video.Clips[0];
        VisualEffectInstruction[] effects = presentation.Visuals?.Effects.ToArray() ?? [];
        CaptionGroup[] groups = presentation.Captions?.Groups.ToArray() ?? [];
        AudioDirectorDecision? audio = presentation.Audio;
        return new
        {
            schemaVersion = 2,
            jobId = plan.JobId.ToString(),
            variantId = plan.VariantId.ToString(),
            revisionId = plan.RevisionId.ToString(),
            analysisRevisionId = plan.AnalysisRevisionId.ToString(),
            profile = "instagram-reels",
            style = new
            {
                requested = StyleName(presentation.Style.RequestedStyle),
                resolved = StyleName(presentation.Style.ResolvedStyle)
            },
            output = new
            {
                width = plan.Output.Width,
                height = plan.Output.Height,
                frameRate = new { numerator = plan.Output.FrameRate.Numerator, denominator = plan.Output.FrameRate.Denominator },
                durationFrames = plan.Output.DurationFrames
            },
            source = new
            {
                originalAssetId = firstClip.Mapping.OriginalAssetId.ToString(),
                masterAssetId = firstClip.Mapping.MasterAssetId.ToString()
            },
            video = new
            {
                clips = plan.Video.Clips.Select(clip =>
                {
                    ReframeTrack? track = presentation.Reframe?.Tracks.FirstOrDefault(item => item.SegmentId == clip.SegmentId);
                    ReframeKeyframe? firstKeyframe = track?.Keyframes.FirstOrDefault();
                    return new
                    {
                        sourceAssetId = clip.Mapping.MasterAssetId.ToString(),
                        source = new { startMicroseconds = clip.Mapping.Original.StartMicroseconds, endMicroseconds = clip.Mapping.Original.EndMicroseconds },
                        master = new { startMicroseconds = clip.Mapping.Master.StartMicroseconds, endMicroseconds = clip.Mapping.Master.EndMicroseconds },
                        output = new { startFrame = clip.Output.StartFrame, endFrame = clip.Output.EndFrame },
                        crop = new
                        {
                            preset = track?.Fallback == ReframeFallback.Fit ? "fit" : "fill",
                            focalX = firstKeyframe?.FocalX ?? 0.5m,
                            focalY = firstKeyframe?.FocalY ?? 0.5m,
                            zoom = firstKeyframe?.Zoom ?? 1m,
                            fallback = track?.Fallback == ReframeFallback.Fit ? "fit" : "center",
                            keyframes = track?.Keyframes.Select(keyframe => new
                            {
                                outputFrame = keyframe.OutputFrame,
                                focalX = keyframe.FocalX,
                                focalY = keyframe.FocalY,
                                zoom = keyframe.Zoom
                            }) ?? []
                        },
                        effects = effects.Where(effect => effect.Output.StartFrame < clip.Output.EndFrame && clip.Output.StartFrame < effect.Output.EndFrame).Select(effect => effect.Id)
                    };
                })
            },
            effects = effects.Select(effect => new
            {
                id = effect.Id,
                type = effect.Kind switch
                {
                    VisualEffectKind.DynamicZoom => "dynamicZoom",
                    VisualEffectKind.TextHighlight => "textHighlight",
                    VisualEffectKind.VisualTransition => "visualTransition",
                    _ => throw new InvalidOperationException($"Unsupported visual effect '{effect.Kind}'.")
                },
                output = new { startFrame = effect.Output.StartFrame, endFrame = effect.Output.EndFrame },
                sourceSkillId = effect.SourceSkillId,
                parameters = new { amount = effect.Strength }
            }),
            captions = new
            {
                words = groups.SelectMany(group => group.Words).Select(word => new { text = word.Text, output = new { startFrame = word.Output.StartFrame, endFrame = word.Output.EndFrame } }),
                groups = groups.Select(group => new
                {
                    text = group.Text,
                    words = group.Words.Select(word => new { text = word.Text, output = new { startFrame = word.Output.StartFrame, endFrame = word.Output.EndFrame } }),
                    output = new { startFrame = group.Output.StartFrame, endFrame = group.Output.EndFrame },
                    maxLines = group.MaxLines,
                    highlight = group.Highlight == CaptionHighlightMode.ActiveWord ? "activeWord" : "none",
                    animation = group.Animation == CaptionAnimation.Fade ? "fade" : "none",
                    placement = "bottomSafe"
                })
            },
            audio = new
            {
                sourceGain = plan.Audio.SourceClips.IsEmpty ? 1m : plan.Audio.SourceClips[0].Gain,
                targetLoudnessLufs = audio?.TargetLoudnessLufs ?? -16m,
                truePeakDbtp = audio?.TruePeakDbtp ?? -1m,
                fadeInFrames = audio?.FadeInFrames ?? 0,
                fadeOutFrames = audio?.FadeOutFrames ?? 0,
                denoise = audio?.Denoise ?? false,
                musicAssetId = audio?.MusicAssetId,
                musicGain = audio?.MusicAssetId is null ? (decimal?)null : 1m,
                ducking = audio?.Ducking
            },
            skills = new EditingDecision?[] { presentation.Captions, presentation.Visuals, presentation.Reframe, presentation.Audio }
                .Where(decision => decision is not null)
                .Select(decision => new
                {
                    id = decision!.SkillId,
                    version = decision.SkillVersion,
                    status = decision.Status switch
                    {
                        EditingDecisionStatus.Applied => "applied",
                        EditingDecisionStatus.Skipped => "skipped",
                        _ => "rejected"
                    }
                })
        };
    }

    private static string StyleName(ReelsStyle style) => style switch
    {
        ReelsStyle.Auto => "auto",
        ReelsStyle.ExpertClean => "expert-clean",
        ReelsStyle.DynamicViral => "dynamic-viral",
        ReelsStyle.Cinematic => "cinematic",
        ReelsStyle.PremiumAds => "premium-ads",
        _ => throw new ArgumentOutOfRangeException(nameof(style))
    };
}
