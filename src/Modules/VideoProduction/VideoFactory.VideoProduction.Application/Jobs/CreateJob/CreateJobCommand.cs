using System.Collections.Immutable;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.Application.Jobs.CreateJob;

/// <summary>
/// Представляет входные данные сценария создания одного задания.
/// </summary>
public sealed record CreateJobCommand
{
    /// <summary>Получает упорядоченные идентификаторы исходных видеоартефактов.</summary>
    public ImmutableArray<ArtifactId> SourceArtifactIds { get; }

    /// <summary>Получает комментарий пользователя или <see langword="null"/>.</summary>
    public string? Comment { get; }

    /// <summary>Получает настройки производства или <see langword="null"/>.</summary>
    public ProductionSettings? Settings { get; }

    /// <summary>Получает Telegram user ID или <see langword="null"/> вне Telegram intake.</summary>
    public long? TelegramUserId { get; }

    /// <summary>Создаёт совместимую команду с одним источником.</summary>
    /// <param name="sourceArtifactId">Идентификатор исходного видеоартефакта.</param>
    /// <param name="comment">Комментарий пользователя или <see langword="null"/>.</param>
    /// <param name="settings">Настройки производства или <see langword="null"/>.</param>
    /// <param name="telegramUserId">Telegram user ID или <see langword="null"/> вне Telegram intake.</param>
    public CreateJobCommand(
        ArtifactId sourceArtifactId,
        string? comment,
        ProductionSettings? settings,
        long? telegramUserId = null)
        : this([sourceArtifactId], comment, settings, telegramUserId)
    {
    }

    /// <summary>Создаёт команду с упорядоченным набором источников.</summary>
    /// <param name="sourceArtifactIds">Упорядоченные идентификаторы исходных видеоартефактов.</param>
    /// <param name="comment">Комментарий пользователя или <see langword="null"/>.</param>
    /// <param name="settings">Настройки производства или <see langword="null"/>.</param>
    /// <param name="telegramUserId">Telegram user ID или <see langword="null"/> вне Telegram intake.</param>
    public CreateJobCommand(
        IReadOnlyList<ArtifactId> sourceArtifactIds,
        string? comment,
        ProductionSettings? settings,
        long? telegramUserId = null)
    {
        ArgumentNullException.ThrowIfNull(sourceArtifactIds);
        SourceArtifactIds = [.. sourceArtifactIds];
        Comment = comment;
        Settings = settings;
        TelegramUserId = telegramUserId;
    }
}
