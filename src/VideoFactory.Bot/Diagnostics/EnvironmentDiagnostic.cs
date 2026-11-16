using System.Text.Json;
using VideoFactory.Bot.Configuration;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.Bot.Diagnostics;

public sealed class EnvironmentDiagnostic
{
    private readonly IConfiguration configuration;
    private readonly string contentRootPath;
    private readonly IProcessRunner? processRunner;

    public EnvironmentDiagnostic(
        IConfiguration configuration,
        string? contentRootPath = null,
        IProcessRunner? processRunner = null)
    {
        this.configuration = configuration;
        this.contentRootPath = contentRootPath ?? Directory.GetCurrentDirectory();
        this.processRunner = processRunner;
    }

    public DiagnosticReport Run()
    {
        List<DiagnosticCheck> checks = [];
        TelegramOptions telegram = GetOptions<TelegramOptions>(TelegramOptions.SectionName);
        PathsOptions paths = GetOptions<PathsOptions>(PathsOptions.SectionName);
        StorageOptions storage = GetOptions<StorageOptions>(StorageOptions.SectionName);
        LimitsOptions limits = GetOptions<LimitsOptions>(LimitsOptions.SectionName);
        FfmpegOptions ffmpeg = GetOptions<FfmpegOptions>(FfmpegOptions.SectionName);
        WhisperOptions whisper = GetOptions<WhisperOptions>(WhisperOptions.SectionName);
        RemotionOptions remotion = GetOptions<RemotionOptions>(RemotionOptions.SectionName);
        AiApiOptions aiApi = GetOptions<AiApiOptions>(AiApiOptions.SectionName);

        CheckSecret(checks, "telegram-token", telegram.BotToken, "Telegram:BotToken");
        AddCondition(checks, "telegram-user", telegram.AllowedUserId > 0,
            "Telegram:AllowedUserId is configured.",
            "Telegram:AllowedUserId must be greater than zero.");
        string workspaceRoot = ResolvePath(paths.WorkspaceRoot, contentRootPath);
        AddCondition(checks, "workspace-root", Directory.Exists(workspaceRoot),
            $"Workspace root found: {workspaceRoot}",
            $"Workspace root was not found: {workspaceRoot}");
        AddCondition(checks, "storage-root", !string.IsNullOrWhiteSpace(storage.DataRoot),
            $"Storage root is configured: {storage.DataRoot}",
            "Storage:DataRoot is not configured.");
        AddCondition(checks, "limits", LimitsAreValid(limits),
            "Processing limits are valid.",
            "Processing limits must be positive.");

        CheckExecutable(checks, "ffmpeg", ffmpeg.FfmpegPath, workspaceRoot);
        CheckExecutable(checks, "ffprobe", ffmpeg.FfprobePath, workspaceRoot);
        CheckExecutable(checks, "python", whisper.PythonPath, workspaceRoot);
        CheckFile(checks, "whisper-worker", whisper.WorkerPath, workspaceRoot);
        CheckExecutable(checks, "node", remotion.NodePath, workspaceRoot);
        CheckFile(checks, "render-host", remotion.RenderHostPath, workspaceRoot);
        CheckAiApi(checks, aiApi);

        return new DiagnosticReport(checks);
    }

    /// <summary>
    /// Проверяет наличие и фактический запуск FFmpeg и FFprobe.
    /// </summary>
    public async Task<DiagnosticReport> RunAsync(CancellationToken cancellationToken)
    {
        DiagnosticReport report = Run();
        if (processRunner is null)
        {
            return report;
        }

        List<DiagnosticCheck> checks = [.. report.Checks];
        FfmpegOptions ffmpeg = GetOptions<FfmpegOptions>(FfmpegOptions.SectionName);
        PathsOptions paths = GetOptions<PathsOptions>(PathsOptions.SectionName);
        RemotionOptions remotion = GetOptions<RemotionOptions>(RemotionOptions.SectionName);
        string workspaceRoot = ResolvePath(paths.WorkspaceRoot, contentRootPath);
        await AddVersionCheckAsync(checks, "ffmpeg-version", ffmpeg.FfmpegPath, cancellationToken);
        await AddVersionCheckAsync(checks, "ffprobe-version", ffmpeg.FfprobePath, cancellationToken);
        await AddRenderHostCheckAsync(checks, remotion, workspaceRoot, cancellationToken);
        return new DiagnosticReport(checks);
    }

