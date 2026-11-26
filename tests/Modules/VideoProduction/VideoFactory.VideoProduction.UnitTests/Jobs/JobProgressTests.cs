using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.UnitTests.Jobs;

public sealed class JobProgressTests
{
    [Fact]
    public void FastModeCompletesWithoutConfirmation()
    {
        JobProgress completed = JobProgress.Create(ProductionMode.Fast)
            .Start()
            .CompletePreparation()
            .CompleteAnalysis()
            .CompletePlanning()
            .CompleteRendering()
            .CompleteCritique()
            .CompleteVerification()
            .CompletePackaging()
            .ConfirmDelivery();

        Assert.Equal(JobStatus.Completed, completed.Status);
        Assert.Equal(ProductionStage.Delivery, completed.Stage);
        Assert.Equal(ProductionMode.Fast, completed.EffectiveMode);
    }

    [Fact]
    public void AutoWaitsForModeConfirmation()
    {
        JobProgress waiting = JobProgress.Create(ProductionMode.Auto)
            .Start()
            .CompletePreparation()
            .CompleteAnalysis();

        Assert.Equal(JobStatus.AwaitingConfirmation, waiting.Status);
        Assert.Equal(ProductionStage.Analysis, waiting.Stage);
        Assert.Equal(ConfirmationKind.Mode, waiting.PendingConfirmation);
        Assert.Null(waiting.EffectiveMode);
        Assert.Throws<InvalidOperationException>(() => waiting.CompletePlanning());

        JobProgress confirmed = waiting.ConfirmMode(ProductionMode.Fast);

        Assert.Equal(JobStatus.Running, confirmed.Status);
        Assert.Equal(ProductionStage.Planning, confirmed.Stage);
        Assert.Equal(ProductionMode.Fast, confirmed.EffectiveMode);
    }

    [Fact]
    public void DirectorModeRequiresBriefAndFinalRenderApproval()
    {
        JobProgress brief = JobProgress.Create(ProductionMode.Director)
            .Start()
            .CompletePreparation()
            .CompleteAnalysis()
            .CompleteBriefing();

        Assert.Equal(ConfirmationKind.Brief, brief.PendingConfirmation);

        JobProgress finalRender = brief
            .ApproveBrief()
            .CompletePlanning()
            .CompletePreview();

        Assert.Equal(ConfirmationKind.FinalRender, finalRender.PendingConfirmation);

        JobProgress rendering = finalRender.ApproveFinalRender();

        Assert.Equal(JobStatus.Running, rendering.Status);
        Assert.Equal(ProductionStage.Rendering, rendering.Stage);
    }

    [Fact]
    public void AutoCannotBeConfirmedAsAuto()
    {
        JobProgress waiting = JobProgress.Create(ProductionMode.Auto)
            .Start()
            .CompletePreparation()
            .CompleteAnalysis();

        Assert.Throws<ArgumentException>(() => waiting.ConfirmMode(ProductionMode.Auto));
    }

    [Fact]
    public void WrongApprovalAndDirectorOnlyTransitionsAreRejected()
    {
        JobProgress fastPlanning = JobProgress.Create(ProductionMode.Fast)
            .Start()
            .CompletePreparation()
            .CompleteAnalysis();

        Assert.Throws<InvalidOperationException>(() => fastPlanning.ApproveBrief());
        Assert.Throws<InvalidOperationException>(() => fastPlanning.CompletePreview());
    }

    [Fact]
    public void JobCanBeCancelledWhileWaitingForConfirmation()
    {
        JobProgress waiting = JobProgress.Create(ProductionMode.Auto)
            .Start()
            .CompletePreparation()
            .CompleteAnalysis();

        JobProgress cancelled = waiting.Cancel();

        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        Assert.Null(cancelled.PendingConfirmation);
    }

    [Fact]
    public void InvalidTransitionDoesNotChangePreviousSnapshot()
    {
        JobProgress queued = JobProgress.Create(ProductionMode.Fast);

        Assert.Throws<InvalidOperationException>(() => queued.CompleteAnalysis());
        Assert.Equal(JobStatus.Queued, queued.Status);
        Assert.Equal(ProductionStage.Preparation, queued.Stage);
    }

    [Fact]
    public void CritiqueCanRequestRemountAndEvaluateTheNextRender()
    {
        JobProgress critique = JobProgress.Create(ProductionMode.Fast)
            .Start()
            .CompletePreparation()
            .CompleteAnalysis()
            .CompletePlanning()
            .CompleteRendering();

        Assert.Equal(ProductionStage.Critique, critique.Stage);

        JobProgress secondCritique = critique
            .RequestRemount()
            .CompleteRendering();

        Assert.Equal(JobStatus.Running, secondCritique.Status);
        Assert.Equal(ProductionStage.Critique, secondCritique.Stage);
        Assert.Equal(ProductionStage.Verification, secondCritique.CompleteCritique().Stage);
    }

    [Theory]
    [InlineData(JobStatus.Completed)]
    [InlineData(JobStatus.Failed)]
    [InlineData(JobStatus.Cancelled)]
    public void TerminalStateCannotTransition(JobStatus terminalStatus)
    {
        JobProgress progress = terminalStatus switch
        {
            JobStatus.Completed => CompleteFastJob(),
            JobStatus.Failed => JobProgress.Create(ProductionMode.Fast).Fail(),
            JobStatus.Cancelled => JobProgress.Create(ProductionMode.Fast).Cancel(),
            _ => throw new ArgumentOutOfRangeException(nameof(terminalStatus))
        };

        Assert.Throws<InvalidOperationException>(() => progress.Cancel());
        Assert.Throws<InvalidOperationException>(() => progress.Fail());
    }

    private static JobProgress CompleteFastJob() => JobProgress.Create(ProductionMode.Fast)
        .Start()
        .CompletePreparation()
        .CompleteAnalysis()
        .CompletePlanning()
        .CompleteRendering()
        .CompleteCritique()
        .CompleteVerification()
        .CompletePackaging()
        .ConfirmDelivery();
}
