namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>
/// Получает проверенные технические metadata локального медиафайла.
/// </summary>
public interface IMediaProbe
{
    /// <summary>
    /// Выполняет probe через внешний адаптер.
    /// Ожидаемые ошибки возвращаются результатом, а не исключением.
    /// </summary>
    Task<MediaProbeResult> ProbeAsync(
        ProbeRequest request,
        CancellationToken cancellationToken);
}
