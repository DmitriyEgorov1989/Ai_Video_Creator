namespace VideoFactory.VideoProduction.Domain.Jobs;

/// <summary>
/// Определяет текущее состояние производственного задания.
/// </summary>
public enum JobStatus
{
    /// <summary>
    /// Задание ожидает начала обработки.
    /// </summary>
    Queued,

    /// <summary>
    /// Задание обрабатывается.
    /// </summary>
    Running,

    /// <summary>
    /// Обработка приостановлена до подтверждения пользователя.
    /// </summary>
    AwaitingConfirmation,

    /// <summary>
    /// Задание успешно завершено.
    /// </summary>
    Completed,

    /// <summary>
    /// Обработка задания завершилась ошибкой.
    /// </summary>
    Failed,

    /// <summary>
    /// Обработка задания отменена.
    /// </summary>
    Cancelled
}

/// <summary>
/// Определяет этап производственного конвейера.
/// </summary>
public enum ProductionStage
{
    /// <summary>
    /// Подготовка исходных материалов.
    /// </summary>
    Preparation = 0,

    /// <summary>
    /// Анализ подготовленного видео.
    /// </summary>
    Analysis = 1,

    /// <summary>
    /// Подготовка и согласование творческого брифа.
    /// </summary>
    Briefing = 2,

    /// <summary>
    /// Формирование монтажных планов.
    /// </summary>
    Planning = 3,

    /// <summary>
    /// Подготовка и согласование предварительного просмотра.
    /// </summary>
    Preview = 4,

    /// <summary>
    /// Рендеринг итоговых видео.
    /// </summary>
    Rendering = 5,

    /// <summary>
    /// Проверка готовых видео.
    /// </summary>
    Verification = 6,

    /// <summary>
    /// Финализация проверенного результата: QA report, manifest и MP4 без ZIP.
    /// </summary>
    Packaging = 7,

    /// <summary>
    /// Доставка результатов пользователю.
    /// </summary>
    Delivery = 8,

    /// <summary>
    /// Объективная и субъективная критика готового рендера до финальной проверки.
    /// </summary>
    Critique = 9
}

/// <summary>
/// Определяет вид пользовательского подтверждения, необходимого для продолжения обработки.
/// </summary>
public enum ConfirmationKind
{
    /// <summary>
    /// Требуется выбрать итоговый режим производства.
    /// </summary>
    Mode,

    /// <summary>
    /// Требуется утвердить творческий бриф.
    /// </summary>
    Brief,

    /// <summary>
    /// Требуется разрешить финальный рендеринг.
    /// </summary>
    FinalRender,

    /// <summary>
    /// Требуется разрешить платный вызов планировщика сверх лимита задания.
    /// </summary>
    CostApproval
}

/// <summary>
/// Представляет неизменяемое состояние прохождения задания по производственному конвейеру.
/// </summary>
public sealed class JobProgress
{
    /// <summary>
    /// Получает текущее состояние задания.
    /// </summary>
    /// <value>Состояние обработки задания.</value>
    public JobStatus Status { get; }

    /// <summary>
    /// Получает текущий этап производственного конвейера.
    /// </summary>
    /// <value>Этап, на котором находится задание.</value>
    public ProductionStage Stage { get; }

    /// <summary>
    /// Получает первоначально запрошенный режим производства.
    /// </summary>
    /// <value>Режим из запроса на производство.</value>
    public ProductionMode RequestedMode { get; }

    /// <summary>
    /// Получает режим, фактически используемый производственным конвейером.
    /// </summary>
    /// <value>Режим <see cref="ProductionMode.Fast" /> или <see cref="ProductionMode.Director" />; <see langword="null" />, пока автоматический режим не разрешён.</value>
    public ProductionMode? EffectiveMode { get; }

    /// <summary>
    /// Получает ожидаемый вид пользовательского подтверждения.
    /// </summary>
    /// <value>Вид подтверждения или <see langword="null" />, если задание не ожидает подтверждения.</value>
    public ConfirmationKind? PendingConfirmation { get; }

