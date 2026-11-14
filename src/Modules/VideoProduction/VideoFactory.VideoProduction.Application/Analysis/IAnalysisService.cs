using VideoFactory.VideoProduction.Domain.Analysis;

namespace VideoFactory.VideoProduction.Application.Analysis;

/// <summary>Публичный прикладной seam этапа анализа.</summary>
public interface IAnalysisService
{
    /// <summary>Создаёт проверенный результат анализа.</summary>
    Task<AnalysisExecutionResult> AnalyzeAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken);
}
