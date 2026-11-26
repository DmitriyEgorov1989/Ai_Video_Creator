using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Application.Critique;

/// <summary>Преобразует подтверждённые наблюдения готового рендера в ограниченные коррекции плана.</summary>
public sealed class ReelsEditingCritic
{
    /// <summary>Оценивает чёрные интервалы и не изменяет переданный утверждённый план.</summary>
    /// <param name="request">План, анализ и объективные наблюдения; не может быть <see langword="null"/>.</param>
    /// <returns>Дефекты, коррекции и решение о необходимости перемонтажа.</returns>
    public ObjectiveCritiqueResult Critique(ObjectiveCritiqueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentNullException.ThrowIfNull(request.Analysis);

        ImmutableArray<CriticFinding>.Builder findings = ImmutableArray.CreateBuilder<CriticFinding>();
        ImmutableArray<CorrectionProposal>.Builder corrections = ImmutableArray.CreateBuilder<CorrectionProposal>();

        EvaluateIntervals(
            request,
            request.BlackIntervals,
            CriticFindingKind.BlackFrames,
            "critic.black_frames",
            findings,
            corrections);
        EvaluateAudioPeak(request, findings, corrections);
        EvaluateIntervals(
            request,
            request.FrozenIntervals,
            CriticFindingKind.FrozenFrames,
            "critic.frozen_frames",
            findings,
            corrections);

        CriticDecision decision = findings.Count == 0
            ? CriticDecision.Accepted
            : corrections.Count == 0
                ? CriticDecision.Rejected
                : CriticDecision.RemountRequired;
        return new ObjectiveCritiqueResult(findings.ToImmutable(), corrections.ToImmutable(), decision);
    }

    private static void EvaluateAudioPeak(
        ObjectiveCritiqueRequest request,
        ImmutableArray<CriticFinding>.Builder findings,
        ImmutableArray<CorrectionProposal>.Builder corrections)
    {
        if (request.MeasuredTruePeakDbtp is not decimal measured
            || request.MaximumTruePeakDbtp is not decimal maximum
            || measured <= maximum)
        {
            return;
        }

        bool isRepairable = !request.Plan.Audio.SourceClips.IsEmpty
            || !request.Plan.Audio.MusicClips.IsEmpty;
        findings.Add(new CriticFinding(
            "critic.audio_peak",
            CriticFindingKind.AudioPeak,
            new OutputRange(0, request.Plan.Output.DurationFrames),
            isRepairable));
        if (isRepairable)
        {
            decimal factor = (decimal)Math.Pow(10d, (double)(maximum - measured) / 20d);
            corrections.Add(new ScaleAudioGainCorrection(Math.Clamp(factor, 0.0001m, 1m)));
        }
    }

    private static void EvaluateIntervals(
        ObjectiveCritiqueRequest request,
        ImmutableArray<OutputRange> intervals,
        CriticFindingKind kind,
        string code,
        ImmutableArray<CriticFinding>.Builder findings,
        ImmutableArray<CorrectionProposal>.Builder corrections)
    {
        foreach (OutputRange interval in intervals)
        {
            VideoClip? clip = request.Plan.Video.Clips.FirstOrDefault(
                candidate => Intersects(candidate.Output, interval));
            if (kind == CriticFindingKind.FrozenFrames
                && clip is not null
                && IsKnownStaticSegment(request, clip))
            {
                continue;
            }
            AllowedSegment? replacement = clip is null
                ? null
                : FindReplacement(request, clip);
            bool isRepairable = clip is not null && replacement is not null;

            findings.Add(new CriticFinding(
                code,
                kind,
                interval,
                isRepairable));

            if (clip is not null && replacement is not null)
            {
                corrections.Add(new ReplaceVideoClipCorrection(
                    interval,
                    clip.Output,
                    clip.SegmentId,
                    replacement.Id,
                    replacement.Mapping));
            }
        }
    }

    private static AllowedSegment? FindReplacement(ObjectiveCritiqueRequest request, VideoClip clip) =>
        request.Analysis.AllowedSegments.FirstOrDefault(segment =>
            segment.IsApproved
            && segment.Id != clip.SegmentId
            && TimelineMath.ToNearestFrameCount(
                segment.Mapping.Master.DurationMicroseconds,
                request.Plan.Output.FrameRate) == clip.Output.DurationFrames);

    private static bool IsKnownStaticSegment(ObjectiveCritiqueRequest request, VideoClip clip)
    {
        AllowedSegment? segment = request.Analysis.AllowedSegments.FirstOrDefault(
            candidate => candidate.Id == clip.SegmentId);
        return segment is not null
            && segment.Confidence >= 0.8m
            && segment.Features.TechnicalValidity
            && segment.Features.TechnicalIssues.IsEmpty
            && segment.Features.Motion <= 0.02m;
    }

    private static bool Intersects(OutputRange left, OutputRange right) =>
        left.StartFrame < right.EndFrame && right.StartFrame < left.EndFrame;
}
