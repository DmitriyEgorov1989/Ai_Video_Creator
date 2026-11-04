namespace VideoFactory.Bot.Diagnostics;

public enum DiagnosticStatus
{
    Passed,
    Warning,
    Failed
}

public sealed record DiagnosticCheck(
    string Name,
    DiagnosticStatus Status,
    string Message);

public sealed record DiagnosticReport(IReadOnlyList<DiagnosticCheck> Checks)
{
    public bool IsSuccessful => Checks.All(check => check.Status != DiagnosticStatus.Failed);
}
