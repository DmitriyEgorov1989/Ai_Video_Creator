using System.Text.Json;
using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Pipeline;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Rendering;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Rendering;

public sealed class RenderingStageExecutorTests
{
    [Fact]
    public async Task SendsPersistedReelsPresentationAsV2ToRenderHost()
    {
        string root = Path.Combine(Path.GetTempPath(), $"render-mapping-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string connectionString = $"Data Source={Path.Combine(root, "jobs.db")};Pooling=False";

        try
        {
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            ArtifactId originalId = ArtifactId.New();
            ArtifactId masterId = ArtifactId.New();
            await File.WriteAllBytesAsync(Path.Combine(root, "original.mp4"), [1]);
            await File.WriteAllBytesAsync(Path.Combine(root, "master.mp4"), [2]);

            SqliteArtifactStore artifacts = new(connectionString);
            await artifacts.RegisterAsync(
                new ArtifactRecord(originalId, "OriginalVideo", "original.mp4", "original-hash", 1, null, null, "test"),
                CancellationToken.None);
            await artifacts.RegisterAsync(
                new ArtifactRecord(masterId, "master-video", "master.mp4", "master-hash", 1, null, originalId, "test"),
                CancellationToken.None);

            CreateJobResult.Succeeded created = Assert.IsType<CreateJobResult.Succeeded>(
                await new CreateJobUseCase(new SqliteJobCreationStore(connectionString)).ExecuteAsync(
                    new CreateJobCommand(originalId, "private-comment", new ProductionSettings(null, false, false))));
            SegmentId segmentId = SegmentId.New();
            SourceMapping mapping = new(
                originalId,
                new OriginalRange(2_000_000, 4_000_000),
                masterId,
                new MasterRange(2_000_000, 4_000_000));
            EditPlan plan = new(
                1,
                created.JobId,
                created.VariantId,
                RevisionId.New(),
                RevisionId.New(),
                42,
                new OutputProfile(1080, 1920, new FrameRate(30, 1), 60),
                TemplateId.Dynamic,
                new TemplateProps(CaptionPreset.None, ColorTheme.Dark, null),
                new VideoTrack([new VideoClip(segmentId, mapping, new OutputRange(0, 60), CropPreset.CenterCrop)]),
                new AudioTrack([], []),
                new CaptionTrack([]),
                new OverlayTrack([]));
            SqliteEditPlanRevisionStore plans = new(connectionString);
            ReelsStyleConfiguration style = new(
                ReelsStyle.DynamicViral,
                ReelsStyle.DynamicViral,
                CaptionHighlightMode.ActiveWord,
                CaptionAnimation.Fade,
                0.12m,
                0.2m,
                0.15m,
                6,
                false);
            ReelsPresentationPlan presentation = new(
                style,
                new CaptionDirectorDecision(
                    "caption-director", "1.0.0", EditingDecisionStatus.Applied, [],
                    [new CaptionGroup(
                        "Привет", [new CaptionWordCue("Привет", new OutputRange(0, 30))],
                        new OutputRange(0, 30), 2, CaptionHighlightMode.ActiveWord, CaptionAnimation.Fade, CaptionPlacement.BottomSafe)]),
                new VisualDirectorDecision(
                    "visual-director", "1.0.0", EditingDecisionStatus.Applied, [],
                    [new VisualEffectInstruction("zoom-1", VisualEffectKind.DynamicZoom, new OutputRange(0, 60), 0.12m, "visual-director")]),
                new SmartReframeDecision(
                    "smart-reframe", "1.0.0", EditingDecisionStatus.Applied, [],
                    [new ReframeTrack(segmentId, ReframeFallback.Center, [new ReframeKeyframe(0, 0.25m, 0.5m, 1.1m)])]),
                new AudioDirectorDecision(
                    "audio-director", "1.0.0", EditingDecisionStatus.Applied, [],
                    -18m, -2m, 6, 6, true, null, null));
            await plans.SaveAsync(plan, "plan-hash", 0m, presentation, CancellationToken.None);

            await using (SqliteConnection connection = new(connectionString))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE jobs SET stage = $stage, current_approved_edit_plan_revision_id = $revision WHERE job_id = $jobId;";
                command.Parameters.AddWithValue("$stage", (int)ProductionStage.Rendering);
                command.Parameters.AddWithValue("$revision", plan.RevisionId.ToString());
                command.Parameters.AddWithValue("$jobId", created.JobId.ToString());
                await command.ExecuteNonQueryAsync();
            }

            CapturingRenderHost host = new();
            CapturingAudio audioBuilder = new();
            RenderingStageExecutor executor = new(
                plans,
                artifacts,
                host,
                audioBuilder,
                new Probe(),
                root,
                TimeSpan.FromSeconds(5),
                4096,
                50_000_000,
                48_000,
                "stereo",
                -16,
                -1,
                new OutputEncodingProfile(4_000_000, 128_000, 2_097_152));

            StageExecutionResult result = await executor.ExecuteAsync(
                (await new SqliteJobStateStore(connectionString).GetAsync(created.JobId, CancellationToken.None))!,
                CancellationToken.None);

            Assert.Equal(StageExecutionKind.Completed, result.Kind);
            using JsonDocument request = JsonDocument.Parse(Assert.IsType<string>(host.RequestJson));
            JsonElement serializedPlan = request.RootElement.GetProperty("plan");
            Assert.Equal(2, serializedPlan.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("instagram-reels", serializedPlan.GetProperty("profile").GetString());
            Assert.Equal("dynamic-viral", serializedPlan.GetProperty("style").GetProperty("resolved").GetString());
            JsonElement clip = serializedPlan.GetProperty("video").GetProperty("clips")[0];
            Assert.Equal(masterId.ToString(), clip.GetProperty("sourceAssetId").GetString());
            Assert.Equal(2_000_000, clip.GetProperty("source").GetProperty("startMicroseconds").GetInt64());
            Assert.Equal(4_000_000, clip.GetProperty("master").GetProperty("endMicroseconds").GetInt64());
            Assert.Equal(0.25m, clip.GetProperty("crop").GetProperty("keyframes")[0].GetProperty("focalX").GetDecimal());
            Assert.Equal("visual-director", serializedPlan.GetProperty("effects")[0].GetProperty("sourceSkillId").GetString());
            Assert.Equal("Привет", serializedPlan.GetProperty("captions").GetProperty("groups")[0].GetProperty("text").GetString());
            Assert.Equal(-18m, serializedPlan.GetProperty("audio").GetProperty("targetLoudnessLufs").GetDecimal());

            JsonElement masterAsset = Assert.Single(
                request.RootElement.GetProperty("assets").EnumerateArray(),
                asset => asset.GetProperty("id").GetString() == masterId.ToString());
            Assert.Equal(masterId.ToString(), masterAsset.GetProperty("id").GetString());
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "master.mp4")), masterAsset.GetProperty("path").GetString());
            Assert.Equal(30, masterAsset.GetProperty("frameRate").GetProperty("numerator").GetInt32());
            Assert.Equal(1, masterAsset.GetProperty("frameRate").GetProperty("denominator").GetInt32());
            Assert.NotNull(audioBuilder.Request);
            Assert.Equal(-18, audioBuilder.Request.TargetLoudnessLufs);
            Assert.Equal(-2, audioBuilder.Request.TruePeakDbtp);
            Assert.True(audioBuilder.Request.Denoise);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class CapturingRenderHost : IRenderHostAdapter
    {
        public string? RequestJson { get; private set; }

        public async Task<RenderHostResult> RenderAsync(RenderHostRequest request, CancellationToken cancellationToken)
        {
            RequestJson = await File.ReadAllTextAsync(request.RequestFilePath, cancellationToken);
            await File.WriteAllBytesAsync(request.ExpectedOutputPath, [1], cancellationToken);
            return RenderHostResult.Succeeded(request.ExpectedOutputPath, 60, [], string.Empty, string.Empty);
        }
    }

    private sealed class Probe : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(MediaProbeResult.Succeeded(new MediaMetadata(
                "mp4",
                5_000_000,
                [new VideoStreamMetadata(0, "h264", 1080, 1920, new FrameRate(30, 1), false, 0)],
                [new AudioStreamMetadata(1, "aac", 48_000, 2)])));
    }

    private sealed class CapturingAudio : IAudioStemBuilder
    {
        public AudioStemRequest? Request { get; private set; }

        public async Task<AudioStemResult> BuildAsync(AudioStemRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            await File.WriteAllBytesAsync(request.OutputPath, [3], cancellationToken);
            return AudioStemResult.Created(request.OutputPath);
        }
    }
}
