using System.Text.Json;
using Microsoft.Extensions.Logging;
using VideoFactory.Bot.Configuration;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Jobs.Cancellation;
using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Application.TelegramIntake;

namespace VideoFactory.Bot.Telegram;

/// <summary>
/// Обрабатывает нормализованные Telegram updates и создаёт только Fast Job.
/// </summary>
public sealed class TelegramUpdateHandler : IPlanningApprovalHandler
{
    private readonly TelegramOptions telegramOptions;
    private readonly LimitsOptions limitsOptions;
    private readonly ITelegramFileDownloader downloader;
    private readonly IArtifactStore artifactStore;
    private readonly ITelegramIntakeStore intakeStore;
    private readonly ITelegramResponder responder;
    private readonly ILogger<TelegramUpdateHandler>? logger;
    private readonly PlanningApprovalUseCase? approvalUseCase;
    private readonly CancelJobUseCase? cancelJobUseCase;
    private readonly RepeatDeliveryUseCase? repeatDeliveryUseCase;
    private readonly IJobSignal? jobSignal;
    private readonly string workspaceRoot;

    /// <summary>Создаёт обработчик intake.</summary>
    public TelegramUpdateHandler(
        TelegramOptions telegramOptions,
        LimitsOptions limitsOptions,
        ITelegramFileDownloader downloader,
        IArtifactStore artifactStore,
        ITelegramIntakeStore intakeStore,
        ITelegramResponder responder,
        ILogger<TelegramUpdateHandler>? logger = null,
        PlanningApprovalUseCase? approvalUseCase = null,
        CancelJobUseCase? cancelJobUseCase = null,
        RepeatDeliveryUseCase? repeatDeliveryUseCase = null,
        IJobSignal? jobSignal = null,
        string? workspaceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(telegramOptions);
        ArgumentNullException.ThrowIfNull(limitsOptions);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(intakeStore);
        ArgumentNullException.ThrowIfNull(responder);
        this.telegramOptions = telegramOptions;
        this.limitsOptions = limitsOptions;
        this.downloader = downloader;
        this.artifactStore = artifactStore;
        this.intakeStore = intakeStore;
        this.responder = responder;
        this.logger = logger;
        this.approvalUseCase = approvalUseCase;
        this.cancelJobUseCase = cancelJobUseCase;
        this.repeatDeliveryUseCase = repeatDeliveryUseCase;
        this.jobSignal = jobSignal;
        this.workspaceRoot = Path.GetFullPath(workspaceRoot ?? Environment.CurrentDirectory);
    }

    /// <inheritdoc />
    public async Task<PlanningApprovalResult> HandleAsync(
        TelegramCallbackQuery query,
        CancellationToken cancellationToken)
    {
        if (query.UserId != telegramOptions.AllowedUserId || approvalUseCase is null)
        {
            return new PlanningApprovalResult.Forbidden();
        }

        string[] parts = query.Data.Split(':', StringSplitOptions.None);
        if (parts.Length != 4 || parts[0] != "planning-cost"
            || !Guid.TryParse(parts[1], out Guid jobGuid)
            || !Guid.TryParse(parts[2], out Guid variantGuid)
            || parts[3].Length == 0)
        {
            return new PlanningApprovalResult.Stale();
        }

        bool approved = parts[3].EndsWith("-approve", StringComparison.Ordinal);
        bool rejected = parts[3].EndsWith("-reject", StringComparison.Ordinal);
        if (!approved && !rejected)
        {
            return new PlanningApprovalResult.Stale();
        }

        PlanningApprovalDecision decision = approved
            ? PlanningApprovalDecision.Approve
            : PlanningApprovalDecision.Reject;
        string versionText = parts[3][..^(approved ? 8 : 7)];
        if (!int.TryParse(versionText, out int version))
        {
            return new PlanningApprovalResult.Stale();
        }

        PlanningApprovalResult result = await approvalUseCase.ExecuteAsync(
            new PlanningApprovalCommand(
                new JobId(jobGuid),
                new VariantId(variantGuid),
                query.UserId,
                version,
                decision),
            cancellationToken);
        string message = result switch
        {
            PlanningApprovalResult.Applied when decision == PlanningApprovalDecision.Approve => "Стоимость подтверждена. Продолжаю планирование.",
            PlanningApprovalResult.Applied => "Дополнительная стоимость отклонена. Задание остановлено.",
            PlanningApprovalResult.AlreadyResolved => "Это решение уже было обработано.",
            PlanningApprovalResult.Forbidden => "Это действие недоступно для данного пользователя.",
            _ => "Запрос подтверждения устарел или уже закрыт."
        };
        await responder.SendTextAsync(query.ChatId, message, cancellationToken);
        return result;
    }

