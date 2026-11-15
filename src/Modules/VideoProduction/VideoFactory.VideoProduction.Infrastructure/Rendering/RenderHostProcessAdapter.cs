using System.Text.Json;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Application.Rendering;

namespace VideoFactory.VideoProduction.Infrastructure.Rendering;

/// <summary>
/// Запускает Node render-host без shell-интерполяции и разбирает JSON Lines.
/// </summary>
public sealed class RenderHostProcessAdapter : IRenderHostAdapter
{
    private readonly IProcessRunner processRunner;
    private readonly string nodePath;
    private readonly string renderHostPath;

    /// <summary>Создаёт адаптер render-host.</summary>
    public RenderHostProcessAdapter(
        IProcessRunner processRunner,
        string nodePath,
        string renderHostPath)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.nodePath = string.IsNullOrWhiteSpace(nodePath) ? throw new ArgumentException("Node path is required.", nameof(nodePath)) : nodePath;
        this.renderHostPath = string.IsNullOrWhiteSpace(renderHostPath) ? throw new ArgumentException("Render-host path is required.", nameof(renderHostPath)) : renderHostPath;
    }

    /// <inheritdoc />
    public async Task<RenderHostResult> RenderAsync(
        RenderHostRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequestFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedOutputPath);

        ProcessExecutionResult process = await processRunner.RunAsync(
            new ProcessStartRequest(
                nodePath,
                [renderHostPath, "render", "--job", request.RequestFilePath],
                request.Timeout,
                request.MaxOutputBytes),
            cancellationToken);

        List<RenderProgress> progress = [];
        string? completedPath = null;
        int? durationFrames = null;
        string? eventErrorCode = null;
        string? eventErrorMessage = null;

        foreach (string line in process.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                string? type = root.TryGetProperty("type", out JsonElement typeElement)
                    ? typeElement.GetString()
                    : null;

                if (type == "progress")
                {
                    progress.Add(new RenderProgress(
                        root.GetProperty("stage").GetString() ?? "unknown",
                        root.GetProperty("percent").GetInt32()));
                }
                else if (type == "completed")
                {
                    completedPath = root.GetProperty("outputPath").GetString();
                    durationFrames = root.GetProperty("durationFrames").GetInt32();
                }
                else if (type == "failed")
                {
                    eventErrorCode = root.GetProperty("code").GetString();
                    eventErrorMessage = root.GetProperty("message").GetString();
                }
            }
            catch (JsonException)
            {
                return RenderHostResult.Failed(
                    "render.protocol_invalid",
                    "render-host emitted an invalid JSON Lines event.",
                    progress,
                    process.StandardOutput,
                    process.StandardError);
            }
            catch (KeyNotFoundException)
            {
                return RenderHostResult.Failed(
                    "render.protocol_invalid",
                    "render-host emitted an incomplete JSON Lines event.",
                    progress,
                    process.StandardOutput,
                    process.StandardError);
            }
        }

        if (eventErrorCode is not null || !process.IsSuccess)
        {
            return RenderHostResult.Failed(
                eventErrorCode ?? process.ErrorCode ?? "render.process_failed",
                eventErrorMessage ?? process.ErrorMessage ?? "render-host failed.",
                progress,
                process.StandardOutput,
                process.StandardError);
        }

        if (completedPath is null || durationFrames is null)
        {
            return RenderHostResult.Failed(
                "render.completed_event_missing",
                "render-host exited successfully without a completed event.",
                progress,
                process.StandardOutput,
                process.StandardError);
        }

        string expectedPath = Path.GetFullPath(request.ExpectedOutputPath);
        string actualPath = Path.GetFullPath(completedPath);
        if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
        {
            return RenderHostResult.Failed(
                "render.output_path_mismatch",
                "render-host completed event contained an unexpected output path.",
                progress,
                process.StandardOutput,
                process.StandardError);
        }

        if (!File.Exists(expectedPath) || new FileInfo(expectedPath).Length <= 0)
        {
            return RenderHostResult.Failed(
                "render.output_missing",
                "render-host reported completion but did not create a non-empty output file.",
                progress,
                process.StandardOutput,
                process.StandardError);
        }

        return RenderHostResult.Succeeded(
            expectedPath,
            durationFrames.Value,
            progress,
            process.StandardOutput,
            process.StandardError);
    }
}
