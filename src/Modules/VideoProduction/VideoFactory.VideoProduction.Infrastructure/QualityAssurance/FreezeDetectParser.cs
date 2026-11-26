using System.Globalization;
using System.Text.RegularExpressions;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.QualityAssurance;

/// <summary>Преобразует диагностику FFmpeg freezedetect в интервалы выходных кадров.</summary>
public sealed class FreezeDetectParser
{
    private static readonly Regex Start = new(
        @"freeze_start:\s*(?<seconds>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex End = new(
        @"freeze_end:\s*(?<seconds>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Возвращает завершённые полуоткрытые freeze-интервалы в границах рендера.</summary>
    public IReadOnlyList<OutputRange> Parse(string standardError, FrameRate frameRate, int durationFrames)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        if (!frameRate.IsValid) throw new ArgumentException("Frame rate must be valid.", nameof(frameRate));
        if (durationFrames <= 0) throw new ArgumentOutOfRangeException(nameof(durationFrames));

        List<OutputRange> intervals = [];
        foreach (Match startMatch in Start.Matches(standardError))
        {
            Match? endMatch = End.Matches(standardError)
                .Cast<Match>()
                .FirstOrDefault(candidate => candidate.Index > startMatch.Index);
            if (endMatch is null
                || !TryParse(startMatch.Groups["seconds"].Value, out decimal startSeconds)
                || !TryParse(endMatch.Groups["seconds"].Value, out decimal endSeconds))
            {
                continue;
            }

            int start = Math.Clamp(ToFrame(startSeconds, frameRate, false), 0, durationFrames);
            int end = Math.Clamp(ToFrame(endSeconds, frameRate, true), 0, durationFrames);
            if (end > start) intervals.Add(new OutputRange(start, end));
        }

        return intervals;
    }

    private static bool TryParse(string value, out decimal seconds) =>
        decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds)
        && seconds >= 0;

    private static int ToFrame(decimal seconds, FrameRate frameRate, bool roundUp)
    {
        decimal frame = seconds * frameRate.Numerator / frameRate.Denominator;
        return checked((int)(roundUp ? decimal.Ceiling(frame) : decimal.Floor(frame)));
    }
}
