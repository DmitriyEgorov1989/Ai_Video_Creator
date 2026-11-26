using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Acceptance;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Packaging;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.Infrastructure.Acceptance;

/// <summary>Читает acceptance evidence из существующей SQLite и локального хранилища без изменений данных.</summary>
public sealed class SqliteAcceptanceEvidenceSource : IAcceptanceEvidenceSource
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string databasePath;
    private readonly string workspaceRoot;

    /// <summary>Создаёт read-only источник.</summary>
    /// <param name="databasePath">Полный путь к существующей SQLite.</param>
    /// <param name="workspaceRoot">Корень, относительно которого зарегистрированы артефакты.</param>
    public SqliteAcceptanceEvidenceSource(string databasePath, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));
        if (string.IsNullOrWhiteSpace(workspaceRoot)) throw new ArgumentException("Workspace root cannot be empty.", nameof(workspaceRoot));
        this.databasePath = Path.GetFullPath(databasePath);
        this.workspaceRoot = Path.GetFullPath(workspaceRoot);
    }

    /// <inheritdoc />
    public async Task<AcceptanceEvidenceSnapshot> ReadAsync(JobId jobId, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
            return Missing(jobId, "storage.database_missing");

        try
        {
            SqliteConnectionStringBuilder builder = new()
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            await using SqliteConnection connection = new(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            JobRow? job = await ReadJobAsync(connection, transaction, jobId, cancellationToken);
            if (job is null)
                return AcceptanceEvidenceSnapshot.Completed(jobId) with { Found = false, JobStatus = null };
            if (job.Status is JobStatus.Queued or JobStatus.Running or JobStatus.AwaitingConfirmation)
                return AcceptanceEvidenceSnapshot.Completed(jobId) with { JobStatus = job.Status };

            int sourceCount = await ReadSourceCountAsync(connection, transaction, jobId, cancellationToken);
            PlanRow? plan = await ReadPlanAsync(connection, transaction, jobId, job, cancellationToken);
            IReadOnlyList<ArtifactRow> rows = await ReadArtifactsAsync(connection, transaction, jobId, job.ActiveVariantId, cancellationToken);
            ArtifactRow? manifestRow = rows.LastOrDefault(row => row.Type == "manifest");
            if (plan is null || manifestRow is null)
                return Base(jobId, job, sourceCount) with { ReadErrorCode = "evidence.required_record_missing" };

            string manifestPath = SafeFileTree.Resolve(workspaceRoot, manifestRow.RelativePath);
            if (!File.Exists(manifestPath))
                return Base(jobId, job, sourceCount) with
                {
                    PlanHash = plan.StoredHash,
                    RecomputedPlanHash = plan.RecomputedHash,
                    SkillIds = plan.SkillIds,
                    RecomputedAuditHash = plan.AuditHash,
                    Artifacts = [await InspectAsync(manifestRow, cancellationToken)]
                };

            using JsonDocument manifestDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(manifestPath, cancellationToken));
            JsonElement manifest = manifestDocument.RootElement;
            IReadOnlyList<ManifestReference> references = ReadManifestReferences(manifest);
            List<AcceptanceArtifactEvidence> artifacts = [];
            foreach (ManifestReference reference in references)
            {
                ArtifactRow? row = rows.SingleOrDefault(candidate => candidate.Id == reference.ArtifactId);
                if (row is not null) artifacts.Add(await InspectAsync(row, cancellationToken));
            }
            artifacts.Add(await InspectAsync(manifestRow, cancellationToken));

            bool linksMatch = references.Count >= 4 && references.All(reference =>
                rows.SingleOrDefault(row => row.Id == reference.ArtifactId) is ArtifactRow artifact
                && artifact.StoredSha256 == reference.Sha256 && artifact.StoredSizeBytes == reference.SizeBytes);
            string? finalId = ReadReference(manifest, "final")?.ArtifactId;
            bool deliverySucceeded = finalId is not null && await ReadDeliverySucceededAsync(
                connection, transaction, jobId, job.ActiveVariantId, finalId, cancellationToken);
            bool criticValid = await ValidateCriticChainAsync(
                connection, transaction, jobId, job.ActiveVariantId, job.ApprovedRevisionId, rows, manifest, cancellationToken);
            bool qaPassed = await ValidateQaAsync(rows, manifest, jobId, job, plan.StoredHash, cancellationToken);
            string? auditHash = manifest.TryGetProperty("audit", out JsonElement manifestAudit)
                && manifestAudit.TryGetProperty("sha256", out JsonElement auditHashElement)
                ? auditHashElement.GetString() : null;

            bool correlation = GetString(manifest, "jobId") == jobId.ToString()
                && GetString(manifest, "variantId") == job.ActiveVariantId
                && GetString(manifest, "planRevisionId") == job.ApprovedRevisionId
                && GetString(manifest, "planHash") == plan.StoredHash
                && job.ApprovedRevisionId == plan.RevisionId
                && job.ApprovedRevisionId == job.VariantApprovedRevisionId;
            bool manifestV2 = manifest.TryGetProperty("schemaVersion", out JsonElement version)
                && version.TryGetInt32(out int schemaVersion) && schemaVersion == 2
                && manifest.TryGetProperty("audit", out JsonElement audit)
                && audit.TryGetProperty("schemaVersion", out JsonElement auditVersion)
                && auditVersion.TryGetInt32(out int auditSchemaVersion) && auditSchemaVersion == 1
                && audit.TryGetProperty("skillCount", out JsonElement skillCount)
                && skillCount.TryGetInt32(out int count) && count == 11;
            manifestV2 = manifestV2
                && FinalManifestContractDispatcher.Dispatch(manifest).Status == FinalManifestContractStatus.Accepted;

            return Base(jobId, job, sourceCount) with
            {
                PlanHash = plan.StoredHash,
                RecomputedPlanHash = plan.RecomputedHash,
                SkillIds = plan.SkillIds,
                AuditHash = auditHash,
                RecomputedAuditHash = plan.AuditHash,
                CorrelationMatches = correlation,
                ManifestV2 = manifestV2,
                ManifestLinksMatch = linksMatch && ValidateLineage(rows, manifestRow, manifest),
                CriticChainValid = criticValid,
                QaPassed = qaPassed,
                DeliverySucceeded = deliverySucceeded,
                Artifacts = artifacts
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return Missing(jobId, exception is SqliteException ? "storage.read_failed" : "evidence.invalid");
        }
    }

    private static AcceptanceEvidenceSnapshot Missing(JobId jobId, string code) =>
        AcceptanceEvidenceSnapshot.Completed(jobId) with { ReadErrorCode = code };

    private static AcceptanceEvidenceSnapshot Base(JobId jobId, JobRow job, int sourceCount) =>
        AcceptanceEvidenceSnapshot.Completed(jobId) with
        {
            JobStatus = job.Status,
            JobStage = job.Stage,
            VariantId = job.ActiveVariantId,
            ApprovedRevisionId = job.ApprovedRevisionId,
            SourceCount = sourceCount,
            SourceMode = sourceCount == 1 ? AcceptanceSourceMode.SingleSource : AcceptanceSourceMode.MultiSource
        };

    private static async Task<JobRow?> ReadJobAsync(
        SqliteConnection connection, SqliteTransaction transaction, JobId jobId, CancellationToken token)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT j.status, j.stage, j.active_variant_id, j.current_approved_edit_plan_revision_id,
                   v.current_approved_edit_plan_revision_id
            FROM jobs j
            LEFT JOIN variants v ON v.job_id = j.job_id AND v.variant_id = j.active_variant_id
            WHERE j.job_id = $jobId;
            """);
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new((JobStatus)reader.GetInt32(0), (ProductionStage)reader.GetInt32(1),
            NullString(reader, 2), NullString(reader, 3), NullString(reader, 4));
    }

    private static async Task<int> ReadSourceCountAsync(
        SqliteConnection connection, SqliteTransaction transaction, JobId jobId, CancellationToken token)
    {
        await using SqliteCommand command = Command(connection, transaction,
            "SELECT COUNT(*) FROM job_sources WHERE job_id = $jobId;");
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    private static async Task<PlanRow?> ReadPlanAsync(
        SqliteConnection connection, SqliteTransaction transaction, JobId jobId, JobRow job, CancellationToken token)
    {
        if (job.ActiveVariantId is null || job.ApprovedRevisionId is null) return null;
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT revision_id, plan_hash, plan_json
            FROM edit_plan_revisions
            WHERE job_id = $jobId AND variant_id = $variantId AND revision_id = $revisionId;
            """);
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", job.ActiveVariantId);
        command.Parameters.AddWithValue("$revisionId", job.ApprovedRevisionId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        string json = reader.GetString(2);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        EditPlan plan;
        ReelsPresentationPlan? presentation = null;
        EditPlanAudit? audit = null;
        if (root.TryGetProperty("plan", out JsonElement planElement))
        {
            plan = planElement.Deserialize<EditPlan>(JsonOptions)!;
            if (root.TryGetProperty("presentation", out JsonElement presentationElement)
                && presentationElement.ValueKind != JsonValueKind.Null)
                presentation = presentationElement.Deserialize<ReelsPresentationPlan>(JsonOptions);
            if (root.TryGetProperty("audit", out JsonElement auditElement)
                && auditElement.ValueKind != JsonValueKind.Null)
                audit = auditElement.Deserialize<EditPlanAudit>(JsonOptions);
        }
        else plan = root.Deserialize<EditPlan>(JsonOptions)!;
        return new(reader.GetString(0), reader.GetString(1),
            EditPlanRevisionIdentity.CalculateHash(plan, presentation, audit),
            audit?.Skills.Select(skill => skill.SkillId).ToArray() ?? [],
            audit is null ? null : EditPlanRevisionIdentity.CalculateAuditHash(audit));
    }

    private static async Task<IReadOnlyList<ArtifactRow>> ReadArtifactsAsync(
        SqliteConnection connection, SqliteTransaction transaction, JobId jobId, string? variantId, CancellationToken token)
    {
        if (variantId is null) return [];
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT artifact_id, artifact_type, relative_path, sha256, size_bytes, parent_artifact_id, metadata_json
            FROM artifacts
            WHERE job_id = $jobId AND variant_id = $variantId
            ORDER BY created_at, artifact_id;
            """);
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        List<ArtifactRow> rows = [];
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), NullString(reader, 5), NullString(reader, 6)));
        return rows;
    }

    private static async Task<bool> ReadDeliverySucceededAsync(
        SqliteConnection connection, SqliteTransaction transaction, JobId jobId, string? variantId,
        string artifactId, CancellationToken token)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM delivery_receipts
                WHERE job_id = $jobId AND variant_id = $variantId
                  AND artifact_id = $artifactId AND status = 'succeeded');
            """);
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId ?? string.Empty);
        command.Parameters.AddWithValue("$artifactId", artifactId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token)) == 1;
    }

    private static async Task<bool> ValidateCriticChainAsync(
        SqliteConnection connection, SqliteTransaction transaction, JobId jobId, string? variantId,
        string? approvedRevisionId, IReadOnlyList<ArtifactRow> artifacts, JsonElement manifest, CancellationToken token)
    {
        if (variantId is null || !manifest.TryGetProperty("criticChain", out JsonElement chain)
            || chain.ValueKind != JsonValueKind.Array) return false;
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT c.source_revision_id, c.render_artifact_id, c.correction_revision_id,
                   c.remount_number, c.status, c.completed_at, p.plan_hash
            FROM critique_attempts c
            JOIN edit_plan_revisions p ON p.revision_id = c.source_revision_id
            WHERE c.job_id = $jobId AND c.variant_id = $variantId
            ORDER BY c.remount_number, c.created_at;
            """);
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token);
        List<CriticRow> rows = [];
        while (await reader.ReadAsync(token))
            rows.Add(new(reader.GetString(0), reader.GetString(1), NullString(reader, 2), reader.GetInt32(3),
                reader.GetString(4), !reader.IsDBNull(5), reader.GetString(6)));
        JsonElement[] manifestRows = chain.EnumerateArray().ToArray();
        if (rows.Count == 0 || rows.Count != manifestRows.Length) return false;
        for (int index = 0; index < rows.Count; index++)
        {
            CriticRow row = rows[index];
            JsonElement item = manifestRows[index];
            ManifestReference? reportReference = ReadReference(item, "criticReport");
            ArtifactRow? render = artifacts.SingleOrDefault(artifact => artifact.Id == row.RenderArtifactId);
            ArtifactRow? report = reportReference is null
                ? null
                : artifacts.SingleOrDefault(artifact => artifact.Id == reportReference.ArtifactId);
            if (!row.Completed || row.RemountNumber != index || GetString(item, "sourceRevisionId") != row.SourceRevisionId
                || ReadReference(item, "render")?.ArtifactId != row.RenderArtifactId
                || GetString(item, "sourcePlanHash") != row.SourcePlanHash
                || GetString(item, "status") != row.Status
                || GetNullableString(item, "correctionRevisionId") != row.CorrectionRevisionId
                || render?.Type != "render-output" || report?.Type != "objective-critique-report"
                || report.ParentId != render.Id || !CriticMetadataMatches(report.MetadataJson, row))
                return false;
            if (row.Status == "remount" && (row.CorrectionRevisionId is null || index == rows.Count - 1)) return false;
            if (row.Status == "accepted" && (row.CorrectionRevisionId is not null || index != rows.Count - 1)) return false;
            if (index + 1 < rows.Count && row.CorrectionRevisionId != rows[index + 1].SourceRevisionId) return false;
        }
        CriticRow final = rows[^1];
        return final.Status == "accepted" && final.SourceRevisionId == approvedRevisionId && final.CorrectionRevisionId is null;
    }

    private static bool CriticMetadataMatches(string? metadataJson, CriticRow attempt)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return false;
        using JsonDocument document = JsonDocument.Parse(metadataJson);
        JsonElement root = document.RootElement;
        return GetString(root, "sourceRevisionId") == attempt.SourceRevisionId
            && GetString(root, "sourcePlanHash") == attempt.SourcePlanHash
            && GetString(root, "renderArtifactId") == attempt.RenderArtifactId
            && root.TryGetProperty("remountNumber", out JsonElement remount)
            && remount.TryGetInt32(out int number) && number == attempt.RemountNumber
            && GetString(root, "decision") == attempt.Status
            && GetNullableString(root, "correctionRevisionId") == attempt.CorrectionRevisionId;
    }

    private async Task<bool> ValidateQaAsync(
        IReadOnlyList<ArtifactRow> rows, JsonElement manifest, JobId jobId, JobRow job, string planHash, CancellationToken token)
    {
        ManifestReference? qaReference = ReadReference(manifest, "qa");
        ManifestReference? renderReference = ReadReference(manifest, "render");
        ArtifactRow? qa = qaReference is null ? null : rows.SingleOrDefault(row => row.Id == qaReference.ArtifactId);
        if (qa is null || renderReference is null || qa.ParentId != renderReference.ArtifactId) return false;
        string path = SafeFileTree.Resolve(workspaceRoot, qa.RelativePath);
        if (!File.Exists(path)) return false;
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token));
        JsonElement root = document.RootElement;
        return GetString(root, "status") == "passed"
            && MatchesWrappedId(root, "jobId", jobId.ToString())
            && MatchesWrappedId(root, "variantId", job.ActiveVariantId)
            && MatchesWrappedId(root, "planRevisionId", job.ApprovedRevisionId)
            && GetString(root, "planHash") == planHash
            && root.TryGetProperty("output", out JsonElement output)
            && MatchesWrappedId(output, "artifactId", renderReference.ArtifactId);
    }

    private static bool ValidateLineage(IReadOnlyList<ArtifactRow> rows, ArtifactRow manifestRow, JsonElement manifest)
    {
        ManifestReference? render = ReadReference(manifest, "render");
        ManifestReference? qa = ReadReference(manifest, "qa");
        ManifestReference? final = ReadReference(manifest, "final");
        if (render is null || qa is null || final is null) return false;
        ArtifactRow? qaRow = rows.SingleOrDefault(row => row.Id == qa.ArtifactId);
        ArtifactRow? finalRow = rows.SingleOrDefault(row => row.Id == final.ArtifactId);
        return qaRow?.ParentId == render.ArtifactId && finalRow?.ParentId == render.ArtifactId
            && manifestRow.ParentId == final.ArtifactId;
    }

    private async Task<AcceptanceArtifactEvidence> InspectAsync(ArtifactRow row, CancellationToken token)
    {
        string path = SafeFileTree.Resolve(workspaceRoot, row.RelativePath);
        if (!File.Exists(path))
            return new(row.Type, row.Id, row.StoredSha256, row.StoredSizeBytes, false, null, null);
        FileInfo file = new(path);
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        return new(row.Type, row.Id, row.StoredSha256, row.StoredSizeBytes, true, hash, file.Length);
    }

    private static IReadOnlyList<ManifestReference> ReadManifestReferences(JsonElement manifest)
    {
        List<ManifestReference> references = [];
        foreach (string name in new[] { "render", "qa", "final" })
            if (ReadReference(manifest, name) is { } reference) references.Add(reference);
        if (manifest.TryGetProperty("criticChain", out JsonElement chain) && chain.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in chain.EnumerateArray())
                foreach (string name in new[] { "render", "criticReport" })
                    if (ReadReference(item, name) is { } reference) references.Add(reference);
        return references.DistinctBy(reference => reference.ArtifactId).ToArray();
    }

    private static ManifestReference? ReadReference(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object) return null;
        string? id = GetString(value, "artifactId");
        string? hash = GetString(value, "sha256");
        return id is not null && hash is not null && value.TryGetProperty("sizeBytes", out JsonElement size)
            && size.TryGetInt64(out long bytes) ? new(id, hash, bytes) : null;
    }

    private static bool MatchesWrappedId(JsonElement parent, string name, string? expected) =>
        expected is not null && parent.TryGetProperty(name, out JsonElement element)
        && (element.ValueKind == JsonValueKind.String ? element.GetString() : GetString(element, "value")) == expected;

    private static string? GetString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? GetNullableString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? NullString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string text)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    private sealed record JobRow(JobStatus Status, ProductionStage Stage, string? ActiveVariantId, string? ApprovedRevisionId, string? VariantApprovedRevisionId);
    private sealed record PlanRow(string RevisionId, string StoredHash, string RecomputedHash, IReadOnlyList<string> SkillIds, string? AuditHash);
    private sealed record ArtifactRow(string Id, string Type, string RelativePath, string? StoredSha256, long? StoredSizeBytes, string? ParentId, string? MetadataJson);
    private sealed record CriticRow(string SourceRevisionId, string RenderArtifactId, string? CorrectionRevisionId, int RemountNumber, string Status, bool Completed, string SourcePlanHash);
    private sealed record ManifestReference(string ArtifactId, string Sha256, long SizeBytes);
}
