using System.Text.Json.Serialization;

namespace VideoFactory.VideoProduction.Domain.Timeline;

/// <summary>
/// Представляет частоту кадров в виде сокращённой положительной рациональной дроби.
/// </summary>
public readonly record struct FrameRate
{
    /// <summary>
    /// Получает числитель сокращённого значения частоты кадров.
    /// </summary>
    public int Numerator { get; }

    /// <summary>
    /// Получает знаменатель сокращённого значения частоты кадров.
    /// </summary>
    public int Denominator { get; }

    /// <summary>
    /// Создаёт частоту кадров и сокращает заданную дробь.
    /// </summary>
    /// <param name="numerator">Положительный числитель частоты кадров.</param>
    /// <param name="denominator">Положительный знаменатель частоты кадров.</param>
    [JsonConstructor]
    public FrameRate(int numerator, int denominator)
    {
        if (numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator));
        }

        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator));
        }

        int divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    /// <summary>
    /// Получает признак того, что числитель и знаменатель положительны.
    /// </summary>
    public bool IsValid => Numerator > 0 && Denominator > 0;

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }
}
