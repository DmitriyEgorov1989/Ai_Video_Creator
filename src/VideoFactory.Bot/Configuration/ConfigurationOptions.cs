namespace VideoFactory.Bot.Configuration;

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; init; } = string.Empty;

    public long AllowedUserId { get; init; }
}

public sealed class PathsOptions
{
    public const string SectionName = "Paths";

    public string WorkspaceRoot { get; init; } = ".";
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string DataRoot { get; init; } = "data";
}

public sealed class LimitsOptions
{
    public const string SectionName = "Limits";

    public long MaxInputFileBytes { get; init; } = 524_288_000;

    public int MaxInputDurationSeconds { get; init; } = 1_800;

    public int MaxVariantsPerJob { get; init; } = 10;
}

public sealed class FfmpegOptions
{
    public const string SectionName = "FFmpeg";

    public string FfmpegPath { get; init; } = "ffmpeg";

    public string FfprobePath { get; init; } = "ffprobe";
}

public sealed class WhisperOptions
{
    public const string SectionName = "Whisper";

    public string PythonPath { get; init; } = "python";

    public string WorkerPath { get; init; } = "scripts/whisper-worker.py";
}

public sealed class RemotionOptions
{
    public const string SectionName = "Remotion";

    public string NodePath { get; init; } = "node";

    public string RenderHostPath { get; init; } = "render-host/dist/index.js";
}

public sealed class AiApiOptions
{
    public const string SectionName = "AiApi";

    public string Provider { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;
}
