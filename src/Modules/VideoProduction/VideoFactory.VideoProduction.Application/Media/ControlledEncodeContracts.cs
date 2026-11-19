namespace VideoFactory.VideoProduction.Application.Media;

/// <summary>Параметры одного контролируемого перекодирования MP4.</summary>
/// <param name="SourcePath">Путь к неизменяемому исходному MP4.</param>
/// <param name="OutputPath">Путь временного результата.</param>
/// <param name="VideoBitrateBitsPerSecond">Целевой video bitrate в битах в секунду.</param>
/// <param name="AudioBitrateBitsPerSecond">Целевой audio bitrate в битах в секунду.</param>
public sealed record ControlledEncodeRequest(
    string SourcePath,
    string OutputPath,
    long VideoBitrateBitsPerSecond,
    long AudioBitrateBitsPerSecond);

/// <summary>Результат контролируемого перекодирования.</summary>
public sealed record ControlledEncodeResult(bool IsSuccess, string? ErrorCode, string? ErrorMessage);

/// <summary>Выполняет не более одного явно запрошенного controlled encode.</summary>
public interface IControlledVideoEncoder
{
    /// <summary>Создаёт временный MP4 и не публикует его в финальный каталог.</summary>
    Task<ControlledEncodeResult> EncodeAsync(ControlledEncodeRequest request, CancellationToken cancellationToken);
}
