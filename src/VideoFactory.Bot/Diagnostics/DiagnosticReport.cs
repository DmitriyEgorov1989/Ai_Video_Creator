namespace VideoFactory.Bot.Diagnostics;

/// <summary>
/// Результат отдельной диагностической проверки; тип значения не допускает
/// <see langword="null"/>.
/// </summary>
public enum DiagnosticStatus
{
    /// <summary>
    /// Проверка успешно пройдена; значение перечисления не является <see langword="null"/>.
    /// </summary>
    Passed,

    /// <summary>
    /// Проверка выявила некритичное предупреждение; значение перечисления не является
    /// <see langword="null"/>.
    /// </summary>
    Warning,

    /// <summary>
    /// Проверка завершилась ошибкой; значение перечисления не является <see langword="null"/>.
    /// </summary>
    Failed
}

/// <summary>
/// Описывает результат одной диагностической проверки.
/// Экземпляр результата не должен быть <see langword="null"/>.
/// </summary>
/// <param name="Name">Имя проверки; не может быть <see langword="null"/>.</param>
/// <param name="Status">Статус проверки; тип значения не допускает <see langword="null"/>.</param>
/// <param name="Message">Диагностическое сообщение; не может быть <see langword="null"/>.</param>
public sealed record DiagnosticCheck(
    string Name,
    DiagnosticStatus Status,
    string Message);

/// <summary>
/// Представляет сводный результат набора диагностических проверок.
/// Экземпляр отчёта не должен быть <see langword="null"/>.
/// </summary>
/// <param name="Checks">
/// Проверки, включённые в отчёт; коллекция и её элементы не могут быть
/// <see langword="null"/>.
/// </param>
public sealed record DiagnosticReport(IReadOnlyList<DiagnosticCheck> Checks)
{
    /// <summary>
    /// Получает признак отсутствия проверок со статусом <see cref="DiagnosticStatus.Failed"/>;
    /// тип значения не допускает <see langword="null"/>.
    /// </summary>
    public bool IsSuccessful => Checks.All(check => check.Status != DiagnosticStatus.Failed);
}
