using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;

namespace VideoFactory.VideoProduction.IntegrationTests.Reliability;

public sealed class CleanupTests
{
    [Fact]
    public async Task CleanedJobCannotReceiveArtifactsOrBecomeSourceForNewJob()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cleanup-tombstone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string cs = $"Data Source={Path.Combine(root, "jobs.db")};Pooling=False";
        try
        {
            await new SqliteMigrationRunner(cs).InitializeAsync(CancellationToken.None);
            string directory = await SeedAsync(root, cs, JobStatus.Completed, "2000-01-01T00:00:00Z");
            JobId id = new(Guid.Parse(Path.GetFileName(directory)));
            ArtifactId source = ArtifactId.New();
            IArtifactStore artifacts = new SqliteArtifactStore(cs);
            await artifacts.RegisterAsync(new ArtifactRecord(source, "OriginalVideo", $"data/jobs/{id}/media.bin", null, 3, null, null, "test", id), CancellationToken.None);
            Assert.Equal(1, (await new SqliteJobCleanup(cs, root, 30, TimeProvider.System).RunAsync(true, CancellationToken.None)).Deleted);
            Assert.IsType<ArtifactRegistrationResult.Rejected>(await artifacts.RegisterAsync(
                new ArtifactRecord(ArtifactId.New(), "analysis", $"data/jobs/{id}/new.json", null, 3, null, null, "test", id), CancellationToken.None));
            CreateJobResult result = await new CreateJobUseCase(new SqliteJobCreationStore(cs)).ExecuteAsync(
                new CreateJobCommand(source, "private", new ProductionSettings(null, false, false)));
            Assert.IsType<CreateJobResult.Rejected>(result);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("lease")]
    [InlineData("waiting")]
    [InlineData("failed")]
    [InlineData("traversal")]
    [InlineData("interrupted")]
    public async Task CleanupProtectsNonEligibleDataAndResumesInterruptedDeletion(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), $"cleanup-fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string cs = $"Data Source={Path.Combine(root, "jobs.db")};Pooling=False";
        try
        {
            await new SqliteMigrationRunner(cs).InitializeAsync(CancellationToken.None);
            string directory = await SeedAsync(root, cs, JobStatus.Completed, "2000-01-01T00:00:00Z");
            await using (SqliteConnection connection = new(cs))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = scenario switch
                {
                    "lease" => "UPDATE jobs SET lease_owner = 'active-worker';",
                    "waiting" => $"UPDATE jobs SET status = {(int)JobStatus.AwaitingConfirmation};",
                    "failed" => $"UPDATE jobs SET status = {(int)JobStatus.Failed};",
                    "traversal" => "UPDATE jobs SET job_id = '../outside';",
                    _ => "UPDATE jobs SET cleanup_started_at = '2000-02-01T00:00:00Z';"
                };
                await command.ExecuteNonQueryAsync();
            }
            if (scenario == "interrupted") File.Delete(Path.Combine(directory, "media.bin"));
            CleanupReport report = await new SqliteJobCleanup(cs, root, 30, TimeProvider.System).RunAsync(true, CancellationToken.None);
            Assert.Equal(scenario == "interrupted" ? 1 : 0, report.Deleted);
            Assert.Equal(scenario != "interrupted", Directory.Exists(directory));
            Assert.Equal(scenario == "traversal" ? 1 : 0, report.Errors);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeletesOnlyExpiredCompletedJobAndCanRunAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string connectionString = $"Data Source={Path.Combine(root, "jobs.db")};Pooling=False";
        try
        {
            await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
            string expired = await SeedAsync(root, connectionString, JobStatus.Completed, "2000-01-01T00:00:00Z");
            string active = await SeedAsync(root, connectionString, JobStatus.Running, "2000-01-01T00:00:00Z");
            string recent = await SeedAsync(root, connectionString, JobStatus.Completed, DateTimeOffset.UtcNow.ToString("O"));
            IJobCleanup cleanup = new SqliteJobCleanup(connectionString, root, 30, TimeProvider.System);

            CleanupReport preview = await cleanup.RunAsync(false, CancellationToken.None);
            Assert.Equal(1, preview.Candidates);
            Assert.True(Directory.Exists(expired));
            CleanupReport applied = await cleanup.RunAsync(true, CancellationToken.None);
            Assert.Equal(1, applied.Deleted);
            Assert.Equal(3, applied.FreedBytes);
            Assert.False(Directory.Exists(expired));
            Assert.True(Directory.Exists(active));
            Assert.True(Directory.Exists(recent));
            Assert.Equal(0, (await cleanup.RunAsync(true, CancellationToken.None)).Candidates);
            await using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM jobs;";
            Assert.Equal(3L, await command.ExecuteScalarAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<string> SeedAsync(string root, string connectionString, JobStatus status, string completedAt)
    {
        string id = JobId.New().ToString();
        string directory = Path.Combine(root, "data", "jobs", id);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "media.bin"), [1, 2, 3]);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (job_id, source_artifact_id, comment, requested_mode, captions_enabled, music_enabled,
                status, stage, created_at, updated_at, completed_at)
            VALUES ($id, $source, 'private', 0, 1, 0, $status, 8, $date, $date, $date);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", ArtifactId.New().ToString());
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$date", completedAt);
        await command.ExecuteNonQueryAsync();
        return directory;
    }
}
