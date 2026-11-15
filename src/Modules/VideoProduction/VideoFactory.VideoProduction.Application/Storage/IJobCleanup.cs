namespace VideoFactory.VideoProduction.Application.Storage;

/// <summary>Удаляет только просроченные каталоги успешно завершённых Job, сохраняя историю в БД.</summary>
public interface IJobCleanup
{
    /// <summary>В режиме <paramref name="apply"/> выполняет удаление, иначе только считает кандидатов.</summary>
    /// <param name="apply">Разрешает удаление; значение false оставляет файлы и строки Job без изменений.</param>
    /// <param name="cancellationToken">Токен отмены обхода и удаления.</param>
    /// <returns>Сводка операции, никогда не <see langword="null"/>.</returns>
    Task<CleanupReport> RunAsync(bool apply, CancellationToken cancellationToken);
}

/// <summary>Безопасная сводка очистки без путей и пользовательского содержимого.</summary>
/// <param name="Candidates">Количество просроченных Job.</param>
/// <param name="Deleted">Количество полностью очищенных Job.</param>
/// <param name="Errors">Количество отказов; частичное удаление допускает повтор команды.</param>
/// <param name="FreedBytes">Размер удалённых файлов в байтах.</param>
public sealed record CleanupReport(int Candidates, int Deleted, int Errors, long FreedBytes);
