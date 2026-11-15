using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using Microsoft.Data.Sqlite;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Применяет retention к завершённым Job через SQLite и локальную файловую систему.</summary>
/// <param name="connectionString">Подключение к уже мигрированной SQLite; не <see langword="null"/>.</param>
/// <param name="workspaceRoot">Разрешённый корень хранения; не <see langword="null"/>.</param>
/// <param name="retentionDays">Срок хранения завершённых Job в сутках UTC, больше нуля.</param>
/// <param name="clock">Источник текущего времени; не <see langword="null"/>.</param>
public sealed class SqliteJobCleanup(string connectionString, string workspaceRoot, int retentionDays, TimeProvider clock) : IJobCleanup
{
    /// <inheritdoc />
    public async Task<CleanupReport> RunAsync(bool apply, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retentionDays);
        // Межпроцессная блокировка освобождается ОС даже при аварийном завершении.
        using FileStream? cleanupLock = apply
            ? new FileStream(SafeFileTree.Resolve(workspaceRoot, ".cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            : null;
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        string cutoff = clock.GetUtcNow().AddDays(-retentionDays).ToString("O");
        List<string> candidates = [];
        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT job_id FROM jobs WHERE {Eligible} ORDER BY completed_at LIMIT 100;";
            AddPolicy(select, cutoff);
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) candidates.Add(reader.GetString(0));
        }
        int deleted = 0, errors = 0;
        long freed = 0;
        foreach (string id in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(id, "N", out Guid guid) || guid == Guid.Empty) { errors++; continue; }
            string[] paths = [$"data/jobs/{new JobId(guid)}", $"jobs/{new JobId(guid)}"];
            try
            {
                // Не удаляем данные, на которые ссылается другое задание, включая общий Original.
                await using SqliteCommand references = connection.CreateCommand();
                references.CommandText = """
                    SELECT EXISTS (SELECT 1 FROM artifacts a
                    WHERE (a.relative_path LIKE $path1 OR a.relative_path LIKE $path2)
                      AND (a.job_id IS NOT NULL AND a.job_id <> $id
                        OR EXISTS (SELECT 1 FROM jobs j WHERE j.job_id <> $id AND j.source_artifact_id = a.artifact_id)));
                    """;
                references.Parameters.AddWithValue("$path1", paths[0] + "/%");
                references.Parameters.AddWithValue("$path2", paths[1] + "/%");
                references.Parameters.AddWithValue("$id", id);
                if (Convert.ToInt64(await references.ExecuteScalarAsync(cancellationToken)) != 0) { errors++; continue; }
                string[] directories = paths.Select(path => SafeFileTree.Resolve(workspaceRoot, path)).ToArray();
                long bytes = directories.Sum(path => SafeFileTree.SizeBytes(path, cancellationToken));
                if (!apply) continue;
                await using SqliteCommand claim = connection.CreateCommand();
                claim.CommandText = $"UPDATE jobs SET cleanup_started_at = COALESCE(cleanup_started_at, $now) WHERE job_id = $id AND {Eligible};";
                AddPolicy(claim, cutoff);
                claim.Parameters.AddWithValue("$id", id);
                claim.Parameters.AddWithValue("$now", clock.GetUtcNow().ToString("O"));
                if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1) continue;
                foreach (string directory in directories) DeleteTree(directory, cancellationToken);
                await using SqliteCommand complete = connection.CreateCommand();
                complete.CommandText = "UPDATE jobs SET files_deleted_at = $now WHERE job_id = $id AND cleanup_started_at IS NOT NULL;";
                complete.Parameters.AddWithValue("$id", id);
                complete.Parameters.AddWithValue("$now", clock.GetUtcNow().ToString("O"));
                await complete.ExecuteNonQueryAsync(cancellationToken);
                freed = checked(freed + bytes);
                deleted++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
            {
                errors++;
            }
        }
        return new CleanupReport(candidates.Count, deleted, errors, freed);
    }

    private const string Eligible = """
        status = $completed AND completed_at IS NOT NULL
        AND julianday(completed_at) < julianday($cutoff) AND files_deleted_at IS NULL
        AND lease_owner IS NULL
        AND NOT EXISTS (SELECT 1 FROM stage_attempts s WHERE s.job_id = jobs.job_id AND s.status = 'Running')
        AND NOT EXISTS (SELECT 1 FROM artifacts a WHERE
            (replace(a.relative_path, '\', '/') LIKE 'data/jobs/' || jobs.job_id || '/%'
              OR replace(a.relative_path, '\', '/') LIKE 'jobs/' || jobs.job_id || '/%')
            AND (a.job_id IS NOT NULL AND a.job_id <> jobs.job_id
              OR EXISTS (SELECT 1 FROM jobs other WHERE other.job_id <> jobs.job_id AND other.source_artifact_id = a.artifact_id)))
        """;

    private static void AddPolicy(SqliteCommand command, string cutoff)
    {
        command.Parameters.AddWithValue("$completed", (int)JobStatus.Completed);
        command.Parameters.AddWithValue("$cutoff", cutoff);
    }

    private static void DeleteTree(string directory, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return;
        SafeFileTree.RejectLink(directory);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            SafeFileTree.RejectLink(entry);
            if (Directory.Exists(entry)) DeleteTree(entry, token);
            else File.Delete(entry);
        }
        Directory.Delete(directory);
    }
}
