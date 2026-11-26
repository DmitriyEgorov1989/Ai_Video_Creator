using VideoFactory.VideoProduction.Application.Jobs.Queue;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.Application.Variants;

/// <summary>
/// Описывает запрос владельца на создание альтернативного варианта завершённого задания.
/// </summary>
/// <param name="JobId">Идентификатор задания.</param>
/// <param name="SourceVariantId">Идентификатор активного варианта, от которого создаётся альтернатива.</param>
/// <param name="TelegramUserId">Идентификатор Telegram-пользователя, который должен быть владельцем задания.</param>
/// <param name="ChangeRequest">Непустое описание требуемых изменений.</param>
/// <param name="RequestKey">Непустой ключ идемпотентности в пределах задания.</param>
public sealed record RequestVariantChangeCommand(
    JobId JobId,
    VariantId SourceVariantId,
    long TelegramUserId,
    string ChangeRequest,
    string RequestKey);

/// <summary>Определяет причину отклонения некорректного запроса изменения варианта.</summary>
public enum RequestVariantChangeErrorCode
{
    /// <summary>Идентификатор Telegram-пользователя не является положительным.</summary>
    InvalidTelegramUserId,

    /// <summary>Описание изменений пусто или состоит из пробельных символов.</summary>
    EmptyChangeRequest,

    /// <summary>Ключ идемпотентности пуст или состоит из пробельных символов.</summary>
    EmptyRequestKey
}

/// <summary>Представляет ожидаемый результат запроса альтернативного варианта.</summary>
public abstract record RequestVariantChangeResult
{
    /// <summary>
    /// Запрос создан либо найден по ключу идемпотентности.
    /// </summary>
    /// <param name="VariantId">Идентификатор созданного ранее или сейчас варианта.</param>
    /// <param name="WasCreated"><see langword="true" />, если вариант создан этим вызовом.</param>
    public sealed record Succeeded(VariantId VariantId, bool WasCreated) : RequestVariantChangeResult;

    /// <summary>
    /// Запрос отклонён до обращения к состоянию задания.
    /// </summary>
    /// <param name="ErrorCode">Причина отклонения.</param>
    public sealed record Invalid(RequestVariantChangeErrorCode ErrorCode) : RequestVariantChangeResult;

    /// <summary>Пользователь не является владельцем задания.</summary>
    public sealed record Forbidden : RequestVariantChangeResult;

    /// <summary>Задание, исходный вариант или их состояние больше не соответствуют запросу.</summary>
    public sealed record Stale : RequestVariantChangeResult;
}

/// <summary>Определяет persistence seam атомарного создания альтернативного варианта.</summary>
public interface IVariantChangeStore
{
    /// <summary>
    /// Идемпотентно создаёт вариант и делает его активным либо возвращает ожидаемый отказ.
    /// </summary>
    /// <param name="command">Проверяемый запрос изменения.</param>
    /// <param name="newVariantId">Новый идентификатор для первой обработки запроса.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Результат атомарной операции.</returns>
    Task<RequestVariantChangeResult> RequestAsync(
        RequestVariantChangeCommand command,
        VariantId newVariantId,
        CancellationToken cancellationToken);
}

/// <summary>Создаёт альтернативный Variant для завершённого Job.</summary>
public sealed class RequestVariantChangeUseCase
{
    private readonly IVariantChangeStore store;
    private readonly IJobSignal? signal;

    /// <summary>Создаёт сценарий запроса альтернативного варианта.</summary>
    /// <param name="store">Persistence seam атомарного изменения.</param>
    /// <param name="signal">Необязательный сигнал последовательному обработчику; <see langword="null" /> отключает публикацию.</param>
    public RequestVariantChangeUseCase(IVariantChangeStore store, IJobSignal? signal = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.signal = signal;
    }

    /// <summary>Проверяет запрос и идемпотентно создаёт альтернативный Variant.</summary>
    /// <param name="command">Запрос владельца задания.</param>
    /// <param name="cancellationToken">Токен отмены операции.</param>
    /// <returns>Успех либо типизированный ожидаемый отказ.</returns>
    public async Task<RequestVariantChangeResult> ExecuteAsync(
        RequestVariantChangeCommand command,
        CancellationToken cancellationToken = default)
    {
        RequestVariantChangeErrorCode? error = Validate(command);
        if (error is not null)
        {
            return new RequestVariantChangeResult.Invalid(error.Value);
        }

        RequestVariantChangeResult result = await store.RequestAsync(
            command,
            VariantId.New(),
            cancellationToken);
        if (result is RequestVariantChangeResult.Succeeded { WasCreated: true } && signal is not null)
        {
            await signal.PublishAsync(command.JobId, cancellationToken);
        }

        return result;
    }

    private static RequestVariantChangeErrorCode? Validate(RequestVariantChangeCommand command)
    {
        if (command.TelegramUserId <= 0)
        {
            return RequestVariantChangeErrorCode.InvalidTelegramUserId;
        }

        if (string.IsNullOrWhiteSpace(command.ChangeRequest))
        {
            return RequestVariantChangeErrorCode.EmptyChangeRequest;
        }

        return string.IsNullOrWhiteSpace(command.RequestKey)
            ? RequestVariantChangeErrorCode.EmptyRequestKey
            : null;
    }
}
