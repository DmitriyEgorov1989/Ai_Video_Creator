namespace VideoFactory.Bot.Logging;

public static class PipelineLogScope
{
    public static IDisposable Begin(
        ILogger logger,
        string? jobId = null,
        string? variantId = null,
        string? stage = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        Dictionary<string, object?> values = [];

        if (jobId is not null)
        {
            values["jobId"] = jobId;
        }

        if (variantId is not null)
        {
            values["variantId"] = variantId;
        }

        if (stage is not null)
        {
            values["stage"] = stage;
        }

        return logger.BeginScope(values) ?? EmptyScope.Instance;
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
