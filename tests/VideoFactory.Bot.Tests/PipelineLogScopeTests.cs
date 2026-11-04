using Microsoft.Extensions.Logging;
using VideoFactory.Bot.Logging;

namespace VideoFactory.Bot.Tests;

public sealed class PipelineLogScopeTests
{
    [Fact]
    public void BeginAddsOnlyKnownCorrelationValues()
    {
        ScopeCapturingLogger logger = new();

        using IDisposable scope = PipelineLogScope.Begin(
            logger,
            jobId: "job-1",
            stage: "diagnostics");

        IReadOnlyDictionary<string, object?> values = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            logger.Scope);
        Assert.Equal("job-1", values["jobId"]);
        Assert.Equal("diagnostics", values["stage"]);
        Assert.DoesNotContain("variantId", values);
    }

    private sealed class ScopeCapturingLogger : ILogger
    {
        public object? Scope { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            Scope = state;
            return CapturedScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class CapturedScope : IDisposable
    {
        public static CapturedScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
