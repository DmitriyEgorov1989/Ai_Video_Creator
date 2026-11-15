using VideoFactory.VideoProduction.Application.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Проверяет локальный дисковый бюджет перед созданием артефактов.</summary>
/// <param name="disk">Источник доступного места; не <see langword="null"/>.</param>
/// <param name="minimumFreeBytes">Резерв диска после операции в байтах.</param>
/// <param name="quotaBytes">Максимальный размер каталога Job в байтах.</param>
/// <param name="workspaceRoot">Корень для совместного учёта каталогов Job; <see langword="null"/> ограничивает учёт переданным каталогом.</param>
public sealed class FileSystemStorageBudget(IAvailableDiskSpace disk, long minimumFreeBytes, long quotaBytes, string? workspaceRoot = null) : IStorageBudget
{
    /// <inheritdoc />
    public Task<StorageBudgetFailure?> CheckAsync(string directory, long additionalBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFreeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quotaBytes);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            long available = disk.GetAvailableBytes(directory);
            if (available < additionalBytes || available - additionalBytes < minimumFreeBytes)
                return Task.FromResult<StorageBudgetFailure?>(new("storage.insufficient_space", "Insufficient free disk space."));
            long used = SafeFileTree.SizeBytes(directory, cancellationToken);
            DirectoryInfo target = new(Path.GetFullPath(directory));
            if (workspaceRoot is not null && target.Parent?.Name == "jobs" && Guid.TryParseExact(target.Name, "N", out _))
            {
                string[] jobPaths = [SafeFileTree.Resolve(workspaceRoot, $"jobs/{target.Name}"),
                    SafeFileTree.Resolve(workspaceRoot, $"data/jobs/{target.Name}")];
                StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                if (jobPaths.Contains(target.FullName, comparer))
                    used = checked(SafeFileTree.SizeBytes(jobPaths[0], cancellationToken) + SafeFileTree.SizeBytes(jobPaths[1], cancellationToken));
            }
            if (additionalBytes > quotaBytes || used > quotaBytes - additionalBytes)
                return Task.FromResult<StorageBudgetFailure?>(new("storage.job_quota_exceeded", "Job directory quota would be exceeded."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return Task.FromResult<StorageBudgetFailure?>(new("storage.unavailable", "Storage capacity could not be verified."));
        }
        return Task.FromResult<StorageBudgetFailure?>(null);
    }
}

/// <summary>Получает доступное место на томе, содержащем ближайший существующий каталог.</summary>
public sealed class AvailableDiskSpace : IAvailableDiskSpace
{
    /// <inheritdoc />
    public long GetAvailableBytes(string path)
    {
        string existing = Path.GetFullPath(path);
        while (!Directory.Exists(existing))
            existing = Path.GetDirectoryName(existing) ?? throw new IOException("Storage volume not found.");
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        DriveInfo drive = DriveInfo.GetDrives()
            .Where(item => existing.Equals(Path.TrimEndingDirectorySeparator(item.Name), comparison)
                || existing.StartsWith(item.Name.EndsWith(Path.DirectorySeparatorChar)
                    ? item.Name : item.Name + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(item => item.Name.Length).FirstOrDefault()
            ?? throw new IOException("Storage volume not found.");
        return drive.AvailableFreeSpace;
    }
}
