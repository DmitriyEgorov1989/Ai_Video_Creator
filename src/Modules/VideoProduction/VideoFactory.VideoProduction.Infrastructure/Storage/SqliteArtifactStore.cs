using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>
/// Регистрирует описания артефактов в SQLite без хранения содержимого файлов.
/// </summary>
public sealed class SqliteArtifactStore : IArtifactStore
{
    private readonly string connectionString;

    /// <summary>
    /// Создаёт SQLite store артефактов.
    /// </summary>
    /// <param name="connectionString">Строка подключения к базе данных.</param>
    public SqliteArtifactStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<ArtifactRecord?> GetAsync(
        ArtifactId artifactId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, job_id, artifact_type, relative_path, sha256, size_bytes,
                   metadata_json, parent_artifact_id, tool_version, variant_id
            FROM artifacts
            WHERE artifact_id = $artifactId;
            """;
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    /// <inheritdoc />
    public async Task<ArtifactRecord?> FindLatestAsync(
        JobId jobId,
        string artifactType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactType);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, job_id, artifact_type, relative_path, sha256, size_bytes,
                   metadata_json, parent_artifact_id, tool_version, variant_id
            FROM artifacts
            WHERE job_id = $jobId AND artifact_type = $artifactType
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$artifactType", artifactType);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<ArtifactRecord?> FindLatestAsync(
        JobId jobId,
        VariantId variantId,
        string artifactType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactType);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, job_id, artifact_type, relative_path, sha256, size_bytes,
                   metadata_json, parent_artifact_id, tool_version, variant_id
            FROM artifacts
            WHERE job_id = $jobId AND variant_id = $variantId AND artifact_type = $artifactType
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$artifactType", artifactType);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRecord(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtifactRecord>> ListAsync(
        JobId jobId,
        string artifactType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactType);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, job_id, artifact_type, relative_path, sha256, size_bytes,
                   metadata_json, parent_artifact_id, tool_version, variant_id
            FROM artifacts
            WHERE job_id = $jobId AND artifact_type = $artifactType
            ORDER BY created_at, artifact_id;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$artifactType", artifactType);
        List<ArtifactRecord> records = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) records.Add(ReadRecord(reader));
        return records;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtifactRecord>> ListAsync(
        JobId jobId,
        VariantId variantId,
        string artifactType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactType);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id, job_id, artifact_type, relative_path, sha256, size_bytes,
                   metadata_json, parent_artifact_id, tool_version, variant_id
            FROM artifacts
            WHERE job_id = $jobId AND variant_id = $variantId AND artifact_type = $artifactType
            ORDER BY created_at, artifact_id;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$artifactType", artifactType);
        List<ArtifactRecord> records = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) records.Add(ReadRecord(reader));
        return records;
    }

    /// <inheritdoc />
    public async Task<ArtifactRegistrationResult> RegisterAsync(
        ArtifactRecord record,
        CancellationToken cancellationToken)
    {
        ArtifactRegistrationResult? validation = Validate(record);
        if (validation is not null)
        {
            return validation;
        }

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (SqliteCommand cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                SELECT EXISTS (SELECT 1 FROM jobs j WHERE j.cleanup_started_at IS NOT NULL
                  AND (j.job_id = $jobId OR $path LIKE 'data/jobs/' || j.job_id || '/%'
                    OR $path LIKE 'jobs/' || j.job_id || '/%'));
                """;
            cleanup.Parameters.AddWithValue("$jobId", record.JobId?.ToString() ?? "");
            cleanup.Parameters.AddWithValue("$path", record.RelativePath.Replace('\\', '/'));
            if (Convert.ToInt64(await cleanup.ExecuteScalarAsync(cancellationToken)) != 0)
                return new ArtifactRegistrationResult.Rejected("Job files are being cleaned or have expired.");
        }

        ArtifactRecord? existing = await FindAsync(
            connection,
            transaction,
            record.ArtifactId,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ArtifactRegistrationResult.Existing(existing);
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO artifacts (
                artifact_id, job_id, artifact_type, relative_path, sha256, size_bytes,
                metadata_json, parent_artifact_id, tool_version, created_at, variant_id)
            VALUES (
                $artifactId, $jobId, $artifactType, $relativePath, $sha256, $sizeBytes,
                $metadataJson, $parentArtifactId, $toolVersion, $createdAt, $variantId);
            """;
        command.Parameters.AddWithValue("$artifactId", record.ArtifactId.ToString());
        command.Parameters.AddWithValue("$jobId", record.JobId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$artifactType", record.ArtifactType);
        command.Parameters.AddWithValue("$relativePath", record.RelativePath);
        command.Parameters.AddWithValue("$sha256", (object?)record.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$sizeBytes", (object?)record.SizeBytes ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadataJson", (object?)record.MetadataJson ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$parentArtifactId",
            record.ParentArtifactId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$toolVersion", (object?)record.ToolVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$variantId", record.VariantId?.ToString() ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ArtifactRegistrationResult.Created(record);
    }

    private static ArtifactRegistrationResult? Validate(ArtifactRecord record)
    {
        if (record.ArtifactId.IsEmpty)
        {
            return new ArtifactRegistrationResult.Rejected("Artifact ID cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(record.ArtifactType))
        {
            return new ArtifactRegistrationResult.Rejected("Artifact type cannot be empty.");
        }

        if (!SafeFileTree.IsSafeRelativePath(record.RelativePath))
        {
            return new ArtifactRegistrationResult.Rejected("Artifact path must be a safe relative path.");
        }

        return null;
    }

    private static async Task<ArtifactRecord?> FindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactId artifactId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT artifact_id, job_id, artifact_type, relative_path, sha256, size_bytes,
                   metadata_json, parent_artifact_id, tool_version, variant_id
            FROM artifacts
            WHERE artifact_id = $artifactId;
            """;
        command.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadRecord(reader);
    }

    private static ArtifactRecord ReadRecord(SqliteDataReader reader) => new(
        new ArtifactId(Guid.Parse(reader.GetString(0))),
        reader.GetString(2),
        reader.GetString(3),
        GetNullableString(reader, 4),
        GetNullableInt64(reader, 5),
        GetNullableString(reader, 6),
        GetNullableArtifactId(reader, 7),
        GetNullableString(reader, 8),
        reader.IsDBNull(1) ? null : new JobId(Guid.Parse(reader.GetString(1))),
        reader.IsDBNull(9) ? null : new VariantId(Guid.Parse(reader.GetString(9))));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static ArtifactId? GetNullableArtifactId(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : new ArtifactId(Guid.Parse(reader.GetString(ordinal)));
}
