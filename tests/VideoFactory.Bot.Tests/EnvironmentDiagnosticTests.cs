using Microsoft.Extensions.Configuration;
using VideoFactory.Bot.Diagnostics;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.Bot.Tests;

public sealed class EnvironmentDiagnosticTests : IDisposable
{
    private readonly string tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"video-factory-tests-{Guid.NewGuid():N}");

    [Fact]
    public void MissingDependenciesAreAllReported()
    {
        EnvironmentDiagnostic diagnostic = CreateDiagnostic(new Dictionary<string, string?>
        {
            ["FFmpeg:FfmpegPath"] = Path.Combine(tempDirectory, "missing-ffmpeg.exe"),
            ["FFmpeg:FfprobePath"] = Path.Combine(tempDirectory, "missing-ffprobe.exe")
        });

        DiagnosticReport report = diagnostic.Run();

        Assert.False(report.IsSuccessful);
        Assert.Contains(report.Checks, check => check.Name == "ffmpeg" && check.Status == DiagnosticStatus.Failed);
        Assert.Contains(report.Checks, check => check.Name == "ffprobe" && check.Status == DiagnosticStatus.Failed);
    }

    [Fact]
    public void ExistingExecutablePathPassesCheck()
    {
        Directory.CreateDirectory(tempDirectory);
        string executablePath = Path.Combine(tempDirectory, "tool.exe");
        File.WriteAllText(executablePath, string.Empty);

        EnvironmentDiagnostic diagnostic = CreateDiagnostic(new Dictionary<string, string?>
        {
            ["FFmpeg:FfmpegPath"] = executablePath
        });

        DiagnosticReport report = diagnostic.Run();

        Assert.Contains(report.Checks, check => check.Name == "ffmpeg" && check.Status == DiagnosticStatus.Passed);
    }

    [Fact]
    public void ReportDoesNotContainSecretValues()
    {
        const string token = "super-secret-token";
        const string apiKey = "super-secret-api-key";
        EnvironmentDiagnostic diagnostic = CreateDiagnostic(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = token,
            ["AiApi:ApiKey"] = apiKey
        });

        DiagnosticReport report = diagnostic.Run();
        string reportText = string.Join(Environment.NewLine, report.Checks.Select(check => check.Message));

        Assert.DoesNotContain(token, reportText, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, reportText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsyncStartsConfiguredRenderHostEntryPoint()
    {
        Directory.CreateDirectory(Path.Combine(tempDirectory, "render-host", "dist"));
        string renderHostPath = Path.Combine(tempDirectory, "render-host", "dist", "cli.js");
        File.WriteAllText(renderHostPath, string.Empty);
        RecordingProcessRunner processRunner = new();
        EnvironmentDiagnostic diagnostic = CreateDiagnostic(
            new Dictionary<string, string?>
            {
                ["Paths:WorkspaceRoot"] = tempDirectory,
                ["Remotion:NodePath"] = "node",
                ["Remotion:RenderHostPath"] = "render-host/dist/cli.js"
            },
            processRunner);

        DiagnosticReport report = await diagnostic.RunAsync(CancellationToken.None);

        Assert.Contains(report.Checks, check => check.Name == "render-host-runtime" && check.Status == DiagnosticStatus.Passed);
        Assert.Equal(renderHostPath, processRunner.Requests[^1].Arguments[0]);
        Assert.Equal("list-templates", processRunner.Requests[^1].Arguments[1]);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static EnvironmentDiagnostic CreateDiagnostic(
        Dictionary<string, string?> values,
        IProcessRunner? processRunner = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new EnvironmentDiagnostic(configuration, processRunner: processRunner);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessStartRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessStartRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessExecutionResult(
                ProcessExecutionStatus.Succeeded,
                0,
                "{\"templates\":[{\"id\":\"dynamic\"}]}\n",
                string.Empty,
                null,
                null,
                false,
                false));
        }
    }
}
