using VideoFactory.Bot;
using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.Bot.Tests;

public sealed class StartupCommandTests
{
    [Fact]
    public async Task CleanupCommandPreviewsAndAppliesRetentionWithoutTelegram()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cleanup-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        string cs = $"Data Source={Path.Combine(root, "data", "jobs.sqlite")};Pooling=False";
        string id = Guid.NewGuid().ToString("N");
        string jobDirectory = Path.Combine(root, "data", "jobs", id);
        Directory.CreateDirectory(jobDirectory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(jobDirectory, "media.bin"), [1, 2, 3]);
            await new SqliteMigrationRunner(cs).InitializeAsync(CancellationToken.None);
            await using (SqliteConnection connection = new(cs))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO jobs (job_id, source_artifact_id, comment, requested_mode, captions_enabled,
                        music_enabled, status, stage, created_at, updated_at, completed_at)
                    VALUES ($id, 'source', 'private-comment', 0, 0, 0, 3, 8, '2000-01-01', '2000-01-01', '2000-01-01');
                    """;
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync();
            }
            string[] args = ["cleanup", $"--Paths:WorkspaceRoot={root}", "--Storage:DataRoot=data", "--Storage:DatabaseFileName=jobs.sqlite"];
            Assert.Equal(0, await VideoFactoryHost.RunAsync(args));
            Assert.True(Directory.Exists(jobDirectory));
            Assert.Equal(0, await VideoFactoryHost.RunAsync([.. args, "--apply"]));
            Assert.False(Directory.Exists(jobDirectory));
            Assert.Equal(0, await VideoFactoryHost.RunAsync([.. args, "--apply"]));
            SqliteConnection.ClearAllPools();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnknownCleanupSwitchReturnsUsageError()
    {
        Assert.Equal(2, await VideoFactoryHost.RunAsync(["cleanup", "--aply"]));
    }

    [Fact]
    public async Task CleanupWithoutDatabaseDoesNotCreateStorageOrRequireTelegram()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cleanup-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            int code = await VideoFactoryHost.RunAsync(["cleanup", $"--Paths:WorkspaceRoot={root}", "--Storage:DataRoot=data"]);
            Assert.Equal(1, code);
            Assert.False(Directory.Exists(Path.Combine(root, "data")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ValidConfigurationAcceptsCancellationBeforePolling()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        int exitCode = await VideoFactoryHost.RunAsync(
            ["--Telegram:BotToken=test-token", "--Telegram:AllowedUserId=123456"],
            cancellation.Token);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task UnknownCommandReturnsUsageError()
    {
        int exitCode = await VideoFactoryHost.RunAsync(["unknown"]);

        Assert.Equal(2, exitCode);
    }
}
