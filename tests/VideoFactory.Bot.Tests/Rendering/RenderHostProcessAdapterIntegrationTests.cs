using System.Text.Json.Nodes;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Application.Rendering;
using VideoFactory.VideoProduction.Infrastructure.Processes;
using VideoFactory.VideoProduction.Infrastructure.Rendering;

namespace VideoFactory.Bot.Tests.Rendering;

public sealed class RenderHostProcessAdapterIntegrationTests
{
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

    [Fact]
    public async Task RenderHostProcessAdapter_PreservesVideoAndAudioSourceOrder()
    {
        string repositoryRoot = FindRepositoryRoot();
        string renderHostPath = Path.Combine(repositoryRoot, "render-host", "dist", "cli.js");
        Assert.True(File.Exists(renderHostPath), $"Build render-host before running this test: {renderHostPath}");

        string jobDirectory = Path.Combine(Path.GetTempPath(), $"video-factory-markers-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobDirectory);
        string sourcePath = Path.Combine(jobDirectory, "markers.mp4");
        string outputPath = Path.Combine(jobDirectory, "output.mp4");
        string requestPath = Path.Combine(jobDirectory, "request.json");
        string firstFramePath = Path.Combine(jobDirectory, "first.rgb");
        string secondFramePath = Path.Combine(jobDirectory, "second.rgb");
        string firstAudioPath = Path.Combine(jobDirectory, "first.pcm");
        string secondAudioPath = Path.Combine(jobDirectory, "second.pcm");

        try
        {
            SystemProcessRunner processRunner = new();
            ProcessExecutionResult sourceResult = await processRunner.RunAsync(
                new ProcessStartRequest(
                    "ffmpeg",
                    [
                        "-y",
                        "-f", "lavfi", "-i", "color=c=red:s=320x568:r=24:d=1",
                        "-f", "lavfi", "-i", "color=c=blue:s=320x568:r=24:d=1",
                        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1",
                        "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=1",
                        "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v];[2:a][3:a]concat=n=2:v=0:a=1[a]",
                        "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-pix_fmt", "yuv420p",
                        "-r", "24", "-c:a", "aac", sourcePath
                    ],
                    TimeSpan.FromSeconds(30),
                    32_768),
                CancellationToken.None);
            Assert.True(sourceResult.IsSuccess, sourceResult.ErrorMessage ?? sourceResult.StandardError);

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
                    CreateClip("segment-1", 0, 1_000_000, 0, 30),
                    CreateClip("segment-2", 1_000_000, 2_000_000, 30, 60),
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
            await File.WriteAllTextAsync(requestPath, request.ToJsonString());

            RenderHostProcessAdapter adapter = new(processRunner, "node", renderHostPath);
            RenderHostResult result = await adapter.RenderAsync(
                new RenderHostRequest(requestPath, outputPath, TimeSpan.FromMinutes(5), 65_536),
                CancellationToken.None);

            Assert.True(
                result.Kind == RenderHostResultKind.Succeeded,
                $"{result.ErrorCode}: {result.ErrorMessage}\nstdout:\n{result.StandardOutput}\nstderr:\n{result.StandardError}");

            await ExtractFrameAsync(processRunner, outputPath, firstFramePath, "0.5");
            await ExtractFrameAsync(processRunner, outputPath, secondFramePath, "1.5");
            byte[] firstFrame = await File.ReadAllBytesAsync(firstFramePath);
            byte[] secondFrame = await File.ReadAllBytesAsync(secondFramePath);
            Assert.Equal(3, firstFrame.Length);
            Assert.Equal(3, secondFrame.Length);
            Assert.True(firstFrame[0] > firstFrame[2] * 2, $"Expected red first frame, got RGB({firstFrame[0]}, {firstFrame[1]}, {firstFrame[2]}).");
            Assert.True(secondFrame[2] > secondFrame[0] * 2, $"Expected blue second frame, got RGB({secondFrame[0]}, {secondFrame[1]}, {secondFrame[2]}).");

            await ExtractAudioAsync(processRunner, outputPath, firstAudioPath, "0.25");
            await ExtractAudioAsync(processRunner, outputPath, secondAudioPath, "1.25");
            int firstZeroCrossings = CountZeroCrossings(await File.ReadAllBytesAsync(firstAudioPath));
            int secondZeroCrossings = CountZeroCrossings(await File.ReadAllBytesAsync(secondAudioPath));
            Assert.InRange(firstZeroCrossings, 350, 550);
            Assert.InRange(secondZeroCrossings, 700, 1_000);
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

    private static async Task ExtractFrameAsync(SystemProcessRunner runner, string inputPath, string outputPath, string timestamp)
    {
        ProcessExecutionResult result = await runner.RunAsync(
            new ProcessStartRequest("ffmpeg", ["-y", "-ss", timestamp, "-i", inputPath, "-frames:v", "1", "-vf", "scale=1:1,format=rgb24", "-f", "rawvideo", outputPath], TimeSpan.FromSeconds(30), 32_768),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorMessage ?? result.StandardError);
    }

    private static async Task ExtractAudioAsync(SystemProcessRunner runner, string inputPath, string outputPath, string timestamp)
    {
        ProcessExecutionResult result = await runner.RunAsync(
            new ProcessStartRequest("ffmpeg", ["-y", "-ss", timestamp, "-t", "0.5", "-i", inputPath, "-vn", "-ac", "1", "-ar", "48000", "-f", "s16le", outputPath], TimeSpan.FromSeconds(30), 32_768),
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.ErrorMessage ?? result.StandardError);
    }

    private static int CountZeroCrossings(byte[] pcm)
    {
        int count = 0;
        short previous = BitConverter.ToInt16(pcm, 0);
        for (int index = 2; index < pcm.Length; index += 2)
        {
            short current = BitConverter.ToInt16(pcm, index);
            if ((previous < 0 && current >= 0) || (previous >= 0 && current < 0)) count++;
            previous = current;
        }

        return count;
    }

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
