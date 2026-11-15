using VideoFactory.VideoProduction.Application.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

internal static class StorageBudgetRunner
{
    internal static IStorageBudget Default { get; } = new FileSystemStorageBudget(new AvailableDiskSpace(), 1_073_741_824, 5_368_709_120);

    internal static async Task<T> RunAsync<T>(IStorageBudget budget, string directory, long estimate,
        Func<CancellationToken, Task<T>> operation, Func<StorageBudgetFailure, T> reject, CancellationToken token)
    {
        StorageBudgetFailure? failure = await budget.CheckAsync(directory, estimate, token);
        if (failure is not null) return reject(failure);
        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(token);
        using CancellationTokenSource monitor = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task monitoring = MonitorAsync();
        try
        {
            T result;
            try { result = await operation(work.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && failure is not null) { return reject(failure); }
            if (failure is not null) return reject(failure);
            StorageBudgetFailure? after = await budget.CheckAsync(directory, 0, token);
            return after is null ? result : reject(after);
        }
        finally
        {
            await monitor.CancelAsync();
            try { await monitoring; }
            catch (OperationCanceledException) when (monitor.IsCancellationRequested) { }
        }

        async Task MonitorAsync()
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(monitor.Token))
            {
                StorageBudgetFailure? exceeded = await budget.CheckAsync(directory, 0, monitor.Token);
                if (exceeded is null) continue;
                failure = exceeded;
                await work.CancelAsync();
                return;
            }
        }
    }

    internal static string JobDirectory(string outputPath)
    {
        DirectoryInfo? current = new(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        while (current?.Parent is not null)
        {
            if (current.Parent.Name == "jobs" && Guid.TryParse(current.Name, out _)) return current.FullName;
            current = current.Parent;
        }
        return Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
    }
}
