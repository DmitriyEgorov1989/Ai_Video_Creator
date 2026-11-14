using VideoFactory.VideoProduction.Domain.Analysis;

namespace VideoFactory.VideoProduction.Application.Analysis;

/// <summary>Результат выполнения анализа.</summary>
public abstract record AnalysisExecutionResult
{
    private AnalysisExecutionResult() { }

    /// <summary>Успешный результат.</summary>
    public sealed record Succeeded(AnalysisResult Analysis) : AnalysisExecutionResult;

    /// <summary>Ожидаемая ошибка анализа.</summary>
    public sealed record Failed(string ErrorCode, string ErrorMessage) : AnalysisExecutionResult;
}
