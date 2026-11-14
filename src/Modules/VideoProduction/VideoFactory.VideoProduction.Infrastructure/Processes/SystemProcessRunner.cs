using System.ComponentModel;
using System.Diagnostics;
using VideoFactory.VideoProduction.Application.Processes;

namespace VideoFactory.VideoProduction.Infrastructure.Processes;

/// <summary>
/// Запускает внешние процессы напрямую, без shell-интерполяции.
/// </summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <inheritdoc />
    public async Task<ProcessExecutionResult> RunAsync(
        ProcessStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return CreateCancelledResult(string.Empty, string.Empty);
        }

        using Process process = CreateProcess(request);

        try
        {
            if (!process.Start())
            {
                return CreateStartFailedResult(
                    "process.start_failed",
                    "The external process could not be started.");
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception ||
            exception is InvalidOperationException)
        {
            return CreateStartFailedResult("process.start_failed", exception.Message);
        }

        Task<BoundedProcessOutput> standardOutputTask =
            BoundedProcessOutputCollector.ReadAsync(
                process.StandardOutput.BaseStream,
                request.MaxOutputBytes,
                CancellationToken.None);
        Task<BoundedProcessOutput> standardErrorTask =
            BoundedProcessOutputCollector.ReadAsync(
                process.StandardError.BaseStream,
                request.MaxOutputBytes,
                CancellationToken.None);

        using CancellationTokenSource timeoutSource = new(request.Timeout);
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (process.HasExited)
            {
                BoundedProcessOutput completedOutput = await standardOutputTask;
                BoundedProcessOutput completedError = await standardErrorTask;
                return CreateExitCodeResult(process, completedOutput, completedError);
            }

            bool cancelled = cancellationToken.IsCancellationRequested;
            await KillProcessTreeAsync(process);

            BoundedProcessOutput output = await standardOutputTask;
            BoundedProcessOutput error = await standardErrorTask;

            return cancelled
                ? CreateCancelledResult(output, error)
                : CreateTimedOutResult(output, error, request.Timeout);
        }

        BoundedProcessOutput standardOutput = await standardOutputTask;
        BoundedProcessOutput standardError = await standardErrorTask;

        return CreateExitCodeResult(process, standardOutput, standardError);
    }

    private static Process CreateProcess(ProcessStartRequest request)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        if (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Процесс мог завершиться между проверкой и Kill.
            }
        }

        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершён и больше не может быть опрошен.
        }
    }

    private static ProcessExecutionResult CreateExitCodeResult(
        Process process,
        BoundedProcessOutput standardOutput,
        BoundedProcessOutput standardError)
    {
        bool succeeded = process.ExitCode == 0;

        return new ProcessExecutionResult(
            succeeded
                ? ProcessExecutionStatus.Succeeded
                : ProcessExecutionStatus.NonZeroExitCode,
            process.ExitCode,
            standardOutput.Text,
            standardError.Text,
            succeeded ? null : "process.exit_code",
            succeeded ? null : $"External process exited with code {process.ExitCode}.",
            standardOutput.Truncated,
            standardError.Truncated);
    }

    private static ProcessExecutionResult CreateTimedOutResult(
        BoundedProcessOutput standardOutput,
        BoundedProcessOutput standardError,
        TimeSpan timeout) =>
        new(
            ProcessExecutionStatus.TimedOut,
            null,
            standardOutput.Text,
            standardError.Text,
            "process.timeout",
            $"External process exceeded timeout of {timeout.TotalSeconds:0.###} seconds.",
            standardOutput.Truncated,
            standardError.Truncated);

    private static ProcessExecutionResult CreateCancelledResult(
        BoundedProcessOutput standardOutput,
        BoundedProcessOutput standardError) =>
        new(
            ProcessExecutionStatus.Cancelled,
            null,
            standardOutput.Text,
            standardError.Text,
            "process.cancelled",
            "External process execution was cancelled.",
            standardOutput.Truncated,
            standardError.Truncated);

    private static ProcessExecutionResult CreateCancelledResult(
        string standardOutput,
        string standardError) =>
        new(
            ProcessExecutionStatus.Cancelled,
            null,
            standardOutput,
            standardError,
            "process.cancelled",
            "External process execution was cancelled.",
            false,
            false);

    private static ProcessExecutionResult CreateStartFailedResult(
        string errorCode,
        string errorMessage) =>
        new(
            ProcessExecutionStatus.StartFailed,
            null,
            string.Empty,
            string.Empty,
            errorCode,
            errorMessage,
            false,
            false);

    private static void Validate(ProcessStartRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        ArgumentNullException.ThrowIfNull(request.Arguments);

        if (request.Arguments.Any(argument => argument is null))
        {
            throw new ArgumentException(
                "Process arguments cannot contain null values.",
                nameof(request));
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Process timeout must be positive.");
        }

        if (request.MaxOutputBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Maximum process output must be positive.");
        }
    }
}
