using Microsoft.Extensions.Configuration;
using VideoFactory.Bot.Diagnostics;

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

    public void Dispose()
    {
        if (Directory.Exists(tempDirectory))
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static EnvironmentDiagnostic CreateDiagnostic(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new EnvironmentDiagnostic(configuration);
    }
}
