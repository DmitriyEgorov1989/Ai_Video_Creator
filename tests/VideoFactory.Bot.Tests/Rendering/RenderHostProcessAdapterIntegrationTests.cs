using System.Text.Json.Nodes;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Infrastructure.Processes;
using VideoFactory.VideoProduction.Infrastructure.Rendering;
using VideoFactory.VideoProduction.IntegrationTests.Media;

namespace VideoFactory.Bot.Tests.Rendering;

public sealed class RenderHostProcessAdapterIntegrationTests
{
    [Fact]
    public async Task RealRemotionFailureAndTimeoutLeaveNoOutputAndRetrySucceeds()
    {
        string repositoryRoot = FindRepositoryRoot();
        string renderHostPath = Path.Combine(repositoryRoot, "render-host", "dist", "cli.js");
        Assert.True(File.Exists(renderHostPath), $"Build render-host before running this test: {renderHostPath}");
        string jobDirectory = Path.Combine(Path.GetTempPath(), $"video-factory-render-fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobDirectory);
        string sourcePath = Path.Combine(jobDirectory, "source.mp4");
        string outputPath = Path.Combine(jobDirectory, "output.mp4");
        string requestPath = Path.Combine(jobDirectory, "request.json");

        try
        {
            JsonObject request = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "contracts", "fixtures", "valid-render-request.json")))!.AsObject();
            request["outputPath"] = outputPath;
            request["assets"]!.AsArray()[0]!["path"] = sourcePath;
            JsonObject plan = request["plan"]!.AsObject();
            plan["output"]!.AsObject()["durationFrames"] = 30;
            plan["video"]!.AsObject()["clips"]!.AsArray()[0]!.AsObject()["output"]!.AsObject()["endFrame"] = 30;
            await File.WriteAllTextAsync(requestPath, request.ToJsonString());
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
            SystemProcessRunner processRunner = new();
            RenderHostProcessAdapter adapter = new(processRunner, "node", renderHostPath);

            RenderHostResult failed = await adapter.RenderAsync(
                new RenderHostRequest(requestPath, outputPath, TimeSpan.FromMinutes(2), 65_536),
                CancellationToken.None);

            Assert.Equal(RenderHostResultKind.Failed, failed.Kind);
            Assert.False(File.Exists(outputPath));

            ProcessExecutionResult sourceResult = await processRunner.RunAsync(
                new ProcessStartRequest(
                    "ffmpeg",
                    [
                        "-y", "-f", "lavfi", "-i", "color=c=0x304050:s=320x568:d=1",
                        "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-shortest",
                        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", sourcePath
                    ],
                    TimeSpan.FromSeconds(30),
                    32_768),
                CancellationToken.None);
            Assert.True(sourceResult.IsSuccess, sourceResult.ErrorMessage ?? sourceResult.StandardError);

            RenderHostResult timedOut = await adapter.RenderAsync(
                new RenderHostRequest(requestPath, outputPath, TimeSpan.FromMilliseconds(1), 65_536),
                CancellationToken.None);

            Assert.Equal(RenderHostResultKind.Failed, timedOut.Kind);
            Assert.Equal("process.timeout", timedOut.ErrorCode);
            Assert.False(File.Exists(outputPath));

            RenderHostResult retried = await adapter.RenderAsync(
                new RenderHostRequest(requestPath, outputPath, TimeSpan.FromMinutes(5), 65_536),
                CancellationToken.None);

