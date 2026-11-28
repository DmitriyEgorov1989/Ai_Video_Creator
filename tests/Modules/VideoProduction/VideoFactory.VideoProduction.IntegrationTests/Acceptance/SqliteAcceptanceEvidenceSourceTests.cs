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
            Assert.Equal(2, passed.SchemaVersion);
            Assert.Equal(1, passed.SuccessfulDeliveryCount);
            Assert.Equal(0.03m, passed.ActualCostUsd);

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

    [Fact]
    public async Task CriticReportForDifferentVariantFailsFrameAssertions()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acceptance-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Fixture fixture = await CreateFixtureAsync(root, criticFileIdentityMismatch: true);
            AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(
                new SqliteAcceptanceEvidenceSource(fixture.DatabasePath, root))
                .CollectAsync(fixture.JobId, CancellationToken.None);

            Assert.Equal(AcceptanceEvidenceOutcome.Failed, report.Outcome);
            Assert.Contains(report.Checks, check => check.Code == "media.frame_assertions.passed" && !check.Passed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task V2DeliveryWithEvidenceForDifferentFinalIsNotSuccessfulBarrier()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acceptance-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Fixture fixture = await CreateFixtureAsync(root);
            await using (SqliteConnection connection = new($"Data Source={fixture.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE delivery_receipts
                    SET uploaded_sha256 = $differentHash
                    WHERE receipt_format_version = 2;
                    """;
                command.Parameters.AddWithValue("$differentHash", new string('b', 64));
                await command.ExecuteNonQueryAsync();
            }

            AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(
                new SqliteAcceptanceEvidenceSource(fixture.DatabasePath, root))
                .CollectAsync(fixture.JobId, CancellationToken.None);

            Assert.Contains(report.Checks, check => check.Code == "delivery.succeeded" && !check.Passed);
            Assert.Equal(0, report.SuccessfulDeliveryCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ModernRevisionCostMismatchMakesCostEvidenceIncomplete()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acceptance-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Fixture fixture = await CreateFixtureAsync(root);
            await using (SqliteConnection connection = new($"Data Source={fixture.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE edit_plan_revisions SET actual_cost_usd = '0.02' WHERE job_id = $jobId;";
                command.Parameters.AddWithValue("$jobId", fixture.JobId.ToString());
                await command.ExecuteNonQueryAsync();
            }

            AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(
                new SqliteAcceptanceEvidenceSource(fixture.DatabasePath, root))
                .CollectAsync(fixture.JobId, CancellationToken.None);

            Assert.Null(report.ActualCostUsd);
            Assert.Equal(AcceptanceEvidenceOutcome.Failed, report.Outcome);
            Assert.Contains(report.Checks, check => check.Code == "cost.actual.recorded" && !check.Passed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UnknownPlanningAttemptCostMakesCostEvidenceIncomplete()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acceptance-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Fixture fixture = await CreateFixtureAsync(root);
            await using (SqliteConnection connection = new($"Data Source={fixture.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE planning_attempts SET actual_cost_usd = NULL, status = 'started' WHERE job_id = $jobId;";
                command.Parameters.AddWithValue("$jobId", fixture.JobId.ToString());
                await command.ExecuteNonQueryAsync();
            }

            AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(
                new SqliteAcceptanceEvidenceSource(fixture.DatabasePath, root))
                .CollectAsync(fixture.JobId, CancellationToken.None);

            Assert.Null(report.ActualCostUsd);
            Assert.Contains(report.Checks, check => check.Code == "cost.actual.recorded" && !check.Passed);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LegacyFallbackCountsCumulativeRevisionCostOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acceptance-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Fixture fixture = await CreateFixtureAsync(root);
            await using (SqliteConnection connection = new($"Data Source={fixture.DatabasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM planning_attempts;
                    INSERT INTO edit_plan_revisions (
                        revision_id, job_id, variant_id, analysis_revision_id, plan_hash,
                        plan_json, actual_cost_usd, created_at)
                    SELECT $revision, job_id, variant_id, analysis_revision_id, plan_hash || '-legacy-copy',
                        plan_json, actual_cost_usd, $createdAt
                    FROM edit_plan_revisions
                    WHERE job_id = $jobId
                    LIMIT 1;
                    UPDATE edit_plan_revisions SET created_at = '1900-01-01T00:00:00.0000000+00:00'
                    WHERE job_id = $jobId;
                    """;
                command.Parameters.AddWithValue("$revision", RevisionId.New().ToString());
                command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.AddSeconds(1).ToString("O"));
                command.Parameters.AddWithValue("$jobId", fixture.JobId.ToString());
                await command.ExecuteNonQueryAsync();
            }

            AcceptanceEvidenceReport report = await new AcceptanceEvidenceCollector(
                new SqliteAcceptanceEvidenceSource(fixture.DatabasePath, root))
                .CollectAsync(fixture.JobId, CancellationToken.None);

            Assert.Equal(0.03m, report.ActualCostUsd);
            Assert.Equal(AcceptanceEvidenceOutcome.Passed, report.Outcome);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(string root, bool criticFileIdentityMismatch = false)
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
        string renderId = Guid.NewGuid().ToString();
        string criticId = Guid.NewGuid().ToString();
        string qaId = Guid.NewGuid().ToString();
        string finalId = Guid.NewGuid().ToString();
        string manifestId = Guid.NewGuid().ToString();
        object CriticReport(string reportVariantId) => new
        {
            schemaVersion = 2,
            jobId = jobId.ToString(),
            variantId = reportVariantId,
            sourceRevisionId = revisionId.ToString(),
            sourcePlanHash = planHash,
            renderArtifactId = renderId,
            remountNumber = 0,
            decision = "accepted",
            correctionRevisionId = (string?)null,
            findings = Array.Empty<object>()
        };
        JsonSerializerOptions webJson = new(JsonSerializerDefaults.Web);
        string criticMetadata = JsonSerializer.Serialize(CriticReport(variantId.ToString()), webJson);
        string criticFileJson = JsonSerializer.Serialize(
            CriticReport(criticFileIdentityMismatch ? VariantId.New().ToString() : variantId.ToString()),
            webJson);
        StoredFile critic = await WriteAsync(root, Path.Combine(artifactDirectory, "critic.json"),
            System.Text.Encoding.UTF8.GetBytes(criticFileJson));
        string qaJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            jobId = jobId.ToString(),
            variantId = variantId.ToString(),
            planRevisionId = revisionId.ToString(),
            planHash,
            status = "passed",
            checks = new { ffprobePassed = true, fullDecodePassed = true, audioAssertionsPassed = true },
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
            VALUES ($job, $source, 'instruction', 0, 0, 0, 3, 8, $revision, $variant, $now, $now, 1, 0);
            INSERT INTO variants (variant_id, job_id, current_approved_edit_plan_revision_id, created_at, updated_at)
            VALUES ($variant, $job, $revision, $now, $now);
            INSERT INTO job_sources (job_id, source_artifact_id, source_order, created_at)
            VALUES ($job, $source, 0, $now);
            INSERT INTO edit_plan_revisions (revision_id, job_id, variant_id, analysis_revision_id, plan_hash,
                plan_json, actual_cost_usd, created_at)
            VALUES ($revision, $job, $variant, $analysis, $planHash, $planJson, '0.01', $now);
            INSERT INTO artifacts (artifact_id, job_id, variant_id, artifact_type, relative_path, sha256, size_bytes,
                parent_artifact_id, metadata_json, created_at) VALUES
                ($renderId, $job, $variant, 'render-output', $renderPath, $renderHash, $renderSize, NULL, NULL, $now),
                ($criticId, $job, $variant, 'objective-critique-report', $criticPath, $criticHash, $criticSize, $renderId, $criticMetadata, $now),
                ($qaId, $job, $variant, 'qa-report', $qaPath, $qaHash, $qaSize, $renderId, NULL, $now),
                ($finalId, $job, $variant, 'final-mp4', $finalPath, $finalHash, $finalSize, $renderId, NULL, $now),
                ($manifestId, $job, $variant, 'manifest', $manifestPath, $manifestHash, $manifestSize, $finalId, NULL, $now);
            INSERT INTO planning_attempts (attempt_id, job_id, variant_id, attempt_number, repair_attempt,
                estimated_cost_usd, actual_cost_usd, status, created_at)
            VALUES ($planningAttempt, $job, $variant, 1, 0, '0.01', '0.01', 'succeeded', $now);
            INSERT INTO critique_attempts (job_id, variant_id, source_revision_id, render_artifact_id,
                remount_number, estimated_cost_usd, actual_cost_usd, status, created_at, completed_at)
            VALUES ($job, $variant, $revision, $renderId, 0, '0.02', '0.02', 'accepted', $now, $now);
            INSERT INTO delivery_receipts (receipt_id, job_id, variant_id, artifact_id, recipient_id, status,
                uploaded_sha256, uploaded_size_bytes, receipt_format_version, created_at)
            VALUES ($legacyReceipt, $job, $variant, $finalId, 42, 'succeeded', NULL, NULL, 1, $now),
                   ($receipt, $job, $variant, $finalId, 42, 'succeeded', $finalHash, $finalSize, 2, $now);
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
        Add(command, "$legacyReceipt", Guid.NewGuid().ToString());
        Add(command, "$planningAttempt", Guid.NewGuid().ToString());
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
