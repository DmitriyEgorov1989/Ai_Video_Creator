using Microsoft.Extensions.Options;

namespace VideoFactory.Bot.Configuration;

public static class ConfigurationRegistration
{
    public static IServiceCollection AddVideoFactoryConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        bool validateOnStart = true)
    {
        OptionsBuilder<TelegramOptions> telegram = services.AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.BotToken),
                "Telegram:BotToken is required.")
            .Validate(options => options.AllowedUserId > 0,
                "Telegram:AllowedUserId must be greater than zero.");

        OptionsBuilder<StorageOptions> storage = services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.DataRoot),
                "Storage:DataRoot is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.DatabaseFileName),
                "Storage:DatabaseFileName is required.")
            .Validate(options => !Path.IsPathRooted(options.DatabaseFileName)
                && options.DatabaseFileName is not "." and not ".."
                && !options.DatabaseFileName.Contains(Path.DirectorySeparatorChar)
                && !options.DatabaseFileName.Contains(Path.AltDirectorySeparatorChar),
                "Storage:DatabaseFileName must be a file name without a directory.");

        OptionsBuilder<PathsOptions> paths = services.AddOptions<PathsOptions>()
            .Bind(configuration.GetSection(PathsOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.WorkspaceRoot),
                "Paths:WorkspaceRoot is required.");

        OptionsBuilder<LimitsOptions> limits = services.AddOptions<LimitsOptions>()
            .Bind(configuration.GetSection(LimitsOptions.SectionName))
            .Validate(options => options.MaxInputFileBytes > 0,
                "Limits:MaxInputFileBytes must be greater than zero.")
            .Validate(options => options.MaxInputDurationSeconds > 0,
                "Limits:MaxInputDurationSeconds must be greater than zero.");

        OptionsBuilder<FfmpegOptions> ffmpeg = services.AddOptions<FfmpegOptions>()
            .Bind(configuration.GetSection(FfmpegOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.FfmpegPath),
                "FFmpeg:FfmpegPath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.FfprobePath),
                "FFmpeg:FfprobePath is required.");

        OptionsBuilder<WhisperOptions> whisper = services.AddOptions<WhisperOptions>()
            .Bind(configuration.GetSection(WhisperOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.PythonPath),
                "Whisper:PythonPath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.WorkerPath),
                "Whisper:WorkerPath is required.");

        OptionsBuilder<RemotionOptions> remotion = services.AddOptions<RemotionOptions>()
            .Bind(configuration.GetSection(RemotionOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.NodePath),
                "Remotion:NodePath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.RenderHostPath),
                "Remotion:RenderHostPath is required.");

        OptionsBuilder<AiApiOptions> aiApi = services.AddOptions<AiApiOptions>()
            .Bind(configuration.GetSection(AiApiOptions.SectionName))
            .Validate(IsAiApiConfigurationComplete,
                "AiApi settings must be either empty or contain Provider, ApiKey, and Model.");

        if (validateOnStart)
        {
            telegram.ValidateOnStart();
            paths.ValidateOnStart();
            storage.ValidateOnStart();
            limits.ValidateOnStart();
            ffmpeg.ValidateOnStart();
            whisper.ValidateOnStart();
            remotion.ValidateOnStart();
            aiApi.ValidateOnStart();
        }

        return services;
    }

    private static bool IsAiApiConfigurationComplete(AiApiOptions options)
    {
        bool anyValuePresent = !string.IsNullOrWhiteSpace(options.Provider)
            || !string.IsNullOrWhiteSpace(options.ApiKey)
            || !string.IsNullOrWhiteSpace(options.Model);

        return !anyValuePresent
            || (!string.IsNullOrWhiteSpace(options.Provider)
                && !string.IsNullOrWhiteSpace(options.ApiKey)
                && !string.IsNullOrWhiteSpace(options.Model));
    }
}
