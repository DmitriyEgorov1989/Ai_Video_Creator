using System.Text.Json;
using VideoFactory.VideoProduction.Application.Processes;
using VideoFactory.VideoProduction.Infrastructure.Processes;

namespace VideoFactory.VideoProduction.IntegrationTests.Processes;

public sealed class SystemProcessRunnerTests
{
    [Fact]
    public async Task PassesEachArgumentWithoutShellInterpolation()
    {
        string outputPath = Path.Combine(
            Path.GetTempPath(),
            $"process-runner-{Guid.NewGuid():N}.json");
        string[] arguments =
        [
            "plain",
            "with spaces",
            "quote \"value\"",
            "semicolon; value",
            "dollar $value"
        ];

        try
        {
            IProcessRunner runner = new SystemProcessRunner();
            ProcessExecutionResult result = await runner.RunAsync(
                new ProcessStartRequest(
                    "dotnet",
                    [
                        FakeExecutablePath(),
                        "argument-dump",
                        outputPath,
                        .. arguments
                    ],
                    TimeSpan.FromSeconds(10),
                    65_536),
                CancellationToken.None);

            Assert.True(result.IsSuccess, result.ErrorMessage);

            string[] actual = JsonSerializer.Deserialize<string[]>(
                await File.ReadAllTextAsync(outputPath))!;

            Assert.Equal(arguments, actual);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task ReturnsOutputAndZeroExitCodeForSuccessfulProcess()
    {
        ProcessExecutionResult result = await RunAsync("success");

        Assert.Equal(ProcessExecutionStatus.Succeeded, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("success-output", result.StandardOutput);
        Assert.Equal("success-diagnostic", result.StandardError);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public async Task PreservesNonZeroExitCodeAndDiagnosticOutput()
    {
        ProcessExecutionResult result = await RunAsync("non-zero", ["42"]);

        Assert.Equal(ProcessExecutionStatus.NonZeroExitCode, result.Status);
        Assert.Equal(42, result.ExitCode);
        Assert.Contains("fake failure", result.StandardError);
        Assert.Equal("process.exit_code", result.ErrorCode);
    }

    [Fact]
    public async Task ReturnsTimeoutAndStopsLongRunningProcess()
    {
        ProcessExecutionResult result = await RunAsync(
            "timeout",
            ["10000"],
            timeout: TimeSpan.FromMilliseconds(250));

        Assert.Equal(ProcessExecutionStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Equal("process.timeout", result.ErrorCode);
    }

    [Fact]
    public async Task ReturnsCancelledWhenCallerCancelsExecution()
    {
        using CancellationTokenSource cancellation =
            new(TimeSpan.FromMilliseconds(250));

        ProcessExecutionResult result = await new SystemProcessRunner().RunAsync(
            CreateRequest("timeout", ["10000"], TimeSpan.FromSeconds(30)),
            cancellation.Token);

        Assert.Equal(ProcessExecutionStatus.Cancelled, result.Status);
        Assert.Equal("process.cancelled", result.ErrorCode);
    }

    [Fact]
    public async Task ReadsLargeOutputWithoutDeadlockAndAppliesByteLimit()
    {
        const int maxOutputBytes = 65_536;

        ProcessExecutionResult result = await RunAsync(
            "large-output",
            ["2097152"],
            maxOutputBytes: maxOutputBytes);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        Assert.InRange(
            System.Text.Encoding.UTF8.GetByteCount(result.StandardOutput),
            1,
            maxOutputBytes);
        Assert.InRange(
            System.Text.Encoding.UTF8.GetByteCount(result.StandardError),
            1,
            maxOutputBytes);
    }

    [Fact]
    public async Task TerminatesChildProcessTreeOnTimeout()
    {
        string pidFile = Path.Combine(
            Path.GetTempPath(),
            $"process-runner-child-{Guid.NewGuid():N}.pid");

        try
        {
            ProcessExecutionResult result = await RunAsync(
                "spawn-child",
                [pidFile],
                timeout: TimeSpan.FromMilliseconds(500));

            Assert.Equal(ProcessExecutionStatus.TimedOut, result.Status);

            int childPid = await WaitForPidAsync(pidFile);
            await WaitUntilAsync(
                () => !IsProcessRunning(childPid),
                TimeSpan.FromSeconds(3));

            Assert.False(IsProcessRunning(childPid));
        }
        finally
        {
            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }
        }
    }

    [Fact]
    public async Task ReturnsStartFailedForMissingExecutable()
    {
        ProcessExecutionResult result = await new SystemProcessRunner().RunAsync(
            new ProcessStartRequest(
                "missing-process-runner-executable",
                [],
                TimeSpan.FromSeconds(1),
                1024),
            CancellationToken.None);

        Assert.Equal(ProcessExecutionStatus.StartFailed, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Equal("process.start_failed", result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    private static string FakeExecutablePath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "VideoFactory.VideoProduction.ProcessRunnerFake.dll");

    private static ProcessStartRequest CreateRequest(
        string command,
        IReadOnlyList<string>? arguments = null,
        TimeSpan? timeout = null,
        int maxOutputBytes = 65_536) =>
        new(
            "dotnet",
            [FakeExecutablePath(), command, .. arguments ?? []],
            timeout ?? TimeSpan.FromSeconds(10),
            maxOutputBytes);

    private static async Task<ProcessExecutionResult> RunAsync(
        string command,
        IReadOnlyList<string>? arguments = null,
        TimeSpan? timeout = null,
        int maxOutputBytes = 65_536) =>
        await new SystemProcessRunner().RunAsync(
            CreateRequest(command, arguments, timeout, maxOutputBytes),
            CancellationToken.None);

    private static async Task<int> WaitForPidAsync(string pidFile)
    {
        await WaitUntilAsync(
            () => File.Exists(pidFile),
            TimeSpan.FromSeconds(3));

        return int.Parse(await File.ReadAllTextAsync(pidFile));
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new Xunit.Sdk.XunitException("Condition was not met in time.");
            }

            await Task.Delay(50);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using System.Diagnostics.Process process =
                System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
