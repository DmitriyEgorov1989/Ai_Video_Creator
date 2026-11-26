using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Pipeline;

/// <summary>
/// Применяет доменные переходы после успешного завершения этапа.
/// </summary>
public static class JobStageTransitions
{
    /// <summary>
    /// Возвращает следующее состояние Job.
    /// </summary>
    public static JobProgress Complete(
        JobProgress progress,
        StageCompletionAction action = StageCompletionAction.Advance)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (action == StageCompletionAction.Remount)
        {
            return progress.Stage == ProductionStage.Critique
                ? progress.RequestRemount()
                : throw new InvalidOperationException("Remount is allowed only after Critique.");
        }

        if (action != StageCompletionAction.Advance)
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        return progress.Stage switch
        {
            ProductionStage.Preparation => progress.CompletePreparation(),
            ProductionStage.Analysis => progress.CompleteAnalysis(),
            ProductionStage.Briefing => progress.CompleteBriefing(),
            ProductionStage.Planning => progress.CompletePlanning(),
            ProductionStage.Preview => progress.CompletePreview(),
            ProductionStage.Rendering => progress.CompleteRendering(),
            ProductionStage.Critique => progress.CompleteCritique(),
            ProductionStage.Verification => progress.CompleteVerification(),
            ProductionStage.Packaging => progress.CompletePackaging(),
            ProductionStage.Delivery => progress.ConfirmDelivery(),
            _ => throw new ArgumentOutOfRangeException(nameof(progress.Stage))
        };
    }
}
