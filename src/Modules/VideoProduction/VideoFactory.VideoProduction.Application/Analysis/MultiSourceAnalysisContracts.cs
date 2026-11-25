using VideoFactory.VideoProduction.Domain.Analysis;

namespace VideoFactory.VideoProduction.Application.Analysis;

/// <summary>Преобразует читаемые legacy Analysis V1/V2 в единый source-scoped контракт V3.</summary>
public static class AnalysisContractAdapter
{
    /// <summary>Адаптирует legacy результат в агрегат ровно с одним источником.</summary>
    /// <param name="analysis">Legacy Analysis V1/V2; не может быть <see langword="null"/>.</param>
    /// <returns>Analysis V3, сохраняющий ревизию, таймлайны, признаки и сегменты legacy результата.</returns>
    public static MultiSourceAnalysisResult ToMultiSource(AnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return new MultiSourceAnalysisResult(
            3,
            analysis.RevisionId,
            [new AnalysisSource(
                analysis.OriginalAssetId,
                analysis.MasterAssetId,
                analysis.Speech,
                analysis.Pauses,
                analysis.Scenes,
                analysis.Features)],
            analysis.AllowedSegments);
    }
}
