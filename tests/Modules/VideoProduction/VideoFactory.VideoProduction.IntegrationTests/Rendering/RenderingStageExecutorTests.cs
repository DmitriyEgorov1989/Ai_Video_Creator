using System.Security.Cryptography;
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
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SendsPersistedReelsPresentationWithVerifiedSourceSpecificAssetsToRenderHost(
        bool multiSource,
        bool corruptMaster)
    {
        string root = Path.Combine(Path.GetTempPath(), $"render-mapping-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string connectionString = $"Data Source={Path.Combine(root, "jobs.db")};Pooling=False";

        try
        {
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            ArtifactId originalId = ArtifactId.New();
            ArtifactId masterId = ArtifactId.New();
            ArtifactId secondOriginalId = ArtifactId.New();
            ArtifactId secondMasterId = ArtifactId.New();
            await File.WriteAllBytesAsync(Path.Combine(root, "original.mp4"), [1]);
            await File.WriteAllBytesAsync(Path.Combine(root, "master.mp4"), [2]);
            await File.WriteAllBytesAsync(Path.Combine(root, "original-2.mp4"), [3]);
            await File.WriteAllBytesAsync(Path.Combine(root, "master-2.mp4"), [4]);

            SqliteArtifactStore artifacts = new(connectionString);
            await artifacts.RegisterAsync(
                new ArtifactRecord(originalId, "OriginalVideo", "original.mp4", "original-hash", 1, null, null, "test"),
                CancellationToken.None);
            await artifacts.RegisterAsync(
                new ArtifactRecord(masterId, "master-video", "master.mp4", Hash([2]), 1, null, originalId, "test"),
                CancellationToken.None);
            if (multiSource)
            {
                await artifacts.RegisterAsync(
                    new ArtifactRecord(secondOriginalId, "OriginalVideo", "original-2.mp4", "original-2-hash", 1, null, null, "test"),
                    CancellationToken.None);
                await artifacts.RegisterAsync(
                    new ArtifactRecord(secondMasterId, "master-video", "master-2.mp4", Hash([4]), 1, null, secondOriginalId, "test"),
                    CancellationToken.None);
            }

            CreateJobResult.Succeeded created = Assert.IsType<CreateJobResult.Succeeded>(
                await new CreateJobUseCase(new SqliteJobCreationStore(connectionString)).ExecuteAsync(
                    new CreateJobCommand(
                        multiSource ? [originalId, secondOriginalId] : [originalId],
                        "private-comment",
                        new ProductionSettings(null, false, false))));
            SegmentId segmentId = SegmentId.New();
            SegmentId secondSegmentId = SegmentId.New();
            SourceMapping mapping = new(
                originalId,
                new OriginalRange(2_000_000, 4_000_000),
                masterId,
                new MasterRange(2_000_000, 4_000_000));
            EditPlan plan = new(
                multiSource ? 3 : 1,
                created.JobId,
                created.VariantId,
                RevisionId.New(),
                RevisionId.New(),
                42,
                new OutputProfile(1080, 1920, new FrameRate(30, 1), 60),
                TemplateId.Dynamic,
                new TemplateProps(CaptionPreset.None, ColorTheme.Dark, null),
                new VideoTrack(multiSource
                    ? [
                        new VideoClip(segmentId, mapping with { Original = new OriginalRange(2_000_000, 3_000_000), Master = new MasterRange(2_000_000, 3_000_000) }, new OutputRange(0, 30), CropPreset.CenterCrop),
                        new VideoClip(secondSegmentId, new SourceMapping(secondOriginalId, new OriginalRange(0, 1_000_000), secondMasterId, new MasterRange(0, 1_000_000)), new OutputRange(30, 60), CropPreset.CenterCrop)
                    ]
                    : [new VideoClip(segmentId, mapping, new OutputRange(0, 60), CropPreset.CenterCrop)]),
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
            if (corruptMaster)
                await File.WriteAllBytesAsync(Path.Combine(root, "master.mp4"), [9]);

            StageExecutionResult result = await executor.ExecuteAsync(
                (await new SqliteJobStateStore(connectionString).GetAsync(created.JobId, CancellationToken.None))!,
                CancellationToken.None);

            if (corruptMaster)
            {
                Assert.Equal(StageExecutionKind.Permanent, result.Kind);
                Assert.Equal("render.master_file_changed", result.ErrorCode);
                Assert.Equal(0, host.RenderCount);
                Assert.Empty(audioBuilder.Requests);
                return;
            }

            Assert.Equal(StageExecutionKind.Completed, result.Kind);
            using JsonDocument request = JsonDocument.Parse(Assert.IsType<string>(host.RequestJson));
            JsonElement serializedPlan = request.RootElement.GetProperty("plan");
            Assert.Equal(multiSource ? 2 : 1, request.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(multiSource ? 3 : 2, serializedPlan.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("instagram-reels", serializedPlan.GetProperty("profile").GetString());
            Assert.Equal("dynamic-viral", serializedPlan.GetProperty("style").GetProperty("resolved").GetString());
            JsonElement clip = serializedPlan.GetProperty("video").GetProperty("clips")[0];
            Assert.Equal(masterId.ToString(), clip.GetProperty("sourceAssetId").GetString());
            Assert.Equal(2_000_000, clip.GetProperty("source").GetProperty("startMicroseconds").GetInt64());
            Assert.Equal(multiSource ? 3_000_000 : 4_000_000, clip.GetProperty("master").GetProperty("endMicroseconds").GetInt64());
            Assert.Equal(0.25m, clip.GetProperty("crop").GetProperty("keyframes")[0].GetProperty("focalX").GetDecimal());
            Assert.Equal("visual-director", serializedPlan.GetProperty("effects")[0].GetProperty("sourceSkillId").GetString());
            Assert.Equal("Привет", serializedPlan.GetProperty("captions").GetProperty("groups")[0].GetProperty("text").GetString());
            Assert.Equal(-18m, serializedPlan.GetProperty("audio").GetProperty("targetLoudnessLufs").GetDecimal());

            JsonElement masterAsset = Assert.Single(
                request.RootElement.GetProperty("assets").EnumerateArray(),
                asset => asset.GetProperty(multiSource ? "sourceAssetId" : "id").GetString() == masterId.ToString());
            Assert.Equal(masterId.ToString(), masterAsset.GetProperty(multiSource ? "sourceAssetId" : "id").GetString());
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "master.mp4")), masterAsset.GetProperty(multiSource ? "videoPath" : "path").GetString());
            Assert.Equal(30, masterAsset.GetProperty("frameRate").GetProperty("numerator").GetInt32());
            Assert.Equal(1, masterAsset.GetProperty("frameRate").GetProperty("denominator").GetInt32());
            if (multiSource)
            {
                JsonElement silentAsset = Assert.Single(
                    request.RootElement.GetProperty("assets").EnumerateArray(),
                    asset => asset.GetProperty("sourceAssetId").GetString() == secondMasterId.ToString());
                Assert.Equal(JsonValueKind.Null, silentAsset.GetProperty("audioPath").ValueKind);
            }
            Assert.Single(audioBuilder.Requests);
            Assert.All(audioBuilder.Requests, audioRequest =>
            {
                Assert.Equal(-18, audioRequest.TargetLoudnessLufs);
                Assert.Equal(-2, audioRequest.TruePeakDbtp);
                Assert.True(audioRequest.Denoise);
            });

            StageExecutionResult repeat = await executor.ExecuteAsync(
                (await new SqliteJobStateStore(connectionString).GetAsync(created.JobId, CancellationToken.None))!,
                CancellationToken.None);
            Assert.Equal(StageExecutionKind.Completed, repeat.Kind);
            Assert.Equal(1, host.RenderCount);
            Assert.Single(audioBuilder.Requests);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class CapturingRenderHost : IRenderHostAdapter
    {
        public string? RequestJson { get; private set; }
        public int RenderCount { get; private set; }

        public async Task<RenderHostResult> RenderAsync(RenderHostRequest request, CancellationToken cancellationToken)
        {
            RenderCount++;
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
                request.FilePath.EndsWith("master-2.mp4", StringComparison.OrdinalIgnoreCase)
                    ? []
                    : [new AudioStreamMetadata(1, "aac", 48_000, 2)])));
    }

    private sealed class CapturingAudio : IAudioStemBuilder
    {
        public List<AudioStemRequest> Requests { get; } = [];

        public async Task<AudioStemResult> BuildAsync(AudioStemRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            await File.WriteAllBytesAsync(request.OutputPath, [3], cancellationToken);
            return AudioStemResult.Created(request.OutputPath);
        }
    }

    private static string Hash(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));
}
