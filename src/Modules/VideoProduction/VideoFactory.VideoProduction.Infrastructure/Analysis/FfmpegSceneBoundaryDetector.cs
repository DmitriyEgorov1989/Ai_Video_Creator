using System.Globalization;
using System.Text.RegularExpressions;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.Analysis;

/// <summary>Определяет базовые scene boundaries через FFmpeg showinfo.</summary>
public sealed class FfmpegSceneBoundaryDetector : ISceneBoundaryDetector
{
    private static readonly Regex Timestamp = new(
        @"pts_time:(?<seconds>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProcessRunner processRunner;
    private readonly string executablePath;
    private readonly TimeSpan timeout;
    private readonly int maxOutputBytes;

    /// <summary>Создаёт detector границ сцен.</summary>
    public FfmpegSceneBoundaryDetector(
        IProcessRunner processRunner,
        string executablePath,
        TimeSpan timeout,
        int maxOutputBytes)
    {
        this.processRunner = processRunner;
        this.executablePath = executablePath;
        this.timeout = timeout;
        this.maxOutputBytes = maxOutputBytes;
    }

    /// <inheritdoc />
    public async Task<SceneDetectionResult> DetectAsync(
        SceneDetectionRequest request,
        CancellationToken cancellationToken)
    {
        ProcessExecutionResult result = await processRunner.RunAsync(
            new ProcessStartRequest(
                executablePath,
                ["-v", "error", "-i", request.FilePath, "-vf", "select='gt(scene,0.35)',showinfo", "-f", "null", "-"],
                timeout,
                maxOutputBytes),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return SceneDetectionResult.Failed(
                result.ErrorCode ?? "analysis.scene_detection_failed",
                result.ErrorMessage ?? "FFmpeg scene detection failed.");
        }

        List<long> cuts = Timestamp.Matches(result.StandardError)
            .Select(match => decimal.TryParse(match.Groups["seconds"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal seconds)
                ? (long?)Math.Round(seconds * 1_000_000m, MidpointRounding.AwayFromZero)
                : null)
            .Where(static value => value.HasValue)
            .Select(static value => value!.Value)
            .Where(value => value > 0 && value < request.DurationMicroseconds)
            .Distinct()
            .OrderBy(static value => value)
            .ToList();

        List<MasterRange> scenes = [];
        long start = 0;
        foreach (long cut in cuts)
        {
            scenes.Add(new MasterRange(start, cut));
            start = cut;
        }
        scenes.Add(new MasterRange(start, request.DurationMicroseconds));
        return SceneDetectionResult.Succeeded(scenes);
    }
}