    private JobProgress(
        JobStatus status,
        ProductionStage stage,
        ProductionMode requestedMode,
        ProductionMode? effectiveMode,
        ConfirmationKind? pendingConfirmation)
    {
        Status = status;
        Stage = stage;
        RequestedMode = requestedMode;
        EffectiveMode = effectiveMode;
        PendingConfirmation = pendingConfirmation;
    }

    /// <summary>
    /// Создаёт начальное состояние задания в очереди на подготовку.
    /// </summary>
    /// <param name="requestedMode">Запрошенный режим производства.</param>
    /// <returns>Начальное состояние задания.</returns>
    public static JobProgress Create(ProductionMode requestedMode)
    {
        if (!Enum.IsDefined(requestedMode))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        }

        ProductionMode? effectiveMode = requestedMode == ProductionMode.Auto ? null : requestedMode;
        return new(JobStatus.Queued, ProductionStage.Preparation, requestedMode, effectiveMode, null);
    }

    /// <summary>
    /// Восстанавливает состояние задания, сохранённое persistence-адаптером.
    /// </summary>
    /// <param name="status">Сохранённый статус задания.</param>
    /// <param name="stage">Сохранённый этап задания.</param>
    /// <param name="requestedMode">Изначально запрошенный режим.</param>
    /// <param name="effectiveMode">Фактически выбранный режим или <see langword="null"/>.</param>
    /// <param name="pendingConfirmation">Ожидаемое подтверждение или <see langword="null"/>.</param>
    /// <returns>Восстановленное состояние задания.</returns>
    public static JobProgress Restore(
        JobStatus status,
        ProductionStage stage,
        ProductionMode requestedMode,
        ProductionMode? effectiveMode,
        ConfirmationKind? pendingConfirmation)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage));
        }

        if (!Enum.IsDefined(requestedMode))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        }

        if (effectiveMode is not null && !Enum.IsDefined(effectiveMode.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveMode));
        }

        if (pendingConfirmation is not null && !Enum.IsDefined(pendingConfirmation.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(pendingConfirmation));
        }

        return new(status, stage, requestedMode, effectiveMode, pendingConfirmation);
    }

    /// <summary>
    /// Начинает подготовку задания, находящегося в очереди.
    /// </summary>
    /// <returns>Новое состояние выполняемой подготовки.</returns>
    public JobProgress Start()
    {
        Require(JobStatus.Queued, ProductionStage.Preparation);
        return Next(JobStatus.Running, ProductionStage.Preparation);
    }

    /// <summary>
    /// Завершает подготовку и переводит задание к анализу.
    /// </summary>
    /// <returns>Новое состояние выполняемого анализа.</returns>
    public JobProgress CompletePreparation()
    {
        RequireRunning(ProductionStage.Preparation);
        return Next(JobStatus.Running, ProductionStage.Analysis);
    }

    /// <summary>
    /// Завершает анализ и выбирает следующий этап согласно запрошенному режиму.
    /// </summary>
    /// <returns>Новое состояние следующего этапа или ожидания выбора режима.</returns>
    public JobProgress CompleteAnalysis()
    {
        RequireRunning(ProductionStage.Analysis);

        return RequestedMode switch
        {
            ProductionMode.Auto => Next(
                JobStatus.AwaitingConfirmation,
                ProductionStage.Analysis,
                null,
                ConfirmationKind.Mode),
            ProductionMode.Director => Next(JobStatus.Running, ProductionStage.Briefing),
            ProductionMode.Fast => Next(JobStatus.Running, ProductionStage.Planning),
            _ => throw new InvalidOperationException("Requested production mode is invalid.")
        };
    }

    /// <summary>
    /// Подтверждает итоговый режим для задания, созданного в автоматическом режиме.
    /// </summary>
    /// <param name="mode">Итоговый режим <see cref="ProductionMode.Fast" /> или <see cref="ProductionMode.Director" />.</param>
    /// <returns>Новое состояние на первом этапе выбранного режима.</returns>
    public JobProgress ConfirmMode(ProductionMode mode)
    {
        RequireConfirmation(ConfirmationKind.Mode);
        if (mode is not (ProductionMode.Fast or ProductionMode.Director))
        {
            throw new ArgumentException("Auto mode must be resolved to Fast or Director.", nameof(mode));
        }

        ProductionStage nextStage = mode == ProductionMode.Fast
            ? ProductionStage.Planning
            : ProductionStage.Briefing;

        return Next(JobStatus.Running, nextStage, mode, null);
    }

    /// <summary>
    /// Завершает подготовку брифа и приостанавливает задание до его утверждения.
    /// </summary>
    /// <returns>Новое состояние ожидания подтверждения брифа.</returns>
    public JobProgress CompleteBriefing()
    {
        RequireDirectorStage(ProductionStage.Briefing);
        return Next(
            JobStatus.AwaitingConfirmation,
            ProductionStage.Briefing,
            EffectiveMode,
            ConfirmationKind.Brief);
    }

    /// <summary>
    /// Утверждает подготовленный бриф и переводит задание к планированию.
    /// </summary>
    /// <returns>Новое состояние выполняемого планирования.</returns>
    public JobProgress ApproveBrief()
    {
        RequireConfirmation(ConfirmationKind.Brief);
        return Next(JobStatus.Running, ProductionStage.Planning);
    }

    /// <summary>
    /// Завершает планирование и выбирает следующий этап согласно итоговому режиму.
    /// </summary>
    /// <returns>Новое состояние рендеринга или предварительного просмотра.</returns>
    public JobProgress CompletePlanning()
    {
        RequireRunning(ProductionStage.Planning);
        return EffectiveMode switch
        {
            ProductionMode.Fast => Next(JobStatus.Running, ProductionStage.Rendering),
            ProductionMode.Director => Next(JobStatus.Running, ProductionStage.Preview),
            _ => throw new InvalidOperationException("An effective mode must be selected before planning completes.")
        };
    }

    /// <summary>
    /// Завершает предварительный просмотр и приостанавливает задание до разрешения финального рендеринга.
    /// </summary>
    /// <returns>Новое состояние ожидания подтверждения финального рендеринга.</returns>
    public JobProgress CompletePreview()
    {
        RequireDirectorStage(ProductionStage.Preview);
        return Next(
            JobStatus.AwaitingConfirmation,
            ProductionStage.Preview,
            EffectiveMode,
            ConfirmationKind.FinalRender);
    }

    /// <summary>
    /// Разрешает финальный рендеринг после предварительного просмотра.
    /// </summary>
    /// <returns>Новое состояние выполняемого рендеринга.</returns>
    public JobProgress ApproveFinalRender()
    {
        RequireConfirmation(ConfirmationKind.FinalRender);
        return Next(JobStatus.Running, ProductionStage.Rendering);
    }

    /// <summary>
    /// Переводит планирование в ожидание подтверждения дополнительной стоимости.
    /// </summary>
    /// <returns>Новое состояние задания в ожидании решения пользователя.</returns>
    public JobProgress AwaitCostApproval()
    {
        RequireRunning(ProductionStage.Planning);
        return Next(JobStatus.AwaitingConfirmation, ProductionStage.Planning, EffectiveMode, ConfirmationKind.CostApproval);
    }

    /// <summary>
    /// Разрешает продолжение планирования после подтверждения стоимости.
    /// </summary>
    /// <returns>Новое состояние выполняемого планирования.</returns>
    public JobProgress ApproveCost()
    {
        RequireConfirmation(ConfirmationKind.CostApproval);
        return Next(JobStatus.Running, ProductionStage.Planning, EffectiveMode, null);
    }

    /// <summary>
    /// Завершает рендеринг и переводит задание к критике монтажа.
    /// </summary>
    /// <returns>Новое состояние выполняемой критики.</returns>
    public JobProgress CompleteRendering() => AdvanceRunning(
        ProductionStage.Rendering,
        ProductionStage.Critique);

    /// <summary>
    /// Завершает критику принятого рендера и переводит задание к технической проверке.
    /// </summary>
    /// <returns>Новое состояние выполняемой проверки.</returns>
    public JobProgress CompleteCritique() => AdvanceRunning(
        ProductionStage.Critique,
        ProductionStage.Verification);

    /// <summary>
    /// Возвращает задание из критики к рендерингу новой ревизии монтажного плана.
    /// </summary>
    /// <returns>Новое состояние повторного рендеринга.</returns>
    public JobProgress RequestRemount() => AdvanceRunning(
        ProductionStage.Critique,
        ProductionStage.Rendering);

    /// <summary>
    /// Завершает проверку и переводит задание к упаковке результатов.
    /// </summary>
    /// <returns>Новое состояние выполняемой упаковки.</returns>
    public JobProgress CompleteVerification() => AdvanceRunning(
        ProductionStage.Verification,
        ProductionStage.Packaging);

    /// <summary>
    /// Завершает упаковку и переводит задание к доставке результатов.
    /// </summary>
    /// <returns>Новое состояние выполняемой доставки.</returns>
    public JobProgress CompletePackaging() => AdvanceRunning(
        ProductionStage.Packaging,
        ProductionStage.Delivery);

    /// <summary>
    /// Подтверждает доставку результатов и успешно завершает задание.
    /// </summary>
    /// <returns>Новое завершённое состояние задания.</returns>
    public JobProgress ConfirmDelivery()
    {
        RequireRunning(ProductionStage.Delivery);
        return Next(JobStatus.Completed, ProductionStage.Delivery);
    }

    /// <summary>
    /// Завершает ошибкой задание, находящееся в незавершённом состоянии.
    /// </summary>
    /// <returns>Новое состояние задания с ошибкой.</returns>
    public JobProgress Fail()
    {
        RequireNonTerminal();
        return Next(JobStatus.Failed, Stage, EffectiveMode, null);
    }

    /// <summary>
    /// Отменяет задание, находящееся в незавершённом состоянии.
    /// </summary>
    /// <returns>Новое отменённое состояние задания.</returns>
    public JobProgress Cancel()
    {
        RequireNonTerminal();
        return Next(JobStatus.Cancelled, Stage, EffectiveMode, null);
    }

    private JobProgress AdvanceRunning(ProductionStage current, ProductionStage next)
    {
        RequireRunning(current);
        return Next(JobStatus.Running, next);
    }

    private void RequireDirectorStage(ProductionStage stage)
    {
        RequireRunning(stage);
        if (EffectiveMode != ProductionMode.Director)
        {
            throw new InvalidOperationException("This transition is available only in Director mode.");
        }
    }

    private void RequireRunning(ProductionStage stage) => Require(JobStatus.Running, stage);

    private void RequireConfirmation(ConfirmationKind confirmation)
    {
        if (Status != JobStatus.AwaitingConfirmation || PendingConfirmation != confirmation)
        {
            throw InvalidTransition();
        }
    }

    private void Require(JobStatus status, ProductionStage stage)
    {
        if (Status != status || Stage != stage)
        {
            throw InvalidTransition();
        }
    }

    private void RequireNonTerminal()
    {
        if (Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        {
            throw InvalidTransition();
        }
    }

    private JobProgress Next(
        JobStatus status,
        ProductionStage stage,
        ProductionMode? effectiveMode = null,
        ConfirmationKind? confirmation = null) =>
        new(
            status,
            stage,
            RequestedMode,
            effectiveMode ?? EffectiveMode,
            confirmation);

    private InvalidOperationException InvalidTransition() => new(
        $"Transition is not allowed from {Status}/{Stage}.");
}
