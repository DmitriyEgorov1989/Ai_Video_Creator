using System.Globalization;
using System.Text.RegularExpressions;
using VideoFactory.VideoProduction.Application.Analysis;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.Analysis;

/// <summary>Преобразует диагностику FFmpeg silencedetect во временные интервалы.</summary>
public sealed class SilenceDetectParser
{
    private static readonly Regex SilenceStart = new(
        @"silence_start:\s*(?<seconds>[+-]?[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SilenceEnd = new(
        @"silence_end:\s*(?<seconds>[+-]?[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Строит speech windows и pauses для файла указанной длительности.</summary>
    public AudioActivityResult Parse(string standardError, long durationMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        if (durationMicroseconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationMicroseconds));

        List<MasterRange> pauses = [];
        long? silenceStart = null;
        foreach (Match match in SilenceStart.Matches(standardError))
        {
            if (!TryParseMicroseconds(match.Groups["seconds"].Value, out long start)) continue;
            silenceStart = Math.Clamp(start, 0, durationMicroseconds);
            Match? end = SilenceEnd.Matches(standardError)
                .Cast<Match>()
                .FirstOrDefault(candidate => candidate.Index > match.Index);
            if (end is null || !TryParseMicroseconds(end.Groups["seconds"].Value, out long endMicroseconds)) continue;
            pauses.Add(new MasterRange(
                silenceStart.Value,
                Math.Clamp(endMicroseconds, silenceStart.Value + 1, durationMicroseconds)));
            silenceStart = null;
        }

        if (silenceStart is not null && silenceStart.Value < durationMicroseconds)
        {
            pauses.Add(new MasterRange(silenceStart.Value, durationMicroseconds));
        }

        List<MasterRange> mergedPauses = Merge(pauses);
        List<MasterRange> speech = [];
        long cursor = 0;
        foreach (MasterRange pause in mergedPauses)
        {
            if (pause.StartMicroseconds > cursor)
            {
                speech.Add(new MasterRange(cursor, pause.StartMicroseconds));
            }
            cursor = Math.Max(cursor, pause.EndMicroseconds);
        }

        if (cursor < durationMicroseconds)
        {
            speech.Add(new MasterRange(cursor, durationMicroseconds));
        }

        return new AudioActivityResult(
            speech.Any(static range => range.DurationMicroseconds >= 250_000),
            speech,
            mergedPauses);
    }

    private static List<MasterRange> Merge(IEnumerable<MasterRange> ranges)
    {
        List<MasterRange> merged = [];
        foreach (MasterRange range in ranges.OrderBy(static range => range.StartMicroseconds))
        {
            if (merged.Count == 0 || range.StartMicroseconds > merged[^1].EndMicroseconds)
            {
                merged.Add(range);
                continue;
            }

            MasterRange previous = merged[^1];
            merged[^1] = new MasterRange(
                previous.StartMicroseconds,
                Math.Max(previous.EndMicroseconds, range.EndMicroseconds));
        }
        return merged;
    }

    private static bool TryParseMicroseconds(string value, out long microseconds)
    {
        microseconds = 0;
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal seconds) || seconds < 0)
        {
            return false;
        }

        decimal result = seconds * 1_000_000m;
        if (result > long.MaxValue) return false;
        microseconds = checked((long)Math.Round(result, MidpointRounding.AwayFromZero));
        return true;
    }
}
