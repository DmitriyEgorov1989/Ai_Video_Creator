using System.Globalization;
using System.Text.RegularExpressions;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.Analysis;

/// <summary>Вычисляет explainable video features через FFmpeg filters.</summary>
public sealed class FfmpegVideoFeatureAnalyzer : IVideoFeatureAnalyzer
{
    private static readonly Regex Metric = new(
        @"(?:lavfi\.signalstats\.(?<name>YAVG|YDIF|SATAVG)|lavfi\.astats\.Overall\.(?<audio>RMS_level))[:=](?<value>-?[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Crop = new(
        @"crop=(?<width>[0-9]+):(?<height>[0-9]+):(?<x>[0-9]+):(?<y>[0-9]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly IProcessRunner processRunner;
    private readonly string executablePath;
    private readonly TimeSpan timeout;
    private readonly int maxOutputBytes;

    /// <summary>Создаёт FFmpeg feature analyzer.</summary>
    public FfmpegVideoFeatureAnalyzer(
        IProcessRunner processRunner,
        string executablePath,
        TimeSpan timeout,
        int maxOutputBytes)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        this.timeout = timeout;
        this.maxOutputBytes = maxOutputBytes;
    }

    /// <inheritdoc />
    public async Task<VideoFeatureAnalysisResult> AnalyzeAsync(
        VideoFeatureAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<SegmentAnalysisFeatures> features = [];
        foreach (AllowedSegment segment in request.Segments)
        {
            ProcessExecutionResult result = await processRunner.RunAsync(
                new ProcessStartRequest(
                    executablePath,
                    BuildArguments(request.FilePath, segment.Mapping.Master),
                    timeout,
                    maxOutputBytes),
                cancellationToken);
            if (!result.IsSuccess)
            {
                return VideoFeatureAnalysisResult.Failed(
                    result.ErrorCode ?? "analysis.video_features_failed",
                    result.ErrorMessage ?? "FFmpeg video feature analysis failed.");
            }

            features.Add(ReadFeatures(segment.Id, result.StandardError));
        }

        return VideoFeatureAnalysisResult.Succeeded(features);
    }

    private static IReadOnlyList<string> BuildArguments(string filePath, MasterRange interval)
    {
        decimal startSeconds = interval.StartMicroseconds / 1_000_000m;
        decimal durationSeconds = interval.DurationMicroseconds / 1_000_000m;
        return
        [
            "-v", "info",
            "-ss", startSeconds.ToString(CultureInfo.InvariantCulture),
            "-t", durationSeconds.ToString(CultureInfo.InvariantCulture),
            "-i", filePath,
            "-vf", "signalstats,metadata=print,cropdetect=limit=24:round=2:reset=1",
            "-af", "astats=metadata=1:reset=1,ametadata=print",
            "-f", "null", "-"
        ];
    }

    private static SegmentAnalysisFeatures ReadFeatures(SegmentId segmentId, string diagnostics)
    {
        Dictionary<string, decimal> metrics = Metric.Matches(diagnostics)
            .Select(match => new
            {
                Name = match.Groups["audio"].Success
                    ? "RMS_LEVEL"
                    : match.Groups["name"].Value.ToUpperInvariant(),
                Value = decimal.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture)
            })
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Average(item => item.Value), StringComparer.Ordinal);
        Match crop = Crop.Match(diagnostics);
        decimal composition = crop.Success
            ? Math.Clamp(1m - Math.Abs(decimal.Parse(crop.Groups["x"].Value, CultureInfo.InvariantCulture)) / 1_000m, 0m, 1m)
            : 0.5m;
        decimal sharpness = Normalize(metrics.GetValueOrDefault("YDIF"), 0m, 64m);
        decimal motion = Normalize(metrics.GetValueOrDefault("YDIF"), 0m, 32m);
        decimal exposure = 1m - Math.Abs(Normalize(metrics.GetValueOrDefault("YAVG"), 0m, 255m) - 0.5m) * 2m;
        decimal audioEnergy = Normalize(metrics.GetValueOrDefault("RMS_LEVEL"), -60m, 0m);
        return new SegmentAnalysisFeatures(
            segmentId,
            0m,
            sharpness,
            Math.Clamp(composition * (0.5m + exposure * 0.5m), 0m, 1m),
            motion,
            audioEnergy,
            metrics.Count == 0 ? 0.1m : 0.8m);
    }

    private static decimal Normalize(decimal value, decimal minimum, decimal maximum) =>
        Math.Clamp((value - minimum) / (maximum - minimum), 0m, 1m);
}