    /// <summary>Обрабатывает одно событие Telegram.</summary>
    public async Task HandleAsync(
        TelegramUpdate update,
        CancellationToken cancellationToken)
    {
        if (update.UserId != telegramOptions.AllowedUserId)
        {
            logger?.LogWarning("Rejected Telegram update from user {UserId}", update.UserId);
            return;
        }

        if (string.Equals(update.Text?.Trim(), "/start", StringComparison.OrdinalIgnoreCase))
        {
            await responder.SendTextAsync(
                update.ChatId,
                "Отправьте до пяти видео и инструкцию, затем используйте /done.",
                cancellationToken);
            return;
        }

        if (string.Equals(update.Text?.Trim(), "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            IntakeCancellationResult result = await intakeStore.CancelAsync(
                update.UserId, update.ChatId, cancellationToken);
            if (result is IntakeCancellationResult.Cancelled cancelled)
            {
                foreach (string relativePath in cancelled.TemporaryRelativePaths)
                    DeleteIntakeFile(relativePath);
            }
            await responder.SendTextAsync(
                update.ChatId,
                result is IntakeCancellationResult.Cancelled
                    ? "Текущий набор видео отменён."
                    : "Открытого набора видео нет.",
                cancellationToken);
            return;
        }

        if (string.Equals(update.Text?.Trim(), "/cancel-job", StringComparison.OrdinalIgnoreCase))
        {
            await HandleJobCancellationAsync(update, cancellationToken);
            return;
        }

        if (update.Text?.StartsWith("/resend", StringComparison.OrdinalIgnoreCase) == true)
        {
            await HandleRepeatDeliveryAsync(update, cancellationToken);
            return;
        }

        if (update.Media is not null)
        {
            await HandleMediaAsync(update, update.Media, cancellationToken);
            return;
        }

        if (string.Equals(update.Text?.Trim(), "/done", StringComparison.OrdinalIgnoreCase))
        {
            await CompleteIntakeAsync(update, cancellationToken);
            return;
        }

        if (update.Text?.Trim().StartsWith("/style", StringComparison.OrdinalIgnoreCase) == true)
        {
            await SetStyleAsync(update, cancellationToken);
            return;
        }

        if (update.Text?.Trim().StartsWith("/duration", StringComparison.OrdinalIgnoreCase) == true)
        {
            await SetDurationAsync(update, cancellationToken);
            return;
        }

        if (!string.IsNullOrWhiteSpace(update.Text))
        {
            IntakeSessionUpdateResult result = await intakeStore.SetInstructionAsync(
                update.UserId, update.ChatId, update.Text.Trim(), cancellationToken);
            await responder.SendTextAsync(
                update.ChatId,
                result is IntakeSessionUpdateResult.Updated
                    ? "Инструкция сохранена. После загрузки всех видео используйте /done."
                    : $"Не удалось сохранить инструкцию: {((IntakeSessionUpdateResult.Rejected)result).Code}",
                cancellationToken);
            return;
        }

        await responder.SendTextAsync(
            update.ChatId,
            "Отправьте видео, инструкцию или команду /done.",
            cancellationToken);
    }

