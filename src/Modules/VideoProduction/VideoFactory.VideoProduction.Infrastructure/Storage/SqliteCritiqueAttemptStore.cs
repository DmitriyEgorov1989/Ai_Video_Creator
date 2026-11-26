using System.Globalization;
using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Critique;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Сохраняет восстанавливаемый аудит попыток критики в SQLite.</summary>
public sealed class SqliteCritiqueAttemptStore : ICritiqueAttemptStore
{
    private readonly string connectionString;

    /// <summary>Создаёт хранилище для указанной SQLite-базы.</summary>
    /// <param name="connectionString">Непустая строка подключения.</param>
    public SqliteCritiqueAttemptStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task SaveAsync(CritiqueAttemptRecord attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO critique_attempts (
                job_id, variant_id, source_revision_id, render_artifact_id,
                correction_revision_id, remount_number, estimated_cost_usd,
                actual_cost_usd, status, error_code, error_message, created_at, completed_at)
            VALUES (
                $jobId, $variantId, $sourceRevisionId, $renderArtifactId,
                $correctionRevisionId, $remountNumber, $estimatedCostUsd,
                $actualCostUsd, $status, $errorCode, $errorMessage, $createdAt, $completedAt)
            ON CONFLICT (variant_id, source_revision_id, render_artifact_id) DO UPDATE SET
                correction_revision_id = COALESCE(excluded.correction_revision_id, correction_revision_id),
                actual_cost_usd = COALESCE(excluded.actual_cost_usd, actual_cost_usd),
                status = CASE WHEN completed_at IS NULL THEN excluded.status ELSE status END,
                error_code = CASE WHEN completed_at IS NULL THEN excluded.error_code ELSE error_code END,
                error_message = CASE WHEN completed_at IS NULL THEN excluded.error_message ELSE error_message END,
                completed_at = COALESCE(completed_at, excluded.completed_at);
            """;
        AddIdentityParameters(command, attempt.VariantId, attempt.SourceRevisionId, attempt.RenderArtifactId);
        command.Parameters.AddWithValue("$jobId", attempt.JobId.ToString());
        command.Parameters.AddWithValue("$correctionRevisionId", attempt.CorrectionRevisionId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$remountNumber", attempt.RemountNumber);
        command.Parameters.AddWithValue("$estimatedCostUsd", attempt.EstimatedCostUsd.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$actualCostUsd", attempt.ActualCostUsd?.ToString(CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", attempt.Status);
        command.Parameters.AddWithValue("$errorCode", attempt.ErrorCode ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$errorMessage", attempt.ErrorMessage ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", attempt.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$completedAt", attempt.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CritiqueAttemptRecord?> GetAsync(
        VariantId variantId,
        RevisionId sourceRevisionId,
        ArtifactId renderArtifactId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT job_id, variant_id, source_revision_id, render_artifact_id,
                   correction_revision_id, remount_number, estimated_cost_usd,
                   actual_cost_usd, status, error_code, error_message, created_at, completed_at
            FROM critique_attempts
            WHERE variant_id = $variantId
              AND source_revision_id = $sourceRevisionId
              AND render_artifact_id = $renderArtifactId;
            """;
        AddIdentityParameters(command, variantId, sourceRevisionId, renderArtifactId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CritiqueAttemptRecord(
            new JobId(Guid.Parse(reader.GetString(0))),
            new VariantId(Guid.Parse(reader.GetString(1))),
            new RevisionId(Guid.Parse(reader.GetString(2))),
            new ArtifactId(Guid.Parse(reader.GetString(3))),
            reader.IsDBNull(4) ? null : new RevisionId(Guid.Parse(reader.GetString(4))),
            reader.GetInt32(5),
            decimal.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(12)
                ? null
                : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static void AddIdentityParameters(
        SqliteCommand command,
        VariantId variantId,
        RevisionId sourceRevisionId,
        ArtifactId renderArtifactId)
    {
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$sourceRevisionId", sourceRevisionId.ToString());
        command.Parameters.AddWithValue("$renderArtifactId", renderArtifactId.ToString());
    }
}
