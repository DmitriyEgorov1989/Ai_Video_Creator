using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Reliability;

public sealed class StorageBudgetTests
{
    [Fact]
    public async Task CountsLegacyArtifactsAndRenderAttemptsAsOneJobBudget()
    {
        string root = Path.Combine(Path.GetTempPath(), $"job-budget-{Guid.NewGuid():N}");
        string id = Guid.NewGuid().ToString("N");
        string legacy = Path.Combine(root, "jobs", id);
        string rendering = Path.Combine(root, "data", "jobs", id);
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(rendering);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(legacy, "analysis.bin"), new byte[60]);
            await File.WriteAllBytesAsync(Path.Combine(rendering, "render.tmp"), new byte[40]);
            IStorageBudget budget = new FileSystemStorageBudget(new Disk(1000), 50, 100, root);
            Assert.Equal("storage.job_quota_exceeded", (await budget.CheckAsync(rendering, 1, CancellationToken.None))?.Code);
            Assert.Null(await budget.CheckAsync(legacy, 0, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ResolvesActualVolumeForNotYetCreatedDirectory()
    {
        IAvailableDiskSpace disk = new AvailableDiskSpace();
        Assert.True(disk.GetAvailableBytes(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "new")) >= 0);
    }

    [Fact]
    public async Task RejectsOperationThatConsumesDiskReserve()
    {
        IStorageBudget budget = new FileSystemStorageBudget(new Disk(99), 50, 1_000);
        StorageBudgetFailure? failure = await budget.CheckAsync(Path.GetTempPath(), 50, CancellationToken.None);
        Assert.Equal("storage.insufficient_space", failure?.Code);
    }

    private sealed class Disk(long bytes) : IAvailableDiskSpace
    {
        public long GetAvailableBytes(string path) => bytes;
    }

    [Fact]
    public async Task CountsNestedTemporaryFilesAgainstQuota()
    {
        string root = Path.Combine(Path.GetTempPath(), $"budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "attempt"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "attempt", "render.tmp"), new byte[60]);
            IStorageBudget budget = new FileSystemStorageBudget(new Disk(1_000), 50, 100);
            StorageBudgetFailure? failure = await budget.CheckAsync(root, 41, CancellationToken.None);
            Assert.Equal("storage.job_quota_exceeded", failure?.Code);
            Assert.Null(await budget.CheckAsync(root, 40, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
}