    private void DeleteIntakeFile(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return;
        string absolutePath = Path.GetFullPath(Path.Combine(
            workspaceRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relativeToRoot = Path.GetRelativePath(workspaceRoot, absolutePath);
        if (relativeToRoot.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeToRoot)) return;
        try
        {
            if (File.Exists(absolutePath)) File.Delete(absolutePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning("Failed to delete a Telegram intake file: {ErrorType}", exception.GetType().Name);
        }
    }

    private async Task CompleteIntakeAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        IntakeCompletionResult result = await intakeStore.CompleteAsync(
            new IntakeCompletion(update.UserId, update.ChatId, Math.Max(1, update.UpdateId)),
            cancellationToken);
        string message;
        switch (result)
        {
            case IntakeCompletionResult.Created created:
                if (jobSignal is not null)
                {
                    await jobSignal.PublishAsync(created.JobId, cancellationToken);
                }
                message = "Задание принято. Начинаю обработку.";
                break;
            case IntakeCompletionResult.Existing:
                message = "Эта команда уже обработана. Продолжаю существующее задание.";
                break;
            case IntakeCompletionResult.Rejected rejected:
                message = $"Не удалось завершить набор: {rejected.Code}";
                break;
            default:
                throw new InvalidOperationException("Unknown intake completion result.");
        }

        await responder.SendTextAsync(update.ChatId, message, cancellationToken);
    }

    private async Task SetStyleAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        string value = update.Text!.Trim()["/style".Length..].Trim();
        RequestedReelsStyle? style = value.ToLowerInvariant() switch
        {
            "auto" => RequestedReelsStyle.Auto,
            "expert-clean" or "expertclean" => RequestedReelsStyle.ExpertClean,
            "dynamic-viral" or "dynamicviral" => RequestedReelsStyle.DynamicViral,
            "cinematic" => RequestedReelsStyle.Cinematic,
            "premium-ads" or "premiumads" => RequestedReelsStyle.PremiumAds,
            _ => null
        };
        if (style is null)
        {
            await responder.SendTextAsync(
                update.ChatId,
                "Стиль: auto, expert-clean, dynamic-viral, cinematic или premium-ads.",
                cancellationToken);
            return;
        }

        IntakeSessionUpdateResult result = await intakeStore.SetStyleAsync(
            update.UserId, update.ChatId, style.Value, cancellationToken);
        await responder.SendTextAsync(
            update.ChatId,
            result is IntakeSessionUpdateResult.Updated
                ? "Стиль сохранён."
                : $"Не удалось сохранить стиль: {((IntakeSessionUpdateResult.Rejected)result).Code}",
            cancellationToken);
    }

    private async Task SetDurationAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        string value = update.Text!.Trim()["/duration".Length..].Trim();
        if (!int.TryParse(value, out int durationSeconds) || durationSeconds is < 5 or > 60)
        {
            await responder.SendTextAsync(
                update.ChatId,
                "Длительность должна быть целым числом от 5 до 60 секунд.",
                cancellationToken);
            return;
        }

