using System.Collections.Immutable;
using System.Globalization;
using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.QualityAssurance;

/// <summary>Измеряет чёрные интервалы готового рендера через FFmpeg blackdetect.</summary>
public sealed class FfmpegRenderedMediaObserver : IRenderedMediaObserver
{
    private readonly IProcessRunner processRunner;
    private readonly string executablePath;
    private readonly TimeSpan timeout;
    private readonly int maxOutputBytes;
    private readonly int minimumBlackDurationMilliseconds;
    private readonly int minimumFreezeDurationMilliseconds;
    private readonly BlackDetectParser parser;
    private readonly FreezeDetectParser freezeParser;

    /// <summary>Создаёт безопасный FFmpeg-адаптер объективных измерений.</summary>
    public FfmpegRenderedMediaObserver(
        IProcessRunner processRunner,
        string executablePath,
        TimeSpan timeout,
        int maxOutputBytes,
        int minimumBlackDurationMilliseconds,
        BlackDetectParser? parser = null,
        int minimumFreezeDurationMilliseconds = 1_000,
        FreezeDetectParser? freezeParser = null)
    {
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("FFmpeg path cannot be empty.", nameof(executablePath));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        if (minimumBlackDurationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(minimumBlackDurationMilliseconds));
        if (minimumFreezeDurationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(minimumFreezeDurationMilliseconds));
        this.executablePath = executablePath;
        this.timeout = timeout;
        this.maxOutputBytes = maxOutputBytes;
        this.minimumBlackDurationMilliseconds = minimumBlackDurationMilliseconds;
        this.minimumFreezeDurationMilliseconds = minimumFreezeDurationMilliseconds;
        this.parser = parser ?? new BlackDetectParser();
        this.freezeParser = freezeParser ?? new FreezeDetectParser();
    }

    /// <inheritdoc />
    public async Task<RenderedMediaObservationResult> ObserveAsync(
        RenderedMediaObservationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        decimal minimumSeconds = minimumBlackDurationMilliseconds / 1000m;
        decimal minimumFreezeSeconds = minimumFreezeDurationMilliseconds / 1000m;
        string filter = string.Format(
            CultureInfo.InvariantCulture,
            "blackdetect=d={0:0.###}:pix_th=0.10,freezedetect=n=-60dB:d={1:0.###}",
            minimumSeconds,
            minimumFreezeSeconds);
        ProcessExecutionResult process = await processRunner.RunAsync(
            new ProcessStartRequest(
                executablePath,
                ["-hide_banner", "-nostdin", "-i", request.FilePath, "-vf", filter, "-an", "-f", "null", "-"],
                timeout,
                maxOutputBytes),
            cancellationToken);
        if (!process.IsSuccess)
        {
            return new RenderedMediaObservationResult.Failed(
                process.ErrorCode ?? "critic.black_detection_failed",
                process.ErrorMessage ?? "FFmpeg black frame detection failed.");
        }

        IReadOnlyList<OutputRange> intervals = parser.Parse(
            process.StandardError,
            request.FrameRate,
            request.DurationFrames);
        IReadOnlyList<OutputRange> frozenIntervals = freezeParser.Parse(
            process.StandardError,
            request.FrameRate,
            request.DurationFrames);
        return new RenderedMediaObservationResult.Succeeded(
            new RenderedMediaObservation(intervals.ToImmutableArray())
            {
                FrozenIntervals = frozenIntervals.ToImmutableArray()
            });
    }
}
