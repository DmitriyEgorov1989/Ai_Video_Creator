namespace VideoFactory.VideoProduction.IntegrationTests.Media;

/// <summary>Цветовой канал, который должен преобладать в наблюдаемой области кадра.</summary>
public enum DominantColor
{
    /// <summary>Красный канал.</summary>
    Red,

    /// <summary>Зелёный канал.</summary>
    Green,

    /// <summary>Синий канал.</summary>
    Blue,
}

/// <summary>Проверяет объективные признаки rendered media.</summary>
public static class MontageQualityAssertions
{
    /// <summary>Отклоняет область кадра с яркостью ниже заданного порога.</summary>
    /// <param name="observation">Непустое имя observation.</param>
    /// <param name="sample">Измеренный RGB, не равен <see langword="null"/>.</param>
    /// <param name="minimumLuminance">Минимальная допустимая яркость.</param>
    public static void RequireNonBlack(string observation, RgbSample sample, double minimumLuminance)
    {
        ArgumentNullException.ThrowIfNull(sample);
        double luminance = (0.2126 * sample.Red) + (0.7152 * sample.Green) + (0.0722 * sample.Blue);
        if (luminance < minimumLuminance)
        {
            throw new InvalidDataException(
                $"{observation}: black frame detected; luminance {luminance:0.##} is below {minimumLuminance:0.##}.");
        }
    }

    /// <summary>Требует двукратного преобладания ожидаемого цветового канала.</summary>
    /// <param name="observation">Непустое имя observation.</param>
    /// <param name="sample">Измеренный RGB, не равен <see langword="null"/>.</param>
    /// <param name="expected">Ожидаемый преобладающий канал.</param>
    public static void RequireDominantColor(string observation, RgbSample sample, DominantColor expected)
    {
        ArgumentNullException.ThrowIfNull(sample);
        double selected = expected switch
        {
            DominantColor.Red => sample.Red,
            DominantColor.Green => sample.Green,
            DominantColor.Blue => sample.Blue,
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };
        double other = expected switch
        {
            DominantColor.Red => Math.Max(sample.Green, sample.Blue),
            DominantColor.Green => Math.Max(sample.Red, sample.Blue),
            DominantColor.Blue => Math.Max(sample.Red, sample.Green),
            _ => throw new ArgumentOutOfRangeException(nameof(expected)),
        };
        if (selected <= other * 2)
        {
            throw new InvalidDataException(
                $"{observation}: expected {expected} marker, observed RGB({sample.Red:0.##}, {sample.Green:0.##}, {sample.Blue:0.##}).");
        }
    }

    /// <summary>Требует совпадения RGB с независимым oracle в заданном допуске.</summary>
    public static void RequireRgbNear(string observation, RgbSample actual, RgbSample expected, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(expected);
        if (Math.Abs(actual.Red - expected.Red) > tolerance
            || Math.Abs(actual.Green - expected.Green) > tolerance
            || Math.Abs(actual.Blue - expected.Blue) > tolerance)
        {
            throw new InvalidDataException(
                $"{observation}: expected RGB({expected.Red:0.##}, {expected.Green:0.##}, {expected.Blue:0.##}) +/- {tolerance:0.##}, observed RGB({actual.Red:0.##}, {actual.Green:0.##}, {actual.Blue:0.##}).");
        }
    }

    /// <summary>Требует совпадения измеренной частоты с oracle в герцах.</summary>
    public static void RequireTone(string observation, ToneMeasurement actual, double expectedFrequencyHz, double toleranceHz)
    {
        ArgumentNullException.ThrowIfNull(actual);
        if (Math.Abs(actual.FrequencyHz - expectedFrequencyHz) > toleranceHz)
        {
            throw new InvalidDataException(
                $"{observation}: expected {expectedFrequencyHz:0.##} Hz +/- {toleranceHz:0.##}, observed {actual.FrequencyHz:0.##} Hz.");
        }
    }

    /// <summary>Требует заметного изменения области только внутри caption window.</summary>
    public static void RequireOverlayContrast(string observation, RgbSample outside, RgbSample inside, double minimumRedDelta)
    {
        ArgumentNullException.ThrowIfNull(outside);
        ArgumentNullException.ThrowIfNull(inside);
        if (outside.Red - inside.Red < minimumRedDelta)
        {
            throw new InvalidDataException(
                $"{observation}: caption overlay contrast is too small; outside red={outside.Red:0.##}, inside red={inside.Red:0.##}.");
        }
    }
}
