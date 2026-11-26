using System.Globalization;
using System.Text.RegularExpressions;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.QualityAssurance;

/// <summary>Преобразует диагностику FFmpeg blackdetect в интервалы выходных кадров.</summary>
public sealed class BlackDetectParser
{
    private static readonly Regex Interval = new(
        @"black_start:(?<start>[0-9]+(?:\.[0-9]+)?)\s+black_end:(?<end>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Возвращает корректные полуоткрытые интервалы, ограниченные длительностью рендера.</summary>
    /// <param name="standardError">Диагностика FFmpeg; не может быть <see langword="null"/>.</param>
    /// <param name="frameRate">Частота кадров готового рендера.</param>
    /// <param name="durationFrames">Полная длительность рендера в кадрах.</param>
    /// <returns>Найденные интервалы в порядке вывода FFmpeg.</returns>
    public IReadOnlyList<OutputRange> Parse(
        string standardError,
        FrameRate frameRate,
        int durationFrames)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        if (!frameRate.IsValid) throw new ArgumentException("Frame rate must be valid.", nameof(frameRate));
        if (durationFrames <= 0) throw new ArgumentOutOfRangeException(nameof(durationFrames));

        List<OutputRange> intervals = [];
        foreach (Match match in Interval.Matches(standardError))
        {
            if (!TryParseSeconds(match.Groups["start"].Value, out decimal startSeconds)
                || !TryParseSeconds(match.Groups["end"].Value, out decimal endSeconds))
            {
                continue;
            }

            int start = Math.Clamp(ToFrame(startSeconds, frameRate, roundUp: false), 0, durationFrames);
            int end = Math.Clamp(ToFrame(endSeconds, frameRate, roundUp: true), 0, durationFrames);
            if (end > start)
            {
                intervals.Add(new OutputRange(start, end));
            }
        }

        return intervals;
    }

    private static bool TryParseSeconds(string value, out decimal seconds) =>
        decimal.TryParse(
            value,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out seconds)
        && seconds >= 0;

    private static int ToFrame(decimal seconds, FrameRate frameRate, bool roundUp)
    {
        decimal frame = seconds * frameRate.Numerator / frameRate.Denominator;
        return checked((int)(roundUp ? decimal.Ceiling(frame) : decimal.Floor(frame)));
    }
}
