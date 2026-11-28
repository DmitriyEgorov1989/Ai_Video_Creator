using VideoFactory.Bot;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public async Task AcceptanceEvidenceWithoutDatabaseWritesFailedReportWithoutCreatingStorage()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acceptance-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string report = Path.Combine(root, "evidence.json");
        try
        {
            int code = await VideoFactoryHost.RunAsync([
                "acceptance-evidence",
                $"--job={Guid.NewGuid()}",
                $"--output={report}",
                $"--Paths:WorkspaceRoot={root}",
                "--Storage:DataRoot=data"
            ]);

            Assert.Equal(1, code);
            Assert.True(File.Exists(report));
            Assert.False(Directory.Exists(Path.Combine(root, "data")));
            string json = await File.ReadAllTextAsync(report);
            Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
            Assert.Contains("\"successfulDeliveryCount\": 0", json, StringComparison.Ordinal);
            Assert.DoesNotContain("recipient", json, StringComparison.OrdinalIgnoreCase);
            Assert.True(await SatisfiesAcceptanceEvidenceV2SchemaAsync(json));
            JsonObject invalid = JsonNode.Parse(json)!.AsObject();
            invalid.Remove("uploadedSha256");
            Assert.False(await SatisfiesAcceptanceEvidenceV2SchemaAsync(invalid.ToJsonString()));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AcceptanceEvidenceWithInvalidJobReturnsUsageError()
    {
        Assert.Equal(2, await VideoFactoryHost.RunAsync([
            "acceptance-evidence", "--job=not-a-guid", "--output=evidence.json"
        ]));
    }

    [Fact]
    public async Task AcceptanceEvidenceForRunningJobReturnsInProgress()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acceptance-running-{Guid.NewGuid():N}");
        string data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        string database = Path.Combine(data, "jobs.sqlite");
        string jobId = Guid.NewGuid().ToString("N");
        try
        {
            await new SqliteMigrationRunner($"Data Source={database};Pooling=False").InitializeAsync(CancellationToken.None);
            await using (SqliteConnection connection = new($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO jobs (job_id, source_artifact_id, comment, requested_mode, captions_enabled,
                        music_enabled, status, stage, created_at, updated_at, edit_tempo, requested_style)
                    VALUES ($job, 'source', '', 0, 0, 0, 1, 5, $now, $now, 1, 0);
                    """;
                command.Parameters.AddWithValue("$job", jobId);
                command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync();
            }
            string report = Path.Combine(root, "evidence.json");

            int code = await VideoFactoryHost.RunAsync([
                "acceptance-evidence", $"--job={jobId}", $"--output={report}",
                $"--Paths:WorkspaceRoot={root}", "--Storage:DataRoot=data"
            ]);

            string json = await File.ReadAllTextAsync(report);
            Assert.True(code == 3, json);
            Assert.Contains("\"outcome\": \"InProgress\"", json, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
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

    private static async Task<bool> SatisfiesAcceptanceEvidenceV2SchemaAsync(string json)
    {
        string schemaPath = Path.Combine(FindRepositoryRoot(), "contracts", "acceptance-evidence.v2.schema.json");
        using JsonDocument schema = JsonDocument.Parse(await File.ReadAllTextAsync(schemaPath));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out JsonElement version)
            || version.GetInt32() != schema.RootElement.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32())
            return false;
        HashSet<string> required = schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        HashSet<string> allowed = schema.RootElement.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<string> actual = root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        return required.IsSubsetOf(actual) && actual.IsSubsetOf(allowed);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ai_Video_Creator.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
