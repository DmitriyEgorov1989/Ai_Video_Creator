using VideoFactory.VideoProduction.Application.Jobs.CreateJob;

namespace VideoFactory.VideoProduction.Application.Pipeline;

/// <summary>
/// Выполняет один текущий этап Job.
/// </summary>
public interface IJobStageExecutor
{
    /// <summary>
    /// Выполняет этап и возвращает классифицированный результат без потери диагностики.
    /// </summary>
    Task<StageExecutionResult> ExecuteAsync(
        JobCreationRecord job,
        CancellationToken cancellationToken);
}