    private async Task AddRenderHostCheckAsync(
        List<DiagnosticCheck> checks,
        RemotionOptions options,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        string renderHostPath = ResolvePath(options.RenderHostPath, workspaceRoot);
        ProcessExecutionResult result = await processRunner!.RunAsync(
            new ProcessStartRequest(
                options.NodePath,
                [renderHostPath, "list-templates"],
                TimeSpan.FromSeconds(30),
                16_384),
            cancellationToken);

        if (!result.IsSuccess)
        {
            checks.Add(new DiagnosticCheck(
                "render-host-runtime",
                DiagnosticStatus.Failed,
                result.ErrorMessage ?? result.StandardError));
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            bool hasTemplates = document.RootElement.TryGetProperty("templates", out JsonElement templates)
                && templates.ValueKind == JsonValueKind.Array
                && templates.GetArrayLength() > 0;
            checks.Add(new DiagnosticCheck(
                "render-host-runtime",
                hasTemplates ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
                hasTemplates
                    ? $"render-host started successfully: {renderHostPath}"
                    : "render-host returned no templates."));
        }
        catch (JsonException)
        {
            checks.Add(new DiagnosticCheck(
                "render-host-runtime",
                DiagnosticStatus.Failed,
                "render-host returned invalid JSON Lines output."));
        }
    }

    private async Task AddVersionCheckAsync(
        List<DiagnosticCheck> checks,
        string name,
        string executable,
        CancellationToken cancellationToken)
    {
        ProcessExecutionResult result = await processRunner!.RunAsync(
            new ProcessStartRequest(
                executable,
                ["-version"],
                TimeSpan.FromSeconds(10),
                16_384),
            cancellationToken);

        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
        string firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim() ?? "No version output was returned.";

        checks.Add(result.IsSuccess
            ? new DiagnosticCheck(name, DiagnosticStatus.Passed, firstLine)
            : new DiagnosticCheck(
                name,
                DiagnosticStatus.Failed,
                result.ErrorMessage ?? $"Unable to execute {executable}."));
    }

    private T GetOptions<T>(string sectionName) where T : new() =>
        configuration.GetSection(sectionName).Get<T>() ?? new T();

    private static bool LimitsAreValid(LimitsOptions limits) =>
        limits.MaxInputFileBytes > 0
        && limits.MaxInputDurationSeconds > 0;

    private static void CheckSecret(
        List<DiagnosticCheck> checks,
        string name,
        string value,
        string configurationKey) =>
        AddCondition(
            checks,
            name,
            !string.IsNullOrWhiteSpace(value),
            $"{configurationKey} is configured.",
            $"{configurationKey} is not configured.");

    private void CheckExecutable(
        List<DiagnosticCheck> checks,
        string name,
        string configuredPath,
        string workspaceRoot)
    {
        string? resolvedPath = ResolveExecutable(configuredPath, workspaceRoot);
        checks.Add(resolvedPath is null
            ? new DiagnosticCheck(name, DiagnosticStatus.Failed, $"Executable was not found: {configuredPath}")
            : new DiagnosticCheck(name, DiagnosticStatus.Passed, $"Executable found: {resolvedPath}"));
    }

    private static void CheckFile(
        List<DiagnosticCheck> checks,
        string name,
        string configuredPath,
        string workspaceRoot)
    {
        string path = ResolvePath(configuredPath, workspaceRoot);
        checks.Add(File.Exists(path)
            ? new DiagnosticCheck(name, DiagnosticStatus.Passed, $"File found: {path}")
            : new DiagnosticCheck(name, DiagnosticStatus.Failed, $"File was not found: {path}"));
    }

    private static void CheckAiApi(List<DiagnosticCheck> checks, AiApiOptions options)
    {
        bool anyValuePresent = !string.IsNullOrWhiteSpace(options.Provider)
            || !string.IsNullOrWhiteSpace(options.ApiKey)
            || !string.IsNullOrWhiteSpace(options.Model);
        bool complete = !string.IsNullOrWhiteSpace(options.Provider)
            && !string.IsNullOrWhiteSpace(options.ApiKey)
            && !string.IsNullOrWhiteSpace(options.Model);

        checks.Add(!anyValuePresent
            ? new DiagnosticCheck("ai-api", DiagnosticStatus.Warning, "AI API is not configured.")
            : new DiagnosticCheck(
                "ai-api",
                complete ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
                complete ? "AI API is configured." : "AiApi settings are incomplete."));
    }

    private static void AddCondition(
        List<DiagnosticCheck> checks,
        string name,
        bool condition,
        string successMessage,
        string failureMessage) =>
        checks.Add(new DiagnosticCheck(
            name,
            condition ? DiagnosticStatus.Passed : DiagnosticStatus.Failed,
            condition ? successMessage : failureMessage));

    private static string? ResolveExecutable(string configuredPath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        string expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
        if (Path.IsPathRooted(expandedPath)
            || expandedPath.Contains(Path.DirectorySeparatorChar)
            || expandedPath.Contains(Path.AltDirectorySeparatorChar))
        {
            string path = ResolvePath(expandedPath, workspaceRoot);
            return File.Exists(path) ? path : null;
        }

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidateName in GetCandidateNames(expandedPath, extensions))
            {
                string candidate = Path.Combine(directory.Trim('"'), candidateName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateNames(string command, string[] extensions)
    {
        if (Path.HasExtension(command))
        {
            yield return command;
            yield break;
        }

        yield return command;
        foreach (string extension in extensions)
        {
            yield return command + extension;
        }
    }

    private static string ResolvePath(string configuredPath, string basePath) =>
        Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(basePath, configuredPath));
}
