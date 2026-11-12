using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Events;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>
/// Сохраняет технические события Job в SQLite.
/// </summary>
public sealed class SqliteJobEventStore : IJobEventStore
{
    private readonly string connectionString;

    /// <summary>
    /// Создаёт SQLite store событий.
    /// </summary>
    /// <param name="connectionString">Строка подключения к базе данных.</param>
    public SqliteJobEventStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task AppendAsync(
        JobEventRecord eventRecord,
        CancellationToken cancellationToken)
    {
        if (eventRecord.EventId == Guid.Empty)
        {
            throw new ArgumentException("Event ID cannot be empty.", nameof(eventRecord));
        }

        if (eventRecord.JobId.IsEmpty)
        {
            throw new ArgumentException("Job ID cannot be empty.", nameof(eventRecord));
        }

        if (string.IsNullOrWhiteSpace(eventRecord.EventType))
        {
            throw new ArgumentException("Event type cannot be empty.", nameof(eventRecord));
        }

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO events (
                event_id, job_id, variant_id, event_type, stage, payload_json, created_at)
            VALUES (
                $eventId, $jobId, $variantId, $eventType, $stage, $payloadJson, $createdAt);
            """;
        command.Parameters.AddWithValue("$eventId", eventRecord.EventId.ToString("N"));
        command.Parameters.AddWithValue("$jobId", eventRecord.JobId.ToString());
        command.Parameters.AddWithValue(
            "$variantId",
            eventRecord.VariantId?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$eventType", eventRecord.EventType);
        command.Parameters.AddWithValue(
            "$stage",
            eventRecord.Stage is null ? (object)DBNull.Value : (int)eventRecord.Stage.Value);
        command.Parameters.AddWithValue("$payloadJson", (object?)eventRecord.PayloadJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", eventRecord.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobEventRecord>> GetAsync(
        JobId jobId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, variant_id, event_type, stage, payload_json, created_at
            FROM events
            WHERE job_id = $jobId
            ORDER BY created_at, event_id;
            """;
        command.Parameters.AddWithValue("$jobId", jobId.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<JobEventRecord> events = [];

        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new JobEventRecord(
                Guid.Parse(reader.GetString(0)),
                jobId,
                reader.IsDBNull(1) ? null : new VariantId(Guid.Parse(reader.GetString(1))),
                reader.GetString(2),
                reader.IsDBNull(3)
                    ? null
                    : (ProductionStage)reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }

        return events;
    }
}