        IntakeSessionUpdateResult result = await intakeStore.SetDurationAsync(
            update.UserId, update.ChatId, durationSeconds, cancellationToken);
        await responder.SendTextAsync(
            update.ChatId,
            result is IntakeSessionUpdateResult.Updated
                ? "Длительность сохранена."
                : $"Не удалось сохранить длительность: {((IntakeSessionUpdateResult.Rejected)result).Code}",
            cancellationToken);
    }

    private async Task HandleJobCancellationAsync(
        TelegramUpdate update,
        CancellationToken cancellationToken)
    {
        if (cancelJobUseCase is null)
        {
            await responder.SendTextAsync(update.ChatId, "Отмена заданий пока недоступна.", cancellationToken);
            return;
        }

        JobCancellationResult result = await cancelJobUseCase.ExecuteAsync(update.UserId, cancellationToken);
        string message = result switch
        {
            JobCancellationResult.Requested requested =>
                $"Отмена задания на этапе {requested.Job.Progress.Stage} запрошена. Готовый файл не будет опубликован.",
            JobCancellationResult.AlreadyRequested already =>
                $"Отмена задания на этапе {already.Job.Progress.Stage} уже запрошена.",
            JobCancellationResult.UnsupportedStage unsupported =>
                $"Задание сейчас находится на этапе {unsupported.Stage}; отмена этой командой недоступна.",
            JobCancellationResult.NoActiveJob => "Активных заданий на Analysis или Rendering нет.",
            _ => "Не удалось обработать отмену задания."
        };
        await responder.SendTextAsync(update.ChatId, message, cancellationToken);
    }

    private async Task HandleRepeatDeliveryAsync(TelegramUpdate update, CancellationToken cancellationToken)
    {
        string[] parts = update.Text!.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (repeatDeliveryUseCase is null || parts.Length != 2 || !Guid.TryParse(parts[1], out Guid jobGuid) || update.UpdateId <= 0)
        {
            await responder.SendTextAsync(update.ChatId, "Используйте команду /resend <jobId> для повторной отправки готового ролика.", cancellationToken);
            return;
        }

        RepeatDeliveryResult result = await repeatDeliveryUseCase.ExecuteAsync(
            new RepeatDeliveryCommand(new JobId(jobGuid), update.UserId, update.UpdateId), cancellationToken);
        string message = result switch
        {
            RepeatDeliveryResult.Succeeded => "Готовый ролик отправлен повторно.",
            RepeatDeliveryResult.AlreadyProcessed => "Эта команда уже была обработана.",
            RepeatDeliveryResult.Rejected rejected when rejected.Code == "delivery.forbidden" => "Это задание недоступно для повторной отправки.",
            RepeatDeliveryResult.Rejected rejected when rejected.Code is "delivery.file_missing" or "delivery.final_missing" => "Готовый файл уже удалён и недоступен.",
            RepeatDeliveryResult.Rejected rejected when rejected.Code == "delivery.file_changed" => "Готовый файл изменён и не будет отправлен.",
            RepeatDeliveryResult.Rejected rejected when rejected.Code == "delivery.job_not_completed" => "Задание ещё не завершено.",
            RepeatDeliveryResult.Rejected rejected when rejected.Code == "delivery.unknown_result" => "Предыдущая попытка доставки имеет неопределённый результат. Повторите команду с новым сообщением.",
            _ => "Не удалось повторно отправить ролик."
        };
        await responder.SendTextAsync(update.ChatId, message, cancellationToken);
    }

    private async Task HandleMediaAsync(
        TelegramUpdate update,
        TelegramMediaInput input,
        CancellationToken cancellationToken)
    {
        if (!MediaUploadPolicy.Accepts(input.MimeType, input.FileName))
        {
            await responder.SendTextAsync(update.ChatId, "Поддерживаются только видео MP4 или MOV с известным типом файла.", cancellationToken);
            return;
        }
        if (input.SizeBytes is long size && size > limitsOptions.MaxInputFileBytes)
        {
            await responder.SendTextAsync(
                update.ChatId,
                "Видео превышает допустимый размер.",
                cancellationToken);
            return;
        }

        long messageId = Math.Max(1, update.UpdateId);
        string relativePath = $"jobs/intake/{Guid.NewGuid():N}/original.bin";
        IntakeReservationResult reservation = await intakeStore.ReserveAsync(
            new IntakeReservation(
                update.UserId,
                update.ChatId,
                messageId,
                input.FileUniqueId,
                input.SizeBytes,
                limitsOptions.MaxInputFileBytes,
                relativePath + ".tmp"),
            cancellationToken);
        if (reservation is IntakeReservationResult.Duplicate)
        {
            await responder.SendTextAsync(update.ChatId, "Это видео уже добавлено.", cancellationToken);
            return;
        }
        if (reservation is IntakeReservationResult.Rejected reservationRejected)
        {
            await responder.SendTextAsync(
                update.ChatId,
                $"Не удалось зарезервировать видео: {reservationRejected.Code}",
                cancellationToken);
            return;
        }

        IntakeReservationResult.Reserved reserved = (IntakeReservationResult.Reserved)reservation;
        TelegramDownloadResult result = await downloader.DownloadAsync(
            input,
            relativePath,
            reserved.ReservedBytes,
            (requiredBytes, token) => requiredBytes > limitsOptions.MaxInputFileBytes
                ? Task.FromResult<long?>(null)
                : intakeStore.TryExpandReservationAsync(
                    reserved.SessionId, messageId, requiredBytes, token),
            cancellationToken);
        if (result is TelegramDownloadResult.Rejected failure)
        {
            await intakeStore.FailAsync(reserved.SessionId, messageId, failure.Code, cancellationToken);
            logger?.LogWarning("Telegram download rejected: {ErrorCode}", failure.Code);
            await responder.SendTextAsync(update.ChatId, "Не удалось принять видео: проверьте формат и размер или повторите позже.", cancellationToken);
            return;
        }
        TelegramDownloadedFile downloaded = ((TelegramDownloadResult.Succeeded)result).File;
        if (downloaded.SizeBytes > limitsOptions.MaxInputFileBytes)
        {
            await intakeStore.FailAsync(
                reserved.SessionId,
                messageId,
                "intake.file_limit_exceeded",
                cancellationToken);
            DeleteIntakeFile(downloaded.RelativePath);
            await responder.SendTextAsync(
                update.ChatId,
                "Фактический размер скачанного видео превышает допустимый лимит.",
                cancellationToken);
            return;
        }

        ArtifactId artifactId = ArtifactId.New();
        ArtifactRecord record = new(
            artifactId,
            "OriginalVideo",
            downloaded.RelativePath,
            downloaded.Sha256,
            downloaded.SizeBytes,
            JsonSerializer.Serialize(new
            {
                telegram = new
                {
                    fileId = input.FileId,
                    fileUniqueId = input.FileUniqueId
                }
            }),
            null,
            "telegram-intake/1");

        ArtifactRegistrationResult registration = await artifactStore.RegisterAsync(
            record,
            cancellationToken);
        if (registration is ArtifactRegistrationResult.Rejected)
        {
            await intakeStore.FailAsync(
                reserved.SessionId,
                messageId,
                "intake.artifact_registration_failed",
                cancellationToken);
            DeleteIntakeFile(downloaded.RelativePath);
            logger?.LogWarning(
                "Telegram Artifact registration rejected for user {UserId}",
                update.UserId);
            await responder.SendTextAsync(
                update.ChatId,
                "Не удалось сохранить видео. Попробуйте ещё раз.",
                cancellationToken);
            return;
        }

        ArtifactRecord stored = registration switch
        {
            ArtifactRegistrationResult.Created created => created.Record,
            ArtifactRegistrationResult.Existing existing => existing.Record,
            _ => throw new InvalidOperationException("Unknown Artifact registration result.")
        };
        IntakeAcceptanceResult acceptance = await intakeStore.AcceptAsync(
            new IntakeAcceptance(
                reserved.SessionId,
                messageId,
                stored.ArtifactId,
                downloaded.Sha256,
                downloaded.SizeBytes,
                downloaded.DurationMicroseconds),
            cancellationToken);
        if (acceptance is IntakeAcceptanceResult.Rejected)
            DeleteIntakeFile(downloaded.RelativePath);
        await responder.SendTextAsync(
            update.ChatId,
            acceptance is IntakeAcceptanceResult.Accepted
                ? "Видео получено. Добавьте ещё видео или инструкцию, затем используйте /done."
                : $"Не удалось добавить видео: {((IntakeAcceptanceResult.Rejected)acceptance).Code}",
            cancellationToken);
    }
}
