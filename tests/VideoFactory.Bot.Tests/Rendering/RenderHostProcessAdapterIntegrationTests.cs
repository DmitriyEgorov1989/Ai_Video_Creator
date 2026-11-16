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