            Assert.Equal(RenderHostResultKind.Succeeded, retried.Kind);
            Assert.True(new FileInfo(outputPath).Length > 0);
            Assert.Single(Directory.GetFiles(jobDirectory, "output.mp4", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            if (Directory.Exists(jobDirectory))
            {
                Directory.Delete(jobDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderHostProcessAdapter_V2FocalCropRetainsSelectedSubject()
    {
        string repositoryRoot = FindRepositoryRoot();
        string renderHostPath = Path.Combine(repositoryRoot, "render-host", "dist", "cli.js");
        Assert.True(File.Exists(renderHostPath), $"Build render-host before running this test: {renderHostPath}");

        string jobDirectory = Path.Combine(Path.GetTempPath(), $"video-factory-v2-crop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobDirectory);
        string sourcePath = Path.Combine(jobDirectory, "source.mp4");
        string outputPath = Path.Combine(jobDirectory, "output.mp4");
        string requestPath = Path.Combine(jobDirectory, "request.json");

        try
        {
            SystemProcessRunner processRunner = new();
            ProcessExecutionResult sourceResult = await processRunner.RunAsync(
                new ProcessStartRequest(
                    "ffmpeg",
                    [
                        "-y", "-f", "lavfi", "-i",
                        "color=c=0x808080:s=640x360:r=30:d=1,drawbox=x=20:y=130:w=80:h=80:c=lime:t=fill",
                        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                        "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", sourcePath
                    ],
                    TimeSpan.FromSeconds(30),
                    32_768),
                CancellationToken.None);
            Assert.True(sourceResult.IsSuccess, sourceResult.ErrorMessage ?? sourceResult.StandardError);

            JsonObject request = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "contracts", "fixtures", "valid-render-request.json")))!.AsObject();
            request["outputPath"] = outputPath;
            request["assets"]!.AsArray()[0]!["path"] = sourcePath;
            request["assets"]!.AsArray()[0]!["frameRate"] = new JsonObject { ["numerator"] = 30, ["denominator"] = 1 };
            request["plan"] = CreateV2Plan(
                new JsonObject { ["preset"] = "fill", ["focalX"] = 0, ["focalY"] = 0.5, ["zoom"] = 1 });
            await File.WriteAllTextAsync(requestPath, request.ToJsonString());

            RenderHostProcessAdapter adapter = new(processRunner, "node", renderHostPath);
            RenderHostResult result = await adapter.RenderAsync(
                new RenderHostRequest(requestPath, outputPath, TimeSpan.FromMinutes(5), 65_536),
                CancellationToken.None);

            Assert.True(
                result.Kind == RenderHostResultKind.Succeeded,
                $"{result.ErrorCode}: {result.ErrorMessage}\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");
            RgbSample subject = await MediaMeasurements.SampleRegionAsync(
                processRunner,
                outputPath,
                TimeSpan.FromSeconds(0.5),
                new PixelRegion(150, 750, 250, 250),
                CancellationToken.None);
            MontageQualityAssertions.RequireDominantColor("V2 focal subject", subject, DominantColor.Green);
            PixelRegion captionRegion = new(70, 1560, 940, 160);
            RgbSample beforeCaption = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.05), captionRegion, CancellationToken.None);
            RgbSample visibleCaption = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.4), captionRegion, CancellationToken.None);
            MontageQualityAssertions.RequireOverlayContrast("V2 Cyrillic safe caption", beforeCaption, visibleCaption, 20);
            PixelRegion fullFrame = new(0, 0, 1080, 1920);
            RgbSample beforeTransition = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.7), fullFrame, CancellationToken.None);
            RgbSample duringTransition = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.83), fullFrame, CancellationToken.None);
            MontageQualityAssertions.RequireOverlayContrast("V2 visual transition", beforeTransition, duringTransition, 20);
            ToneMeasurement fadeIn = await MediaMeasurements.MeasureToneAsync(
                processRunner, outputPath, TimeSpan.Zero, TimeSpan.FromSeconds(0.1), CancellationToken.None);
            ToneMeasurement steady = await MediaMeasurements.MeasureToneAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.1), CancellationToken.None);
            MontageQualityAssertions.RequireTone("V2 synchronized source audio", steady, 440, 5);
            Assert.True(steady.RmsDecibels - fadeIn.RmsDecibels > 3, $"Expected measurable fade-in, observed {fadeIn.RmsDecibels:0.##} dBFS then {steady.RmsDecibels:0.##} dBFS.");
        }
        finally
        {
            if (Directory.Exists(jobDirectory))
            {
                Directory.Delete(jobDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderHostProcessAdapter_CreatesValidMp4ThroughBuiltNodeHost()
    {
        string repositoryRoot = FindRepositoryRoot();
        string renderHostPath = Path.Combine(repositoryRoot, "render-host", "dist", "cli.js");
        Assert.True(File.Exists(renderHostPath), $"Build render-host before running this test: {renderHostPath}");

        string jobDirectory = Path.Combine(Path.GetTempPath(), $"video-factory-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobDirectory);
        string sourcePath = Path.Combine(jobDirectory, "source.mp4");
        string outputPath = Path.Combine(jobDirectory, "output.mp4");
        string requestPath = Path.Combine(jobDirectory, "request.json");

        try
        {
            SystemProcessRunner processRunner = new();
            ProcessExecutionResult sourceResult = await processRunner.RunAsync(
                new ProcessStartRequest(
                    "ffmpeg",
                    [
                        "-y", "-f", "lavfi", "-i", "color=c=0x304050:s=320x568:d=1",
                        "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-shortest",
                        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", sourcePath
                    ],
                    TimeSpan.FromSeconds(30),
                    32_768),
                CancellationToken.None);
            Assert.True(sourceResult.IsSuccess, sourceResult.ErrorMessage ?? sourceResult.StandardError);

            JsonObject request = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "contracts", "fixtures", "valid-render-request.json")))!.AsObject();
            request["outputPath"] = outputPath;
            request["assets"]!.AsArray()[0]!["path"] = sourcePath;
            JsonObject plan = request["plan"]!.AsObject();
            plan["output"]!.AsObject()["durationFrames"] = 30;
            plan["video"]!.AsObject()["clips"]!.AsArray()[0]!.AsObject()["output"]!.AsObject()["endFrame"] = 30;
            await File.WriteAllTextAsync(requestPath, request.ToJsonString());

            RenderHostProcessAdapter adapter = new(processRunner, "node", renderHostPath);
            RenderHostResult result = await adapter.RenderAsync(
                new RenderHostRequest(requestPath, outputPath, TimeSpan.FromMinutes(5), 65_536),
                CancellationToken.None);

            Assert.True(
                result.Kind == RenderHostResultKind.Succeeded,
                $"{result.ErrorCode}: {result.ErrorMessage}\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");
            Assert.Equal(30, result.DurationFrames);
            Assert.True(File.Exists(outputPath));
            Assert.True(new FileInfo(outputPath).Length > 0);

            ProcessExecutionResult probe = await processRunner.RunAsync(
                new ProcessStartRequest(
                    "ffprobe",
                    ["-v", "error", "-show_entries", "stream=codec_type,codec_name,width,height", "-show_entries", "format=duration", "-of", "json", outputPath],
                    TimeSpan.FromSeconds(30),
                    32_768),
                CancellationToken.None);
            Assert.True(probe.IsSuccess, probe.ErrorMessage ?? probe.StandardError);
            Assert.Contains("h264", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("1080", probe.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("1920", probe.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("duration", probe.StandardOutput, StringComparison.OrdinalIgnoreCase);

            ProcessExecutionResult decode = await processRunner.RunAsync(
                new ProcessStartRequest(
                    "ffmpeg",
                    ["-v", "error", "-i", outputPath, "-map", "0", "-f", "null", "-"],
                    TimeSpan.FromSeconds(30),
                    32_768),
                CancellationToken.None);
            Assert.True(decode.IsSuccess, decode.ErrorMessage ?? decode.StandardError);
        }
        finally
        {
            if (Directory.Exists(jobDirectory))
            {
                Directory.Delete(jobDirectory, recursive: true);
            }
        }
    }

    [VideoFactory.Bot.Tests.Acceptance.MediaAcceptanceFact]
    public async Task RenderHostProcessAdapter_PreservesVideoAndAudioSourceOrder()
    {
        string repositoryRoot = FindRepositoryRoot();
        string renderHostPath = Path.Combine(repositoryRoot, "render-host", "dist", "cli.js");
        Assert.True(File.Exists(renderHostPath), $"Build render-host before running this test: {renderHostPath}");

        string jobDirectory = Path.Combine(Path.GetTempPath(), $"video-factory-markers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobDirectory);
        string mediaRoot = Environment.GetEnvironmentVariable("ACCEPTANCE_MEDIA_ROOT")!;
        MediaOracleDocument oracle = await MediaOracleContract.LoadAsync(mediaRoot, CancellationToken.None);
        MediaOracleMontageCase montageCase = Assert.Single(oracle.MontageCases);
        string sourcePath = Path.Combine(mediaRoot, montageCase.Fixture);
        string outputPath = Path.Combine(jobDirectory, "output.mp4");
        string requestPath = Path.Combine(jobDirectory, "request.json");

        try
        {
            SystemProcessRunner processRunner = new();
            JsonObject request = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "contracts", "fixtures", "valid-render-request.json")))!.AsObject();
            request["outputPath"] = outputPath;
            request["assets"]!.AsArray()[0]!["path"] = sourcePath;
            request["assets"]!.AsArray()[0]!["frameRate"] = new JsonObject { ["numerator"] = 24, ["denominator"] = 1 };
            request["assets"]!.AsArray().Add(new JsonObject { ["id"] = "__audio_master", ["path"] = sourcePath });

            JsonObject plan = request["plan"]!.AsObject();
            plan["output"]!.AsObject()["durationFrames"] = 60;
            plan["video"] = new JsonObject
            {
                ["clips"] = new JsonArray
                {
                    CreateClip("segment-1", montageCase.Clips[0].SourceStartMicroseconds, montageCase.Clips[0].SourceEndMicroseconds, montageCase.Clips[0].OutputStartFrame, montageCase.Clips[0].OutputEndFrame),
                    CreateClip("segment-2", montageCase.Clips[1].SourceStartMicroseconds, montageCase.Clips[1].SourceEndMicroseconds, montageCase.Clips[1].OutputStartFrame, montageCase.Clips[1].OutputEndFrame),
                },
            };
            plan["audio"] = new JsonObject
            {
                ["sourceClips"] = new JsonArray
                {
                    new JsonObject { ["segmentId"] = "segment-1", ["output"] = CreateRange(0, 30), ["gain"] = 1 },
                    new JsonObject { ["segmentId"] = "segment-2", ["output"] = CreateRange(30, 60), ["gain"] = 1 },
                },
                ["musicClips"] = new JsonArray(),
            };
            plan["captions"] = new JsonObject
            {
                ["items"] = new JsonArray
                {
                    new JsonObject { ["text"] = montageCase.Caption.Text, ["output"] = CreateRange(montageCase.Caption.StartFrame, montageCase.Caption.EndFrame) },
                },
            };
            await File.WriteAllTextAsync(requestPath, request.ToJsonString());

            RenderHostProcessAdapter adapter = new(processRunner, "node", renderHostPath);
            RenderHostResult result = await adapter.RenderAsync(
                new RenderHostRequest(requestPath, outputPath, TimeSpan.FromMinutes(5), 65_536),
                CancellationToken.None);

            Assert.True(
                result.Kind == RenderHostResultKind.Succeeded,
                $"{result.ErrorCode}: {result.ErrorMessage}\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");

            PixelRegion fullFrame = new(0, 0, 1080, 1920);
            RgbSample firstFrame = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.5), fullFrame, CancellationToken.None);
            RgbSample secondFrame = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(1.5), fullFrame, CancellationToken.None);
            MontageQualityAssertions.RequireDominantColor("first clip", firstFrame, ParseColor(montageCase.Clips[0].ExpectedColor));
            MontageQualityAssertions.RequireDominantColor("second clip", secondFrame, ParseColor(montageCase.Clips[1].ExpectedColor));

            RgbSample beforeTransition = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.98), fullFrame, CancellationToken.None);
            RgbSample afterTransition = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(1.02), fullFrame, CancellationToken.None);
            RgbSample atTransition = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(1), fullFrame, CancellationToken.None);
            MontageQualityAssertions.RequireNonBlack("frame before clip boundary", beforeTransition, 10);
            MontageQualityAssertions.RequireNonBlack("frame at clip boundary", atTransition, 10);
            MontageQualityAssertions.RequireNonBlack("frame after clip boundary", afterTransition, 10);

            PixelRegion cropSafeRegion = new(montageCase.CropSafeRegion.X, montageCase.CropSafeRegion.Y, montageCase.CropSafeRegion.Width, montageCase.CropSafeRegion.Height);
            RgbSample retainedFocalMarker = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.25), cropSafeRegion, CancellationToken.None);
            MontageQualityAssertions.RequireDominantColor(
                "crop-safe focal marker", retainedFocalMarker, ParseColor(montageCase.CropSafeRegion.ExpectedColor));

            PixelRegion captionRegion = new(50, 1550, 980, 220);
            RgbSample beforeCaption = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.2), captionRegion, CancellationToken.None);
            RgbSample visibleCaption = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.5), captionRegion, CancellationToken.None);
            RgbSample afterCaption = await MediaMeasurements.SampleRegionAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.9), captionRegion, CancellationToken.None);
            MontageQualityAssertions.RequireOverlayContrast(
                "caption start at frame 10", beforeCaption, visibleCaption, beforeCaption.Red * 0.2);
            MontageQualityAssertions.RequireOverlayContrast(
                "caption end at frame 20", afterCaption, visibleCaption, afterCaption.Red * 0.2);

            ToneMeasurement firstTone = await MediaMeasurements.MeasureToneAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(0.5), CancellationToken.None);
            ToneMeasurement secondTone = await MediaMeasurements.MeasureToneAsync(
                processRunner, outputPath, TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(0.5), CancellationToken.None);
            MontageQualityAssertions.RequireTone("first source interval", firstTone, montageCase.Clips[0].ExpectedFrequencyHz, oracle.Tolerances.FrequencyHz);
            MontageQualityAssertions.RequireTone("third source interval after removed middle", secondTone, montageCase.Clips[1].ExpectedFrequencyHz, oracle.Tolerances.FrequencyHz);

            string wrongTrimPath = Path.Combine(jobDirectory, "wrong-trim.mp4");
            request["outputPath"] = wrongTrimPath;
            request["plan"]!["video"]!["clips"]![1]!["mapping"]!["original"] = CreateTimeRange(montageCase.Removed.SourceStartMicroseconds, montageCase.Removed.SourceEndMicroseconds);
            request["plan"]!["video"]!["clips"]![1]!["mapping"]!["master"] = CreateTimeRange(montageCase.Removed.SourceStartMicroseconds, montageCase.Removed.SourceEndMicroseconds);
            await File.WriteAllTextAsync(requestPath, request.ToJsonString());
            RenderHostResult wrongTrim = await adapter.RenderAsync(new RenderHostRequest(requestPath, wrongTrimPath, TimeSpan.FromMinutes(5), 65_536), CancellationToken.None);
            Assert.Equal(RenderHostResultKind.Succeeded, wrongTrim.Kind);
            RgbSample wrongTrimFrame = await MediaMeasurements.SampleRegionAsync(processRunner, wrongTrimPath, TimeSpan.FromSeconds(1.5), fullFrame, CancellationToken.None);
            ToneMeasurement wrongTrimTone = await MediaMeasurements.MeasureToneAsync(processRunner, wrongTrimPath, TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(0.5), CancellationToken.None);
            Assert.Throws<InvalidDataException>(() => MontageQualityAssertions.RequireDominantColor("wrong trim", wrongTrimFrame, ParseColor(montageCase.Clips[1].ExpectedColor)));
            Assert.Throws<InvalidDataException>(() => MontageQualityAssertions.RequireTone("audio leakage", wrongTrimTone, montageCase.Clips[1].ExpectedFrequencyHz, oracle.Tolerances.FrequencyHz));

            string wrongOrderPath = Path.Combine(jobDirectory, "wrong-order.mp4");
            request["outputPath"] = wrongOrderPath;
            JsonArray clips = request["plan"]!["video"]!["clips"]!.AsArray();
            clips[0] = CreateClip("segment-1", montageCase.Clips[1].SourceStartMicroseconds, montageCase.Clips[1].SourceEndMicroseconds, 0, 30);
            clips[1] = CreateClip("segment-2", montageCase.Clips[0].SourceStartMicroseconds, montageCase.Clips[0].SourceEndMicroseconds, 30, 60);
            await File.WriteAllTextAsync(requestPath, request.ToJsonString());
            RenderHostResult wrongOrder = await adapter.RenderAsync(new RenderHostRequest(requestPath, wrongOrderPath, TimeSpan.FromMinutes(5), 65_536), CancellationToken.None);
            Assert.Equal(RenderHostResultKind.Succeeded, wrongOrder.Kind);
            RgbSample wrongOrderFrame = await MediaMeasurements.SampleRegionAsync(processRunner, wrongOrderPath, TimeSpan.FromSeconds(0.5), fullFrame, CancellationToken.None);
            Assert.Throws<InvalidDataException>(() => MontageQualityAssertions.RequireDominantColor("wrong order", wrongOrderFrame, ParseColor(montageCase.Clips[0].ExpectedColor)));
        }
        finally
        {
            if (Directory.Exists(jobDirectory))
            {
                Directory.Delete(jobDirectory, recursive: true);
            }
        }
    }

    private static JsonObject CreateClip(string segmentId, long startMicroseconds, long endMicroseconds, int outputStart, int outputEnd)
    {
        return new JsonObject
        {
            ["segmentId"] = segmentId,
            ["mapping"] = new JsonObject
            {
                ["originalAssetId"] = "original-1",
                ["original"] = CreateTimeRange(startMicroseconds, endMicroseconds),
                ["masterAssetId"] = "master-1",
                ["master"] = CreateTimeRange(startMicroseconds, endMicroseconds),
            },
            ["output"] = CreateRange(outputStart, outputEnd),
            ["crop"] = "centerCrop",
        };
    }

    private static JsonObject CreateRange(long start, long end)
    {
        return new JsonObject { ["startFrame"] = start, ["endFrame"] = end };
    }

    private static JsonObject CreateTimeRange(long startMicroseconds, long endMicroseconds)
    {
        return new JsonObject { ["startMicroseconds"] = startMicroseconds, ["endMicroseconds"] = endMicroseconds };
    }

    private static JsonObject CreateV2Plan(JsonObject crop)
    {
        return new JsonObject
        {
            ["schemaVersion"] = 2,
            ["jobId"] = "job-1",
            ["variantId"] = "variant-1",
            ["revisionId"] = "revision-1",
            ["analysisRevisionId"] = "analysis-1",
            ["output"] = new JsonObject
            {
                ["width"] = 1080,
                ["height"] = 1920,
                ["frameRate"] = new JsonObject { ["numerator"] = 30, ["denominator"] = 1 },
                ["durationFrames"] = 30,
            },
            ["source"] = new JsonObject { ["originalAssetId"] = "original-1", ["masterAssetId"] = "master-1" },
            ["video"] = new JsonObject
            {
                ["clips"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["sourceAssetId"] = "master-1",
                        ["source"] = CreateTimeRange(0, 1_000_000),
                        ["master"] = CreateTimeRange(0, 1_000_000),
                        ["output"] = CreateRange(0, 30),
                        ["crop"] = crop,
                        ["effects"] = new JsonArray("transition-1"),
                    },
                },
            },
            ["effects"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "transition-1",
                    ["type"] = "visualTransition",
                    ["output"] = CreateRange(22, 28),
                    ["sourceSkillId"] = "visual-director",
                    ["parameters"] = new JsonObject { ["amount"] = 0.5 },
                },
            },
            ["captions"] = new JsonObject
            {
                ["words"] = new JsonArray(),
                ["groups"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["text"] = "Привет мир",
                        ["words"] = new JsonArray
                        {
                            new JsonObject { ["text"] = "Привет", ["output"] = CreateRange(5, 12) },
                            new JsonObject { ["text"] = "мир", ["output"] = CreateRange(12, 20) },
                        },
                        ["output"] = CreateRange(5, 20),
                        ["maxLines"] = 2,
                        ["highlight"] = "activeWord",
                        ["animation"] = "fade",
                        ["placement"] = "bottomSafe",
                    },
                },
            },
            ["audio"] = new JsonObject
            {
                ["sourceGain"] = 1,
                ["targetLoudnessLufs"] = -16,
                ["truePeakDbtp"] = -1,
                ["fadeInFrames"] = 6,
                ["fadeOutFrames"] = 6,
                ["denoise"] = false,
            },
            ["skills"] = new JsonArray
            {
                new JsonObject { ["id"] = "visual-director", ["version"] = "1.0.0", ["status"] = "applied" },
                new JsonObject { ["id"] = "caption-director", ["version"] = "1.0.0", ["status"] = "applied" },
                new JsonObject { ["id"] = "audio-director", ["version"] = "1.0.0", ["status"] = "applied" },
            },
        };
    }

    private static DominantColor ParseColor(string value) => value switch
    {
        "red" => DominantColor.Red,
        "green" => DominantColor.Green,
        "blue" => DominantColor.Blue,
        _ => throw new InvalidDataException($"Unsupported oracle color '{value}'."),
    };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ai_Video_Creator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
