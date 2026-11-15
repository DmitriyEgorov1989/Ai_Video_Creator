namespace VideoFactory.VideoProduction.Application.Rendering;

/// <summary>
/// Запускает внешний render-host через безопасную границу процесса.
/// </summary>
public interface IRenderHostAdapter
{
    /// <summary>
    /// Запускает один рендер из JSON-файла запроса.
    /// </summary>
    /// <param name="request">Параметры запуска.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Структурированный результат запуска.</returns>
    Task<RenderHostResult> RenderAsync(
        RenderHostRequest request,
        CancellationToken cancellationToken);
}
