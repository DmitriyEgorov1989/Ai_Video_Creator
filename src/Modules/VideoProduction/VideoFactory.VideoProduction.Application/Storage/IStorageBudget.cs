namespace VideoFactory.VideoProduction.Application.Storage;

/// <summary>Проверяет квоту каталога и остаток места до выделения дополнительных байтов.</summary>
public interface IStorageBudget
{
    /// <summary>Возвращает отказ либо <see langword="null"/>, если операция помещается в бюджет.</summary>
    /// <param name="directory">Каталог Job или отдельной загрузки; не <see langword="null"/>.</param>
    /// <param name="additionalBytes">Верхняя оценка дополнительных файлов в байтах.</param>
    /// <param name="cancellationToken">Токен отмены обхода файлов.</param>
    Task<StorageBudgetFailure?> CheckAsync(string directory, long additionalBytes, CancellationToken cancellationToken);
}

/// <summary>Ожидаемый отказ выделения дискового бюджета.</summary>
/// <param name="Code">Безопасный технический код; не <see langword="null"/>.</param>
/// <param name="Message">Сообщение без содержимого файлов; не <see langword="null"/>.</param>
public sealed record StorageBudgetFailure(string Code, string Message);

/// <summary>Граница получения свободного места на фактическом томе файловой системы.</summary>
public interface IAvailableDiskSpace
{
    /// <summary>Возвращает доступный текущему пользователю объём в байтах для указанного пути.</summary>
    /// <param name="path">Путь на целевом томе; не <see langword="null"/>. Конечный каталог может ещё отсутствовать.</param>
    long GetAvailableBytes(string path);
}
