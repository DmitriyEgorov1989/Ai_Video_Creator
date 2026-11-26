using VideoFactory.VideoProduction.Domain.Analysis;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoFactory.VideoProduction.Application.Critique;

/// <summary>Представляет ожидаемый результат применения типизированной коррекции.</summary>
public abstract record EditPlanCorrectionResult
{
    private EditPlanCorrectionResult() { }

    /// <summary>Коррекция применена к новой неизменяемой ревизии плана.</summary>
    /// <param name="Plan">Новая ревизия; не может быть <see langword="null"/>.</param>
    public sealed record Applied(EditPlan Plan) : EditPlanCorrectionResult;

    /// <summary>Коррекция отклонена без изменения исходного плана.</summary>
    /// <param name="ErrorCode">Стабильный машинный код; не может быть <see langword="null"/>.</param>
    /// <param name="ErrorMessage">Безопасное описание причины; не может быть <see langword="null"/>.</param>
    public sealed record Rejected(string ErrorCode, string ErrorMessage) : EditPlanCorrectionResult;
}

/// <summary>Применяет закрытые операции коррекции к копии утверждённого монтажного плана.</summary>
public sealed class EditPlanCorrectionService
{
    /// <summary>Создаёт новую ревизию с синхронной заменой video и source audio.</summary>
    /// <param name="plan">Исходный утверждённый план; не может быть <see langword="null"/>.</param>
    /// <param name="analysis">Анализ, разрешающий сегменты замены; не может быть <see langword="null"/>.</param>
    /// <param name="correction">Типизированная коррекция; не может быть <see langword="null"/>.</param>
    /// <returns>Новая ревизия либо ожидаемый отказ.</returns>
    public EditPlanCorrectionResult Apply(
        EditPlan plan,
        MultiSourceAnalysisResult analysis,
        CorrectionProposal correction)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(correction);
        RevisionId correctionRevisionId = CreateRevisionId(plan.RevisionId, correction);

        return correction switch
        {
            ReplaceVideoClipCorrection replacement => ApplyReplacement(plan, analysis, replacement, correctionRevisionId),
            ScaleAudioGainCorrection scale => ApplyAudioScale(plan, scale, correctionRevisionId),
            _ => new EditPlanCorrectionResult.Rejected(
                "correction.unsupported",
                "The correction operation is not supported.")
        };
    }

    private static EditPlanCorrectionResult ApplyReplacement(
        EditPlan plan,
        MultiSourceAnalysisResult analysis,
        ReplaceVideoClipCorrection correction,
        RevisionId correctionRevisionId)
    {

        AllowedSegment? replacement = analysis.AllowedSegments.FirstOrDefault(segment =>
            segment.Id == correction.ReplacementSegmentId
            && segment.IsApproved
            && segment.Mapping == correction.ReplacementMapping);
        if (replacement is null)
        {
            return new EditPlanCorrectionResult.Rejected(
                "correction.replacement_not_allowed",
                "The replacement segment is not approved by the referenced analysis.");
        }

        int videoIndex = FindVideoIndex(plan, correction);
        if (videoIndex < 0)
        {
            return new EditPlanCorrectionResult.Rejected(
                "correction.stale_video_clip",
                "The video clip no longer matches the correction precondition.");
        }

        int audioIndex = FindAudioIndex(plan, correction);
        if (audioIndex < 0)
        {
            return new EditPlanCorrectionResult.Rejected(
                "correction.source_audio_missing",
                "The matching source audio clip was not found.");
        }

        VideoClip video = plan.Video.Clips[videoIndex] with
        {
            SegmentId = replacement.Id,
            Mapping = replacement.Mapping
        };
        SourceAudioClip audio = plan.Audio.SourceClips[audioIndex] with
        {
            SegmentId = replacement.Id
        };
        EditPlan corrected = plan with
        {
            RevisionId = correctionRevisionId,
            Video = plan.Video with { Clips = plan.Video.Clips.SetItem(videoIndex, video) },
            Audio = plan.Audio with { SourceClips = plan.Audio.SourceClips.SetItem(audioIndex, audio) }
        };
        return new EditPlanCorrectionResult.Applied(corrected);
    }

    private static EditPlanCorrectionResult ApplyAudioScale(
        EditPlan plan,
        ScaleAudioGainCorrection correction,
        RevisionId correctionRevisionId)
    {
        if (correction.Factor is <= 0m or > 1m)
        {
            return new EditPlanCorrectionResult.Rejected(
                "correction.audio_factor_invalid",
                "The audio gain factor must be greater than zero and no greater than one.");
        }

        ImmutableArray<SourceAudioClip> sourceClips =
        [
            .. plan.Audio.SourceClips.Select(clip => clip with
            {
                Gain = clip.Gain * correction.Factor
            })
        ];
        ImmutableArray<MusicClip> musicClips =
        [
            .. plan.Audio.MusicClips.Select(clip => clip with
            {
                Gain = clip.Gain * correction.Factor
            })
        ];
        return new EditPlanCorrectionResult.Applied(plan with
        {
            RevisionId = correctionRevisionId,
            Audio = new AudioTrack(sourceClips, musicClips)
        });
    }

    private static RevisionId CreateRevisionId(RevisionId sourceRevisionId, CorrectionProposal correction)
    {
        string correctionJson = JsonSerializer.Serialize(correction, correction.GetType());
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceRevisionId}|{correctionJson}"));
        return new RevisionId(new Guid(hash[..16]));
    }

    private static int FindVideoIndex(EditPlan plan, ReplaceVideoClipCorrection correction)
    {
        for (int index = 0; index < plan.Video.Clips.Length; index++)
        {
            VideoClip candidate = plan.Video.Clips[index];
            if (candidate.SegmentId == correction.ExpectedSegmentId
                && candidate.Output == correction.ClipOutput)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindAudioIndex(EditPlan plan, ReplaceVideoClipCorrection correction)
    {
        for (int index = 0; index < plan.Audio.SourceClips.Length; index++)
        {
            SourceAudioClip candidate = plan.Audio.SourceClips[index];
            if (candidate.SegmentId == correction.ExpectedSegmentId
                && candidate.Output == correction.ClipOutput)
            {
                return index;
            }
        }

        return -1;
    }
}
