using System.Text.Json;
using Microsoft.Extensions.Logging;
using VideoFactory.Bot.Configuration;
using VideoFactory.VideoProduction.Application.Jobs.CreateJob;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Application.Jobs.Cancellation;

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
    private readonly ICreateJobUseCase createJob;
    private readonly ITelegramIntakeSessionStore sessions;
    private readonly ITelegramResponder responder;
    private readonly ILogger<TelegramUpdateHandler>? logger;
    private readonly PlanningApprovalUseCase? approvalUseCase;
    private readonly CancelJobUseCase? cancelJobUseCase;

    /// <summary>Создаёт обработчик intake.</summary>
    public TelegramUpdateHandler(
        TelegramOptions telegramOptions,
        LimitsOptions limitsOptions,
        ITelegramFileDownloader downloader,
        IArtifactStore artifactStore,
        ICreateJobUseCase createJob,
        ITelegramIntakeSessionStore sessions,
        ITelegramResponder responder,
        ILogger<TelegramUpdateHandler>? logger = null,
        PlanningApprovalUseCase? approvalUseCase = null,
        CancelJobUseCase? cancelJobUseCase = null)
    {
        ArgumentNullException.ThrowIfNull(telegramOptions);
        ArgumentNullException.ThrowIfNull(limitsOptions);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(artifactStore);
        ArgumentNullException.ThrowIfNull(createJob);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(responder);
        this.telegramOptions = telegramOptions;
        this.limitsOptions = limitsOptions;
        this.downloader = downloader;
        this.artifactStore = artifactStore;
        this.createJob = createJob;
        this.sessions = sessions;
        this.responder = responder;
        this.logger = logger;
        this.approvalUseCase = approvalUseCase;
        this.cancelJobUseCase = cancelJobUseCase;
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
                "Отправьте видео, затем отправьте непустой комментарий для режима Fast.",
                cancellationToken);
            return;
        }

        if (string.Equals(update.Text?.Trim(), "/cancel", StringComparison.OrdinalIgnoreCase))
        {
            sessions.Remove(update.UserId);
            await responder.SendTextAsync(update.ChatId, "Текущий диалог отменён.", cancellationToken);
            return;
        }

        if (string.Equals(update.Text?.Trim(), "/cancel-job", StringComparison.OrdinalIgnoreCase))
        {
            await HandleJobCancellationAsync(update, cancellationToken);
            return;
        }

        if (update.Media is not null)
        {
            await HandleMediaAsync(update, update.Media, cancellationToken);
            return;
        }

        TelegramIntakeSession? session = sessions.Get(update.UserId);
        if (session is null)
        {
            await responder.SendTextAsync(
                update.ChatId,
                "Сначала отправьте видео. Для отмены используйте /cancel.",
                cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(update.Text))
        {
            await responder.SendTextAsync(
                update.ChatId,
                "Комментарий не должен быть пустым. Опишите желаемый стиль ролика.",
                cancellationToken);
            return;
        }

        CreateJobResult result = await createJob.ExecuteAsync(
            new CreateJobCommand(
                session.SourceArtifactId,
                update.Text,
                new ProductionSettings(null, captionsEnabled: true, musicEnabled: false),
                update.UserId),
            cancellationToken);

        if (result is CreateJobResult.Succeeded succeeded)
        {
            sessions.Remove(update.UserId);
            await responder.SendTextAsync(
                update.ChatId,
                succeeded.WasCreated
                    ? "Задание принято. Начинаю обработку."
                    : "Это видео уже принято ранее. Продолжаю обработку существующего задания.",
                cancellationToken);
            return;
        }

        CreateJobResult.Rejected rejected = (CreateJobResult.Rejected)result;
        logger?.LogWarning(
            "Telegram Job creation rejected for user {UserId}: {ErrorCode}",
            update.UserId,
            rejected.Error.Code);
        await responder.SendTextAsync(
            update.ChatId,
            "Не удалось принять задание. Попробуйте отправить видео ещё раз.",
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

        TelegramDownloadResult result = await downloader.DownloadAsync(input, cancellationToken);
        if (result is TelegramDownloadResult.Rejected failure)
        {
            logger?.LogWarning("Telegram download rejected: {ErrorCode}", failure.Code);
            await responder.SendTextAsync(update.ChatId, "Не удалось принять видео: проверьте формат и размер или повторите позже.", cancellationToken);
            return;
        }
        TelegramDownloadedFile downloaded = ((TelegramDownloadResult.Succeeded)result).File;
        if (downloaded.SizeBytes > limitsOptions.MaxInputFileBytes)
        {
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
        sessions.Set(new TelegramIntakeSession(
            update.UserId,
            stored.ArtifactId,
            input.FileId,
            input.FileUniqueId));
        await responder.SendTextAsync(
            update.ChatId,
            "Видео получено. Теперь отправьте непустой комментарий.",
            cancellationToken);
    }
}
