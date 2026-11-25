using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>
/// Сохраняет задания производства в SQLite.
/// </summary>
public sealed class SqliteJobCreationStore : IJobCreationStore
{
    private readonly string connectionString;

    /// <summary>
    /// Создаёт SQLite store для указанной базы данных.
    /// </summary>
    /// <param name="connectionString">Строка подключения к базе данных.</param>
    public SqliteJobCreationStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        }

        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<JobCreationStoreResult> CreateAsync(
        JobCreationRecord record,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        JobCreationStoreResult result = await SqliteJobWriter.CreateAsync(
            connection,
            transaction,
            record,
            cancellationToken);
        if (result is not JobCreationStoreResult.Rejected)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }
}
