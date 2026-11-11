namespace VideoFactory.VideoProduction.Domain.Validation;

/// <summary>
/// Описывает обнаруженное нарушение правила валидации.
/// </summary>
/// <param name="Code">Стабильный код нарушения; не может быть <see langword="null"/>.</param>
/// <param name="Path">Путь к значению, в котором обнаружено нарушение; не может быть <see langword="null"/>.</param>
/// <param name="Message">Человекочитаемое описание нарушения; не может быть <see langword="null"/>.</param>
public sealed record ValidationIssue(string Code, string Path, string Message);
