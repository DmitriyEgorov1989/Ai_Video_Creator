using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Acceptance;
using VideoFactory.VideoProduction.Application.Editing;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Templates;
using VideoFactory.VideoProduction.Domain.Timeline;
using VideoFactory.VideoProduction.Infrastructure.Acceptance;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Acceptance;

public sealed class SqliteAcceptanceEvidenceSourceTests
{
    [Fact]
    public async Task CompletedEvidencePassesAndTamperedFinalFails()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acceptance-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Fixture fixture = await CreateFixtureAsync(root);
            AcceptanceEvidenceCollector collector = new(
                new SqliteAcceptanceEvidenceSource(fixture.DatabasePath, root));

            AcceptanceEvidenceReport passed = await collector.CollectAsync(fixture.JobId, CancellationToken.None);
            Assert.Equal(AcceptanceEvidenceOutcome.Passed, passed.Outcome);

            await File.AppendAllTextAsync(fixture.FinalPath, "tampered");
            AcceptanceEvidenceReport failed = await collector.CollectAsync(fixture.JobId, CancellationToken.None);
            Assert.Equal(AcceptanceEvidenceOutcome.Failed, failed.Outcome);
            Assert.Contains(failed.Checks, check => check.Code == "artifact.final-mp4.sha256" && !check.Passed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(string root)
    {
        string database = Path.Combine(root, "jobs.sqlite");
        string connectionString = $"Data Source={database};Pooling=False";
        await new SqliteMigrationRunner(connectionString).InitializeAsync(CancellationToken.None);
        JobId jobId = JobId.New();
        VariantId variantId = VariantId.New();
        RevisionId revisionId = RevisionId.New();
        EditPlan plan = new(
            4, jobId, variantId, revisionId, RevisionId.New(), 1,
            new OutputProfile(1080, 1920, new FrameRate(30, 1), 30),
            TemplateId.Dynamic,
            new TemplateProps(CaptionPreset.None, ColorTheme.Dark, null),
            new VideoTrack([]), new AudioTrack([], []), new CaptionTrack([]), new OverlayTrack([]));
        string[] skillIds =
        [
            "multi-clip-director", "semantic-story", "hook-builder", "smart-cut", "best-shot",
            "pacing-director", "speech-tightening", "caption-director", "visual-director",
            "smart-reframe", "audio-director"
        ];
        EditPlanAudit audit = new(1, [.. skillIds.Select(id =>
            new EditingSkillAuditEntry(id, "1", EditingDecisionStatus.Applied, ImmutableArray<EditingDecisionReason>.Empty))]);
        string planHash = EditPlanRevisionIdentity.CalculateHash(plan, null, audit);
        string auditHash = EditPlanRevisionIdentity.CalculateAuditHash(audit);
        string planJson = JsonSerializer.Serialize(new { schemaVersion = 1, plan, presentation = (object?)null, audit },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        string artifactDirectory = Path.Combine(root, "data", "jobs", jobId.ToString());
        Directory.CreateDirectory(artifactDirectory);
        StoredFile render = await WriteAsync(root, Path.Combine(artifactDirectory, "render.mp4"), [1, 2, 3]);
        StoredFile critic = await WriteAsync(root, Path.Combine(artifactDirectory, "critic.json"), "{}"u8.ToArray());
        string renderId = Guid.NewGuid().ToString();
        string criticId = Guid.NewGuid().ToString();
        string qaId = Guid.NewGuid().ToString();
        string finalId = Guid.NewGuid().ToString();
        string manifestId = Guid.NewGuid().ToString();
        string criticMetadata = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            sourceRevisionId = revisionId.ToString(),
            sourcePlanHash = planHash,
            renderArtifactId = renderId,
            remountNumber = 0,
            decision = "accepted",
            correctionRevisionId = (string?)null
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string qaJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            jobId = jobId.ToString(),
            variantId = variantId.ToString(),
            planRevisionId = revisionId.ToString(),
            planHash,
            status = "passed",
            output = new { artifactId = renderId, sha256 = render.Hash, sizeBytes = render.Size }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        StoredFile qa = await WriteAsync(root, Path.Combine(artifactDirectory, "qa.json"),
            System.Text.Encoding.UTF8.GetBytes(qaJson));
        StoredFile final = await WriteAsync(root, Path.Combine(artifactDirectory, "final.mp4"), [4, 5, 6]);
        string manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            jobId = jobId.ToString(),
            variantId = variantId.ToString(),
            planRevisionId = revisionId.ToString(),
            planHash,
            audit = new { schemaVersion = 1, skillCount = 11, sha256 = auditHash },
            criticChain = new[]
            {
                new
                {
                    remountNumber = 0,
                    sourceRevisionId = revisionId.ToString(),
                    sourcePlanHash = planHash,
                    render = Reference(renderId, render),
                    criticReport = Reference(criticId, critic),
                    status = "accepted",
                    correctionRevisionId = (string?)null
                }
            },
            render = Reference(renderId, render),
            qa = Reference(qaId, qa),
            final = Reference(finalId, final)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        StoredFile manifest = await WriteAsync(root, Path.Combine(artifactDirectory, "manifest.json"),
            System.Text.Encoding.UTF8.GetBytes(manifestJson));

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            INSERT INTO jobs (job_id, source_artifact_id, comment, requested_mode, captions_enabled, music_enabled,
                status, stage, current_approved_edit_plan_revision_id, active_variant_id, created_at, updated_at,
                edit_tempo, requested_style)
            VALUES ($job, $source, '', 0, 0, 0, 3, 8, $revision, $variant, $now, $now, 1, 0);
            INSERT INTO variants (variant_id, job_id, current_approved_edit_plan_revision_id, created_at, updated_at)
            VALUES ($variant, $job, $revision, $now, $now);
            INSERT INTO job_sources (job_id, source_artifact_id, source_order, created_at)
            VALUES ($job, $source, 0, $now);
            INSERT INTO edit_plan_revisions (revision_id, job_id, variant_id, analysis_revision_id, plan_hash,
                plan_json, actual_cost_usd, created_at)
            VALUES ($revision, $job, $variant, $analysis, $planHash, $planJson, '0', $now);
            INSERT INTO artifacts (artifact_id, job_id, variant_id, artifact_type, relative_path, sha256, size_bytes,
                parent_artifact_id, metadata_json, created_at) VALUES
                ($renderId, $job, $variant, 'render-output', $renderPath, $renderHash, $renderSize, NULL, NULL, $now),
                ($criticId, $job, $variant, 'objective-critique-report', $criticPath, $criticHash, $criticSize, $renderId, $criticMetadata, $now),
                ($qaId, $job, $variant, 'qa-report', $qaPath, $qaHash, $qaSize, $renderId, NULL, $now),
                ($finalId, $job, $variant, 'final-mp4', $finalPath, $finalHash, $finalSize, $renderId, NULL, $now),
                ($manifestId, $job, $variant, 'manifest', $manifestPath, $manifestHash, $manifestSize, $finalId, NULL, $now);
            INSERT INTO critique_attempts (job_id, variant_id, source_revision_id, render_artifact_id,
                remount_number, estimated_cost_usd, actual_cost_usd, status, created_at, completed_at)
            VALUES ($job, $variant, $revision, $renderId, 0, '0', '0', 'accepted', $now, $now);
            INSERT INTO delivery_receipts (receipt_id, job_id, variant_id, artifact_id, recipient_id, status, created_at)
            VALUES ($receipt, $job, $variant, $finalId, 42, 'succeeded', $now);
            """;
        Add(command, "$job", jobId.ToString());
        Add(command, "$source", Guid.NewGuid().ToString());
        Add(command, "$variant", variantId.ToString());
        Add(command, "$revision", revisionId.ToString());
        Add(command, "$analysis", plan.AnalysisRevisionId.ToString());
        Add(command, "$planHash", planHash);
        Add(command, "$planJson", planJson);
        Add(command, "$criticMetadata", criticMetadata);
        Add(command, "$now", DateTimeOffset.UtcNow.ToString("O"));
        Add(command, "$renderId", renderId); AddFile(command, "render", render);
        Add(command, "$criticId", criticId); AddFile(command, "critic", critic);
        Add(command, "$qaId", qaId); AddFile(command, "qa", qa);
        Add(command, "$finalId", finalId); AddFile(command, "final", final);
        Add(command, "$manifestId", manifestId); AddFile(command, "manifest", manifest);
        Add(command, "$receipt", Guid.NewGuid().ToString());
        await command.ExecuteNonQueryAsync();
        return new(database, jobId, Path.Combine(root, final.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static object Reference(string id, StoredFile file) => new { artifactId = id, sha256 = file.Hash, sizeBytes = file.Size };

    private static async Task<StoredFile> WriteAsync(string root, string path, byte[] bytes)
    {
        await File.WriteAllBytesAsync(path, bytes);
        return new(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength);
    }

    private static void AddFile(SqliteCommand command, string prefix, StoredFile file)
    {
        Add(command, $"${prefix}Path", file.RelativePath);
        Add(command, $"${prefix}Hash", file.Hash);
        Add(command, $"${prefix}Size", file.Size);
    }

    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);

    private sealed record StoredFile(string RelativePath, string Hash, long Size);
    private sealed record Fixture(string DatabasePath, JobId JobId, string FinalPath);
}
