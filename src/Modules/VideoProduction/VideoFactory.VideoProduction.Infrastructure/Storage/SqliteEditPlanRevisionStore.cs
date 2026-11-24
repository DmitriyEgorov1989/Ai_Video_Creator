using System.Text.Json;
using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>
/// Сохраняет неизменяемые ревизии монтажных планов в SQLite.
/// </summary>
public sealed class SqliteEditPlanRevisionStore : IEditPlanRevisionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;

    /// <summary>Создаёт SQLite store ревизий.</summary>
    /// <param name="connectionString">Строка подключения к базе данных.</param>
    public SqliteEditPlanRevisionStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<EditPlanRevision?> FindApprovedAsync(
        JobId jobId,
        VariantId variantId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE revision_id = (" +
            "SELECT current_approved_edit_plan_revision_id FROM jobs " +
            "WHERE job_id = $jobId) AND job_id = $jobId AND variant_id = $variantId;";
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadAsync(reader, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EditPlanRevision?> GetAsync(
        RevisionId revisionId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE revision_id = $revisionId;";
        command.Parameters.AddWithValue("$revisionId", revisionId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadAsync(reader, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EditPlanRevision?> FindAsync(
        JobId jobId,
        VariantId variantId,
        string planHash,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectSql +
            " WHERE job_id = $jobId AND variant_id = $variantId AND plan_hash = $planHash;";
        AddIdentityParameters(command, jobId, variantId, planHash);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadAsync(reader, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<EditPlanRevision> SaveAsync(
        EditPlan plan,
        string planHash,
        decimal actualCostUsd,
        CancellationToken cancellationToken) =>
        await SaveAsync(plan, planHash, actualCostUsd, null, cancellationToken);

    /// <inheritdoc />
    public async Task<EditPlanRevision> SaveAsync(
        EditPlan plan,
        string planHash,
        decimal actualCostUsd,
        ReelsPresentationPlan? presentation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(planHash))
        {
            throw new ArgumentException("Plan hash cannot be empty.", nameof(planHash));
        }

        EditPlanRevision revision = new(
            plan.RevisionId,
            plan.JobId,
            plan.VariantId,
            planHash,
            plan,
            actualCostUsd,
            presentation);
        string planJson = presentation is null
            ? JsonSerializer.Serialize(plan, JsonOptions)
            : JsonSerializer.Serialize(new StoredPlanDocument(plan, presentation), JsonOptions);

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO edit_plan_revisions (
                revision_id, job_id, variant_id, analysis_revision_id,
                plan_hash, plan_json, actual_cost_usd, created_at)
            VALUES (
                $revisionId, $jobId, $variantId, $analysisRevisionId,
                $planHash, $planJson, $actualCostUsd, $createdAt);
            """;
        command.Parameters.AddWithValue("$revisionId", plan.RevisionId.ToString());
        command.Parameters.AddWithValue("$jobId", plan.JobId.ToString());
        command.Parameters.AddWithValue("$variantId", plan.VariantId.ToString());
        command.Parameters.AddWithValue("$analysisRevisionId", plan.AnalysisRevisionId.ToString());
        command.Parameters.AddWithValue("$planHash", planHash);
        command.Parameters.AddWithValue("$planJson", planJson);
        command.Parameters.AddWithValue("$actualCostUsd", actualCostUsd.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using SqliteCommand approvalCommand = connection.CreateCommand();
            approvalCommand.Transaction = transaction;
            approvalCommand.CommandText = """
                UPDATE jobs
                SET current_approved_edit_plan_revision_id = $revisionId, updated_at = $updatedAt
                WHERE job_id = $jobId;
                UPDATE variants
                SET current_approved_edit_plan_revision_id = $revisionId, updated_at = $updatedAt
                WHERE variant_id = $variantId;
                """;
            approvalCommand.Parameters.AddWithValue("$revisionId", plan.RevisionId.ToString());
            approvalCommand.Parameters.AddWithValue("$jobId", plan.JobId.ToString());
            approvalCommand.Parameters.AddWithValue("$variantId", plan.VariantId.ToString());
            approvalCommand.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await approvalCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return revision;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(cancellationToken);
            EditPlanRevision? existing = await FindAsync(
                plan.JobId,
                plan.VariantId,
                planHash,
                cancellationToken);
            if (existing is not null)
            {
                return existing;
            }

            throw;
        }
    }

    private static void AddIdentityParameters(
        SqliteCommand command,
        JobId jobId,
        VariantId variantId,
        string planHash)
    {
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        command.Parameters.AddWithValue("$variantId", variantId.ToString());
        command.Parameters.AddWithValue("$planHash", planHash);
    }

    private static async Task<EditPlanRevision?> ReadAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string planJson = reader.GetString(5);
        using JsonDocument document = JsonDocument.Parse(planJson);
        StoredPlanDocument? stored = document.RootElement.TryGetProperty("plan", out _)
            ? JsonSerializer.Deserialize<StoredPlanDocument>(planJson, JsonOptions)
            : null;
        EditPlan plan = stored?.Plan
            ?? JsonSerializer.Deserialize<EditPlan>(planJson, JsonOptions)
            ?? throw new InvalidOperationException("Stored edit plan JSON is null.");
        return new EditPlanRevision(
            new RevisionId(Guid.Parse(reader.GetString(0))),
            new JobId(Guid.Parse(reader.GetString(1))),
            new VariantId(Guid.Parse(reader.GetString(2))),
            reader.GetString(4),
            plan,
            decimal.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture),
            stored?.Presentation);
    }

    private const string SelectSql = """
        SELECT revision_id, job_id, variant_id, analysis_revision_id,
               plan_hash, plan_json, actual_cost_usd
        FROM edit_plan_revisions
        """;

    private sealed record StoredPlanDocument(EditPlan Plan, ReelsPresentationPlan Presentation);
}
