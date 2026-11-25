using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.TelegramIntake;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Infrastructure.Storage;

/// <summary>Сохраняет Telegram intake sessions и reservations в SQLite.</summary>
public sealed class SqliteTelegramIntakeStore : ITelegramIntakeStore
{
    private const long MaximumSessionBytes = 524_288_000;
    private const long MaximumSessionDurationMicroseconds = 600_000_000;
    private const int MaximumItems = 5;
    private readonly string connectionString;

    /// <summary>Создаёт SQLite store для указанной базы данных.</summary>
    /// <param name="connectionString">Непустая строка подключения SQLite.</param>
    public SqliteTelegramIntakeStore(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("SQLite connection string cannot be empty.", nameof(connectionString));
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task<IntakeReservationResult> ReserveAsync(
        IntakeReservation request,
        CancellationToken cancellationToken)
    {
        if (request.UserId <= 0 || request.ChatId == 0 || request.MessageId <= 0
            || request.MaximumFileBytes <= 0 || string.IsNullOrWhiteSpace(request.TemporaryRelativePath)
            || request.DeclaredSizeBytes is <= 0)
        {
            return new IntakeReservationResult.Rejected("intake.reservation_invalid", "Media reservation is invalid.");
        }

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        (Guid SessionId, bool Created) session = await GetOrCreateSessionAsync(
            connection, transaction, request.UserId, request.ChatId, cancellationToken);
        IntakeReservationResult.Duplicate? duplicate = await FindDuplicateAsync(
            connection, transaction, session.SessionId, request, cancellationToken);
        if (duplicate is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return duplicate;
        }

        (long Count, long ReservedBytes) totals = await ReadReservationTotalsAsync(
            connection, transaction, session.SessionId, cancellationToken);
        if (totals.Count >= MaximumItems)
            return new IntakeReservationResult.Rejected("intake.item_limit_exceeded", "The intake session already contains five videos.");

        long remaining = MaximumSessionBytes - totals.ReservedBytes;
        long reserved = request.DeclaredSizeBytes ?? Math.Min(request.MaximumFileBytes, remaining);
        if (reserved > request.MaximumFileBytes || reserved > remaining)
            return new IntakeReservationResult.Rejected("intake.byte_limit_exceeded", "The intake byte limit would be exceeded.");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO telegram_intake_items (
                session_id, chat_id, message_id, file_unique_id, status,
                temporary_relative_path, reserved_bytes, created_at, updated_at)
            VALUES (
                $sessionId, $chatId, $messageId, $fileUniqueId, 0,
                $temporaryRelativePath, $reservedBytes, $createdAt, $updatedAt);
            """;
        insert.Parameters.AddWithValue("$sessionId", session.SessionId.ToString("N"));
        insert.Parameters.AddWithValue("$chatId", request.ChatId);
        insert.Parameters.AddWithValue("$messageId", request.MessageId);
        insert.Parameters.AddWithValue("$fileUniqueId", request.FileUniqueId ?? (object)DBNull.Value);
        insert.Parameters.AddWithValue("$temporaryRelativePath", request.TemporaryRelativePath);
        insert.Parameters.AddWithValue("$reservedBytes", reserved);
        insert.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        insert.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IntakeReservationResult.Reserved(session.SessionId, reserved);
    }

    /// <inheritdoc />
    public async Task<IntakeAcceptanceResult> AcceptAsync(
        IntakeAcceptance request,
        CancellationToken cancellationToken)
    {
        if (request.SessionId == Guid.Empty || request.MessageId <= 0 || request.ArtifactId.IsEmpty
            || string.IsNullOrWhiteSpace(request.Sha256) || request.SizeBytes <= 0 || request.DurationMicroseconds <= 0)
            return new IntakeAcceptanceResult.Rejected("intake.acceptance_invalid", "Media acceptance is invalid.");

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        (long Bytes, long Duration) totals = await ReadAcceptedTotalsAsync(
            connection, transaction, request.SessionId, cancellationToken);
        if (totals.Bytes + request.SizeBytes > MaximumSessionBytes)
        {
            await FailPendingAsync(connection, transaction, request, "intake.byte_limit_exceeded", cancellationToken);
            await DeleteUnownedArtifactAsync(connection, transaction, request.ArtifactId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IntakeAcceptanceResult.Rejected("intake.byte_limit_exceeded", "The intake byte limit would be exceeded.");
        }
        if (totals.Duration + request.DurationMicroseconds > MaximumSessionDurationMicroseconds)
        {
            await FailPendingAsync(connection, transaction, request, "intake.duration_limit_exceeded", cancellationToken);
            await DeleteUnownedArtifactAsync(connection, transaction, request.ArtifactId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IntakeAcceptanceResult.Rejected("intake.duration_limit_exceeded", "The intake duration limit would be exceeded.");
        }

        if (await HasAcceptedShaAsync(connection, transaction, request, cancellationToken))
        {
            await using SqliteCommand failDuplicate = connection.CreateCommand();
            failDuplicate.Transaction = transaction;
            failDuplicate.CommandText = """
                UPDATE telegram_intake_items
                SET status = 2, error_code = 'intake.duplicate_content', updated_at = $updatedAt
                WHERE session_id = $sessionId AND message_id = $messageId AND status = 0;
                """;
            failDuplicate.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            failDuplicate.Parameters.AddWithValue("$sessionId", request.SessionId.ToString("N"));
            failDuplicate.Parameters.AddWithValue("$messageId", request.MessageId);
            await failDuplicate.ExecuteNonQueryAsync(cancellationToken);
            await DeleteUnownedArtifactAsync(connection, transaction, request.ArtifactId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IntakeAcceptanceResult.Rejected("intake.duplicate_content", "The same media content is already accepted.");
        }

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE telegram_intake_items
            SET status = 1, artifact_id = $artifactId, sha256 = $sha256,
                actual_size_bytes = $actualSizeBytes, duration_microseconds = $durationMicroseconds,
                updated_at = $updatedAt
            WHERE session_id = $sessionId AND message_id = $messageId
              AND status = 0 AND reserved_bytes >= $actualSizeBytes;
            """;
        update.Parameters.AddWithValue("$artifactId", request.ArtifactId.ToString());
        update.Parameters.AddWithValue("$sha256", request.Sha256);
        update.Parameters.AddWithValue("$actualSizeBytes", request.SizeBytes);
        update.Parameters.AddWithValue("$durationMicroseconds", request.DurationMicroseconds);
        update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$sessionId", request.SessionId.ToString("N"));
        update.Parameters.AddWithValue("$messageId", request.MessageId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await DeleteUnownedArtifactAsync(connection, transaction, request.ArtifactId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IntakeAcceptanceResult.Rejected("intake.pending_item_not_found", "Pending media reservation was not found.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new IntakeAcceptanceResult.Accepted();
    }

    private static async Task DeleteUnownedArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ArtifactId artifactId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM artifacts WHERE artifact_id = $artifactId AND job_id IS NULL;";
        delete.Parameters.AddWithValue("$artifactId", artifactId.ToString());
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task FailAsync(
        Guid sessionId,
        long messageId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        if (string.IsNullOrWhiteSpace(errorCode)) throw new ArgumentException("Error code cannot be empty.", nameof(errorCode));

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE telegram_intake_items
            SET status = 2, error_code = $errorCode, updated_at = $updatedAt
            WHERE session_id = $sessionId AND message_id = $messageId AND status = 0;
            """;
        command.Parameters.AddWithValue("$errorCode", errorCode);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("N"));
        command.Parameters.AddWithValue("$messageId", messageId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long?> TryExpandReservationAsync(
        Guid sessionId,
        long messageId,
        long requiredBytes,
        CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID cannot be empty.", nameof(sessionId));
        if (messageId <= 0) throw new ArgumentOutOfRangeException(nameof(messageId));
        if (requiredBytes <= 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        long? current;
        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT reserved_bytes FROM telegram_intake_items
                WHERE session_id = $sessionId AND message_id = $messageId AND status = 0;
                """;
            select.Parameters.AddWithValue("$sessionId", sessionId.ToString("N"));
            select.Parameters.AddWithValue("$messageId", messageId);
            object? value = await select.ExecuteScalarAsync(cancellationToken);
            current = value is null or DBNull ? null : Convert.ToInt64(value);
        }

        if (current is null) return null;
        if (requiredBytes <= current.Value)
        {
            await transaction.CommitAsync(cancellationToken);
            return current;
        }

        long otherBytes;
        await using (SqliteCommand totals = connection.CreateCommand())
        {
            totals.Transaction = transaction;
            totals.CommandText = """
                SELECT COALESCE(SUM(CASE WHEN status = 1 THEN actual_size_bytes ELSE reserved_bytes END), 0)
                FROM telegram_intake_items
                WHERE session_id = $sessionId AND status IN (0, 1) AND message_id <> $messageId;
                """;
            totals.Parameters.AddWithValue("$sessionId", sessionId.ToString("N"));
            totals.Parameters.AddWithValue("$messageId", messageId);
            otherBytes = Convert.ToInt64(await totals.ExecuteScalarAsync(cancellationToken));
        }

        if (requiredBytes > MaximumSessionBytes - otherBytes) return null;

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE telegram_intake_items SET reserved_bytes = $requiredBytes, updated_at = $updatedAt
            WHERE session_id = $sessionId AND message_id = $messageId AND status = 0;
            """;
        update.Parameters.AddWithValue("$requiredBytes", requiredBytes);
        update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$sessionId", sessionId.ToString("N"));
        update.Parameters.AddWithValue("$messageId", messageId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return requiredBytes;
    }

    /// <inheritdoc />
    public Task<IntakeSessionUpdateResult> SetInstructionAsync(
        long userId,
        long chatId,
        string instruction,
        CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(instruction)
            ? Task.FromResult<IntakeSessionUpdateResult>(
                new IntakeSessionUpdateResult.Rejected("intake.instruction_empty", "Instruction is required."))
            : UpdateSessionAsync(
                userId, chatId, "instruction", instruction.Trim(), cancellationToken);

    /// <inheritdoc />
    public Task<IntakeSessionUpdateResult> SetStyleAsync(
        long userId,
        long chatId,
        RequestedReelsStyle style,
        CancellationToken cancellationToken) =>
        !Enum.IsDefined(style)
            ? Task.FromResult<IntakeSessionUpdateResult>(
                new IntakeSessionUpdateResult.Rejected("intake.style_invalid", "Requested style is invalid."))
            : UpdateSessionAsync(
                userId, chatId, "requested_style", (int)style, cancellationToken);

    /// <inheritdoc />
    public Task<IntakeSessionUpdateResult> SetDurationAsync(
        long userId,
        long chatId,
        int durationSeconds,
        CancellationToken cancellationToken) =>
        durationSeconds is < 5 or > 60
            ? Task.FromResult<IntakeSessionUpdateResult>(
                new IntakeSessionUpdateResult.Rejected("intake.duration_invalid", "Duration must be from 5 to 60 seconds."))
            : UpdateSessionAsync(
                userId, chatId, "target_duration_seconds", durationSeconds, cancellationToken);

    /// <inheritdoc />
    public async Task<PendingRecoveryResult> RecoverPendingAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        ImmutableArray<string>.Builder paths = ImmutableArray.CreateBuilder<string>();
        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT temporary_relative_path FROM telegram_intake_items WHERE status = 0;";
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) paths.Add(reader.GetString(0));
        }

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE telegram_intake_items
            SET status = 2, error_code = 'intake.interrupted', updated_at = $updatedAt
            WHERE status = 0;
            """;
        update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PendingRecoveryResult(paths.ToImmutable());
    }

    /// <inheritdoc />
    public async Task<IntakeCompletionResult> CompleteAsync(
        IntakeCompletion completion,
        CancellationToken cancellationToken)
    {
        if (completion.UserId <= 0 || completion.ChatId == 0 || completion.DoneMessageId <= 0)
            return new IntakeCompletionResult.Rejected("intake.completion_invalid", "Completion identity is invalid.");

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        JobCreationRecord? receipt = await ReadCompletionReceiptAsync(
            connection, transaction, completion.ChatId, completion.DoneMessageId, cancellationToken);
        if (receipt is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new IntakeCompletionResult.Existing(receipt.JobId, receipt.VariantId);
        }

        (Guid SessionId, string? Instruction, RequestedReelsStyle Style, int? Duration)? session =
            await ReadOpenSessionHeaderAsync(
                connection, transaction, completion.UserId, completion.ChatId, cancellationToken);
        if (session is null)
            return new IntakeCompletionResult.Rejected("intake.session_missing", "No open intake session was found.");

        List<ArtifactId> sourceIds = [];
        long totalDuration = 0;
        long pendingCount;
        await using (SqliteCommand items = connection.CreateCommand())
        {
            items.Transaction = transaction;
            items.CommandText = """
                SELECT status, artifact_id, duration_microseconds
                FROM telegram_intake_items
                WHERE session_id = $sessionId ORDER BY message_id;
                """;
            items.Parameters.AddWithValue("$sessionId", session.Value.SessionId.ToString("N"));
            await using SqliteDataReader reader = await items.ExecuteReaderAsync(cancellationToken);
            pendingCount = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                TelegramIntakeItemStatus status = (TelegramIntakeItemStatus)reader.GetInt64(0);
                if (status == TelegramIntakeItemStatus.Pending) pendingCount++;
                if (status == TelegramIntakeItemStatus.Accepted)
                {
                    sourceIds.Add(new ArtifactId(Guid.Parse(reader.GetString(1))));
                    totalDuration += reader.GetInt64(2);
                }
            }
        }

        if (pendingCount > 0)
            return new IntakeCompletionResult.Rejected("intake.uploads_pending", "Media uploads are still pending.");
        if (sourceIds.Count == 0)
            return new IntakeCompletionResult.Rejected("intake.videos_missing", "At least one accepted video is required.");
        if (string.IsNullOrWhiteSpace(session.Value.Instruction))
            return new IntakeCompletionResult.Rejected("intake.instruction_missing", "Instruction is required.");
        if (session.Value.Duration is int duration && duration * 1_000_000L > totalDuration)
            return new IntakeCompletionResult.Rejected("intake.duration_unavailable", "Target duration exceeds source duration.");

        JobCreationRecord proposed = new(
            JobId.New(),
            VariantId.New(),
            new JobRequest(
                sourceIds,
                session.Value.Instruction,
                ProductionMode.Fast,
                new ProductionSettings(
                    session.Value.Duration,
                    captionsEnabled: true,
                    musicEnabled: false,
                    EditTempo.Dynamic,
                    session.Value.Style)),
            JobProgress.Create(ProductionMode.Fast),
            null)
        {
            TelegramUserId = completion.UserId
        };
        JobCreationStoreResult created = await SqliteJobWriter.CreateAsync(
            connection, transaction, proposed, cancellationToken);
        if (created is JobCreationStoreResult.Rejected rejected)
            return new IntakeCompletionResult.Rejected(rejected.Error.Code.ToString(), "Job creation was rejected.");

        JobCreationRecord record = created switch
        {
            JobCreationStoreResult.Created value => value.Record,
            JobCreationStoreResult.Existing value => value.Record,
            _ => throw new InvalidOperationException("Unknown Job creation result.")
        };
        string now = DateTimeOffset.UtcNow.ToString("O");
        await using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE telegram_intake_sessions
                SET status = 1, job_id = $jobId, updated_at = $updatedAt
                WHERE session_id = $sessionId AND status = 0;
                INSERT INTO telegram_intake_completion_receipts (
                    chat_id, done_message_id, session_id, job_id, created_at)
                VALUES ($chatId, $doneMessageId, $sessionId, $jobId, $createdAt);
                """;
            update.Parameters.AddWithValue("$jobId", record.JobId.ToString());
            update.Parameters.AddWithValue("$updatedAt", now);
            update.Parameters.AddWithValue("$sessionId", session.Value.SessionId.ToString("N"));
            update.Parameters.AddWithValue("$chatId", completion.ChatId);
            update.Parameters.AddWithValue("$doneMessageId", completion.DoneMessageId);
            update.Parameters.AddWithValue("$createdAt", now);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return created is JobCreationStoreResult.Created
            ? new IntakeCompletionResult.Created(record.JobId, record.VariantId)
            : new IntakeCompletionResult.Existing(record.JobId, record.VariantId);
    }

    /// <inheritdoc />
    public async Task<IntakeCancellationResult> CancelAsync(
        long userId,
        long chatId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        (Guid SessionId, string? Instruction, RequestedReelsStyle Style, int? Duration)? session =
            await ReadOpenSessionHeaderAsync(connection, transaction, userId, chatId, cancellationToken);
        if (session is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new IntakeCancellationResult.AlreadyClosed([]);
        }

        ImmutableArray<string>.Builder paths = ImmutableArray.CreateBuilder<string>();
        List<string> artifactIds = [];
        await using (SqliteCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT CASE WHEN i.status = 1 THEN a.relative_path ELSE i.temporary_relative_path END,
                       i.artifact_id
                FROM telegram_intake_items AS i
                LEFT JOIN artifacts AS a ON a.artifact_id = i.artifact_id
                WHERE i.session_id = $sessionId AND i.status IN (0, 1);
                """;
            select.Parameters.AddWithValue("$sessionId", session.Value.SessionId.ToString("N"));
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0)) paths.Add(reader.GetString(0));
                if (!reader.IsDBNull(1)) artifactIds.Add(reader.GetString(1));
            }
        }

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE telegram_intake_items
            SET status = 2, artifact_id = NULL, error_code = 'intake.cancelled', updated_at = $updatedAt
            WHERE session_id = $sessionId AND status IN (0, 1);
            UPDATE telegram_intake_sessions
            SET status = 2, updated_at = $updatedAt
            WHERE session_id = $sessionId AND status = 0;
            """;
        update.Parameters.AddWithValue("$sessionId", session.Value.SessionId.ToString("N"));
        update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);
        foreach (string artifactId in artifactIds)
        {
            await using SqliteCommand deleteArtifact = connection.CreateCommand();
            deleteArtifact.Transaction = transaction;
            deleteArtifact.CommandText = "DELETE FROM artifacts WHERE artifact_id = $artifactId AND job_id IS NULL;";
            deleteArtifact.Parameters.AddWithValue("$artifactId", artifactId);
            await deleteArtifact.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new IntakeCancellationResult.Cancelled(paths.ToImmutable());
    }

    /// <inheritdoc />
    public async Task<TelegramIntakeSession?> GetOpenAsync(
        long userId,
        long chatId,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadOpenSessionAsync(connection, userId, chatId, cancellationToken);
    }

    private static async Task<(Guid SessionId, bool Created)> GetOrCreateSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long userId,
        long chatId,
        CancellationToken cancellationToken)
    {
        await using (SqliteCommand find = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT session_id FROM telegram_intake_sessions WHERE user_id = $userId AND status = 0;";
            find.Parameters.AddWithValue("$userId", userId);
            object? existing = await find.ExecuteScalarAsync(cancellationToken);
            if (existing is string sessionId)
                return (Guid.Parse(sessionId), false);
        }

        Guid created = Guid.NewGuid();
        string now = DateTimeOffset.UtcNow.ToString("O");
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO telegram_intake_sessions (
                session_id, completion_identity, user_id, chat_id, status,
                requested_style, created_at, updated_at)
            VALUES ($sessionId, $completionIdentity, $userId, $chatId, 0, 0, $createdAt, $updatedAt);
            """;
        insert.Parameters.AddWithValue("$sessionId", created.ToString("N"));
        insert.Parameters.AddWithValue("$completionIdentity", created.ToString("N"));
        insert.Parameters.AddWithValue("$userId", userId);
        insert.Parameters.AddWithValue("$chatId", chatId);
        insert.Parameters.AddWithValue("$createdAt", now);
        insert.Parameters.AddWithValue("$updatedAt", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return (created, true);
    }

    private static async Task<JobCreationRecord?> ReadCompletionReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long chatId,
        long doneMessageId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT job_id FROM telegram_intake_completion_receipts
            WHERE chat_id = $chatId AND done_message_id = $doneMessageId;
            """;
        command.Parameters.AddWithValue("$chatId", chatId);
        command.Parameters.AddWithValue("$doneMessageId", doneMessageId);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string jobId
            ? await SqliteJobWriter.ReadByJobIdAsync(
                connection, transaction, new JobId(Guid.Parse(jobId)), cancellationToken)
            : null;
    }

    private static async Task<(Guid SessionId, string? Instruction, RequestedReelsStyle Style, int? Duration)?>
        ReadOpenSessionHeaderAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long userId,
            long chatId,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id, instruction, requested_style, target_duration_seconds
            FROM telegram_intake_sessions
            WHERE user_id = $userId AND chat_id = $chatId AND status = 0;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$chatId", chatId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return (
            Guid.Parse(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            (RequestedReelsStyle)reader.GetInt64(2),
            reader.IsDBNull(3) ? null : checked((int)reader.GetInt64(3)));
    }

    private async Task<IntakeSessionUpdateResult> UpdateSessionAsync(
        long userId,
        long chatId,
        string column,
        object value,
        CancellationToken cancellationToken)
    {
        if (userId <= 0 || chatId == 0)
            return new IntakeSessionUpdateResult.Rejected("intake.identity_invalid", "Telegram identity is invalid.");

        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        (Guid SessionId, _) = await GetOrCreateSessionAsync(
            connection, transaction, userId, chatId, cancellationToken);
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE telegram_intake_sessions SET {column} = $value, updated_at = $updatedAt WHERE session_id = $sessionId AND status = 0;";
        update.Parameters.AddWithValue("$value", value);
        update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$sessionId", SessionId.ToString("N"));
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        TelegramIntakeSession session = (await GetOpenAsync(userId, chatId, cancellationToken))!;
        return new IntakeSessionUpdateResult.Updated(session);
    }

    private static async Task<IntakeReservationResult.Duplicate?> FindDuplicateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        IntakeReservation request,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT message_id FROM telegram_intake_items
            WHERE (chat_id = $chatId AND message_id = $messageId)
               OR (session_id = $sessionId AND file_unique_id = $fileUniqueId AND status IN (0, 1))
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$chatId", request.ChatId);
        command.Parameters.AddWithValue("$messageId", request.MessageId);
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("N"));
        command.Parameters.AddWithValue("$fileUniqueId", request.FileUniqueId ?? (object)DBNull.Value);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : new(sessionId, Convert.ToInt64(value));
    }

    private static async Task<(long Count, long ReservedBytes)> ReadReservationTotalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(CASE WHEN status = 1 THEN actual_size_bytes ELSE reserved_bytes END), 0)
            FROM telegram_intake_items WHERE session_id = $sessionId AND status IN (0, 1);
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<(long Bytes, long Duration)> ReadAcceptedTotalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(SUM(actual_size_bytes), 0), COALESCE(SUM(duration_microseconds), 0)
            FROM telegram_intake_items WHERE session_id = $sessionId AND status = 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("N"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<bool> HasAcceptedShaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IntakeAcceptance request,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM telegram_intake_items
                WHERE session_id = $sessionId AND status = 1 AND sha256 = $sha256);
            """;
        command.Parameters.AddWithValue("$sessionId", request.SessionId.ToString("N"));
        command.Parameters.AddWithValue("$sha256", request.Sha256);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static async Task FailPendingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IntakeAcceptance request,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE telegram_intake_items
            SET status = 2, error_code = $errorCode, updated_at = $updatedAt
            WHERE session_id = $sessionId AND message_id = $messageId AND status = 0;
            """;
        command.Parameters.AddWithValue("$errorCode", errorCode);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$sessionId", request.SessionId.ToString("N"));
        command.Parameters.AddWithValue("$messageId", request.MessageId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<TelegramIntakeSession?> ReadOpenSessionAsync(
        SqliteConnection connection,
        long userId,
        long chatId,
        CancellationToken cancellationToken)
    {
        Guid sessionId;
        string? instruction;
        RequestedReelsStyle style;
        int? duration;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT session_id, instruction, requested_style, target_duration_seconds
                FROM telegram_intake_sessions
                WHERE user_id = $userId AND chat_id = $chatId AND status = 0;
                """;
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$chatId", chatId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            sessionId = Guid.Parse(reader.GetString(0));
            instruction = reader.IsDBNull(1) ? null : reader.GetString(1);
            style = (RequestedReelsStyle)reader.GetInt64(2);
            duration = reader.IsDBNull(3) ? null : checked((int)reader.GetInt64(3));
        }

        ImmutableArray<TelegramIntakeItem>.Builder items = ImmutableArray.CreateBuilder<TelegramIntakeItem>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT message_id, file_unique_id, status, artifact_id, reserved_bytes,
                       actual_size_bytes, duration_microseconds, temporary_relative_path
                FROM telegram_intake_items
                WHERE session_id = $sessionId ORDER BY message_id;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId.ToString("N"));
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new TelegramIntakeItem(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    (TelegramIntakeItemStatus)reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : new ArtifactId(Guid.Parse(reader.GetString(3))),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.GetString(7)));
            }
        }

        return new TelegramIntakeSession(
            sessionId, userId, chatId, TelegramIntakeSessionStatus.Open,
            instruction, style, duration, items.ToImmutable(), null);
    }
}
