using System.Globalization;
using System.Text.RegularExpressions;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.Infrastructure.Analysis;

/// <summary>Извлекает explainable-признаки через встроенные FFmpeg filters.</summary>
public sealed class FfmpegAnalysisFeatureExtractor : IAnalysisFeatureExtractor
{
    private static readonly Regex Metric = new(
        @"(?:lavfi\.signalstats\.|lavfi\.astats\.Overall\.)(?<name>[A-Za-z_]+)=(?<value>-?[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly IProcessRunner processRunner;
    private readonly string executablePath;
    private readonly TimeSpan timeout;
    private readonly int maxOutputBytes;

    /// <summary>Создаёт FFmpeg feature extractor.</summary>
    public FfmpegAnalysisFeatureExtractor(
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
    public async Task<SegmentQualityFeatures> ExtractAsync(
        FeatureExtractionRequest request,
        CancellationToken cancellationToken)
    {
        string start = (request.Interval.StartMicroseconds / 1_000_000m)
            .ToString("0.######", CultureInfo.InvariantCulture);
        string duration = (request.Interval.DurationMicroseconds / 1_000_000m)
            .ToString("0.######", CultureInfo.InvariantCulture);
        ProcessExecutionResult result = await processRunner.RunAsync(
            new ProcessStartRequest(
                executablePath,
                [
                    "-v", "warning", "-ss", start, "-t", duration, "-i", request.FilePath,
                    "-vf", "signalstats,metadata=print", "-af", "astats=metadata=1,ametadata=print",
                    "-f", "null", "-"
                ],
                timeout,
                maxOutputBytes),
            cancellationToken);

        if (!result.IsSuccess)
        {
            return SegmentQualityFeatures.Fallback with
            {
                TechnicalValidity = false,
                TechnicalIssues = ["ffmpeg-feature-extraction-failed"]
            };
        }

        Dictionary<string, decimal> values = Metric.Matches(result.StandardError + result.StandardOutput)
            .Select(match => new
            {
                Name = match.Groups["name"].Value.ToUpperInvariant(),
                Value = decimal.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value)
                    ? value
                    : 0m
            })
            .GroupBy(item => item.Name)
            .ToDictionary(group => group.Key, group => group.Average(item => item.Value));

        decimal yavg = Get(values, "YAVG");
        decimal ydiff = Get(values, "YDIFF");
        decimal rms = Get(values, "RMS_LEVEL");
        decimal exposure = Math.Clamp(1m - Math.Abs(yavg - 128m) / 128m, 0m, 1m);
        decimal motion = Math.Clamp(ydiff / 64m, 0m, 1m);
        decimal sharpness = Math.Clamp(ydiff / 32m, 0m, 1m);
        decimal audioEnergy = Math.Clamp((rms + 60m) / 60m, 0m, 1m);

        return new SegmentQualityFeatures(
            motion,
            sharpness,
            exposure,
            audioEnergy,
            true,
            []);
    }

    private static decimal Get(IReadOnlyDictionary<string, decimal> values, string key) =>
        values.TryGetValue(key, out decimal value) ? value : 0m;
}
