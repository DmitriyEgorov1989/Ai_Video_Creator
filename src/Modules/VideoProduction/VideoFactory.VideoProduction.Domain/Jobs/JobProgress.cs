namespace VideoFactory.VideoProduction.Domain.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    AwaitingConfirmation,
    Completed,
    Failed,
    Cancelled
}

public enum ProductionStage
{
    Preparation,
    Analysis,
    Briefing,
    Planning,
    Preview,
    Rendering,
    Verification,
    Packaging,
    Delivery
}

public enum ConfirmationKind
{
    Mode,
    Brief,
    FinalRender
}

public sealed class JobProgress
{
    public JobStatus Status { get; }

    public ProductionStage Stage { get; }

    public ProductionMode RequestedMode { get; }

    public ProductionMode? EffectiveMode { get; }

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

    public static JobProgress Create(ProductionMode requestedMode)
    {
        if (!Enum.IsDefined(requestedMode))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        }

        ProductionMode? effectiveMode = requestedMode == ProductionMode.Auto ? null : requestedMode;
        return new(JobStatus.Queued, ProductionStage.Preparation, requestedMode, effectiveMode, null);
    }

    public JobProgress Start()
    {
        Require(JobStatus.Queued, ProductionStage.Preparation);
        return Next(JobStatus.Running, ProductionStage.Preparation);
    }

    public JobProgress CompletePreparation()
    {
        RequireRunning(ProductionStage.Preparation);
        return Next(JobStatus.Running, ProductionStage.Analysis);
    }

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

    public JobProgress CompleteBriefing()
    {
        RequireDirectorStage(ProductionStage.Briefing);
        return Next(
            JobStatus.AwaitingConfirmation,
            ProductionStage.Briefing,
            EffectiveMode,
            ConfirmationKind.Brief);
    }

    public JobProgress ApproveBrief()
    {
        RequireConfirmation(ConfirmationKind.Brief);
        return Next(JobStatus.Running, ProductionStage.Planning);
    }

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

    public JobProgress CompletePreview()
    {
        RequireDirectorStage(ProductionStage.Preview);
        return Next(
            JobStatus.AwaitingConfirmation,
            ProductionStage.Preview,
            EffectiveMode,
            ConfirmationKind.FinalRender);
    }

    public JobProgress ApproveFinalRender()
    {
        RequireConfirmation(ConfirmationKind.FinalRender);
        return Next(JobStatus.Running, ProductionStage.Rendering);
    }

    public JobProgress CompleteRendering() => AdvanceRunning(
        ProductionStage.Rendering,
        ProductionStage.Verification);

    public JobProgress CompleteVerification() => AdvanceRunning(
        ProductionStage.Verification,
        ProductionStage.Packaging);

    public JobProgress CompletePackaging() => AdvanceRunning(
        ProductionStage.Packaging,
        ProductionStage.Delivery);

    public JobProgress ConfirmDelivery()
    {
        RequireRunning(ProductionStage.Delivery);
        return Next(JobStatus.Completed, ProductionStage.Delivery);
    }

    public JobProgress Fail()
    {
        RequireNonTerminal();
        return Next(JobStatus.Failed, Stage, EffectiveMode, null);
    }

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
