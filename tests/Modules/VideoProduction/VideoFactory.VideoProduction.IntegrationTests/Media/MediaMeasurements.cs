using System.Globalization;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.IntegrationTests.Media;

/// <summary>Прямоугольная область видеокадра в пикселях.</summary>
/// <param name="X">Координата левого края в пикселях.</param>
/// <param name="Y">Координата верхнего края в пикселях.</param>
/// <param name="Width">Ширина области в пикселях.</param>
/// <param name="Height">Высота области в пикселях.</param>
public sealed record PixelRegion(int X, int Y, int Width, int Height);

/// <summary>Среднее значение каналов RGB выбранной области.</summary>
/// <param name="Red">Среднее значение красного канала от 0 до 255.</param>
/// <param name="Green">Среднее значение зелёного канала от 0 до 255.</param>
/// <param name="Blue">Среднее значение синего канала от 0 до 255.</param>
public sealed record RgbSample(double Red, double Green, double Blue);

/// <summary>Объективные измерения монофонического аудиоокна.</summary>
/// <param name="FrequencyHz">Частота по переходам через ноль в герцах.</param>
/// <param name="PeakDecibels">Пиковый уровень относительно full scale в dBFS.</param>
/// <param name="RmsDecibels">Среднеквадратичный уровень относительно full scale в dBFS.</param>
public sealed record ToneMeasurement(double FrequencyHz, double PeakDecibels, double RmsDecibels);

/// <summary>Извлекает объективные frame и audio observations через безопасный process seam.</summary>
public static class MediaMeasurements
{
    private const int SampleRate = 48_000;

    /// <summary>Возвращает средний RGB выбранной области кадра.</summary>
    /// <param name="runner">Process runner, не равен <see langword="null"/>.</param>
    /// <param name="inputPath">Путь к существующему медиафайлу.</param>
    /// <param name="timestamp">Неотрицательная позиция кадра.</param>
    /// <param name="region">Непустая область кадра, не равна <see langword="null"/>.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Средние значения RGB.</returns>
    public static async Task<RgbSample> SampleRegionAsync(
        IProcessRunner runner,
        string inputPath,
        TimeSpan timestamp,
        PixelRegion region,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(region);
        if (string.IsNullOrWhiteSpace(inputPath)) throw new ArgumentException("Input path is required.", nameof(inputPath));
        if (timestamp < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }
        string outputPath = Path.Combine(Path.GetTempPath(), $"frame-{Guid.NewGuid():N}.rgb");
        string filter = FormattableString.Invariant(
            $"crop={region.Width}:{region.Height}:{region.X}:{region.Y},scale=1:1,format=rgb24");
        try
        {
            ProcessExecutionResult result = await runner.RunAsync(
                new ProcessStartRequest(
                    "ffmpeg",
                    ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", FormatSeconds(timestamp), "-i", inputPath, "-frames:v", "1", "-vf", filter, "-f", "rawvideo", outputPath],
                    TimeSpan.FromSeconds(30),
                    32_768),
                cancellationToken);
            EnsureSuccess(result);
            byte[] bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            if (bytes.Length != 3) throw new InvalidDataException($"Expected one RGB sample, received {bytes.Length} bytes.");
            return new(bytes[0], bytes[1], bytes[2]);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    /// <summary>Измеряет частоту и peak dBFS монофонического audio window.</summary>
    /// <param name="runner">Process runner, не равен <see langword="null"/>.</param>
    /// <param name="inputPath">Путь к существующему медиафайлу.</param>
    /// <param name="start">Неотрицательное начало окна.</param>
    /// <param name="duration">Положительная длительность окна.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Измеренные частота и peak dBFS.</returns>
    public static async Task<ToneMeasurement> MeasureToneAsync(
        IProcessRunner runner,
        string inputPath,
        TimeSpan start,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (string.IsNullOrWhiteSpace(inputPath)) throw new ArgumentException("Input path is required.", nameof(inputPath));
        if (start < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(start));
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        string outputPath = Path.Combine(Path.GetTempPath(), $"audio-{Guid.NewGuid():N}.pcm");
        try
        {
            ProcessExecutionResult result = await runner.RunAsync(
                new ProcessStartRequest(
                    "ffmpeg",
                    ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", FormatSeconds(start), "-t", FormatSeconds(duration), "-i", inputPath, "-vn", "-ac", "1", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", outputPath],
                    TimeSpan.FromSeconds(30),
                    32_768),
                cancellationToken);
            EnsureSuccess(result);
            byte[] pcm = await File.ReadAllBytesAsync(outputPath, cancellationToken);
            if (pcm.Length < 4) throw new InvalidDataException("The measured audio window is empty.");

            int crossings = 0;
            int peak = 0;
            double squaredSum = 0;
            int sampleCount = 0;
            short previous = BitConverter.ToInt16(pcm, 0);
            for (int offset = 0; offset < pcm.Length; offset += 2)
            {
                short current = BitConverter.ToInt16(pcm, offset);
                if (offset > 0 && ((previous < 0 && current >= 0) || (previous >= 0 && current < 0))) crossings++;
                peak = Math.Max(peak, Math.Abs((int)current));
                squaredSum += (double)current * current;
                sampleCount++;
                previous = current;
            }

            double actualDuration = (pcm.Length / 2d) / SampleRate;
            double frequency = crossings / (2d * actualDuration);
            double peakDecibels = peak == 0 ? double.NegativeInfinity : 20 * Math.Log10(peak / 32768d);
            double rms = Math.Sqrt(squaredSum / sampleCount);
            double rmsDecibels = rms == 0 ? double.NegativeInfinity : 20 * Math.Log10(rms / 32768d);
            return new(frequency, peakDecibels, rmsDecibels);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    private static void EnsureSuccess(ProcessExecutionResult result)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.ErrorMessage ?? result.StandardError);
        }
    }
}
