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
                "Telegram:AllowedUserId must be greater than zero.")
            .Validate(options => options.ProgressMessageIntervalSeconds > 0,
                "Telegram:ProgressMessageIntervalSeconds must be greater than zero.");

        OptionsBuilder<StorageOptions> storage = services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(options => options.MinimumFreeBytes > 0, "Storage:MinimumFreeBytes must be greater than zero.")
            .Validate(options => options.JobQuotaBytes > 0, "Storage:JobQuotaBytes must be greater than zero.")
            .Validate(options => options.CompletedJobRetentionDays is > 0 and <= 36500,
                "Storage:CompletedJobRetentionDays must be between 1 and 36500 days.")
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

        OptionsBuilder<WorkerOptions> worker = services.AddOptions<WorkerOptions>()
            .Bind(configuration.GetSection(WorkerOptions.SectionName))
            .Validate(options => options.HeartbeatIntervalSeconds > 0,
                "Worker:HeartbeatIntervalSeconds must be greater than zero.")
            .Validate(options => options.LeaseTimeoutSeconds >= options.HeartbeatIntervalSeconds * 3,
                "Worker:LeaseTimeoutSeconds must be at least three heartbeat intervals.")
            .Validate(options => options.MaxStageAttempts > 0,
                "Worker:MaxStageAttempts must be greater than zero.")
            .Validate(options => options.PollIntervalSeconds > 0,
                "Worker:PollIntervalSeconds must be greater than zero.");

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

        OptionsBuilder<AudioNormalizationOptions> audio = services
            .AddOptions<AudioNormalizationOptions>()
            .Bind(configuration.GetSection(AudioNormalizationOptions.SectionName))
            .Validate(options => options.SampleRate > 0,
                "AudioNormalization:SampleRate must be greater than zero.")
            .Validate(options => options.ChannelLayout is "mono" or "stereo",
                "AudioNormalization:ChannelLayout must be mono or stereo.")
            .Validate(options => options.LoudnessLufs <= 0,
                "AudioNormalization:LoudnessLufs must not be positive.")
            .Validate(options => options.TruePeakDbtp <= 0,
                "AudioNormalization:TruePeakDbtp must not be positive.");

        OptionsBuilder<OutputQualityOptions> outputQuality = services
            .AddOptions<OutputQualityOptions>()
            .Bind(configuration.GetSection(OutputQualityOptions.SectionName))
            .Validate(options => options.ExpectedWidth > 0,
                "OutputQuality:ExpectedWidth must be greater than zero.")
            .Validate(options => options.ExpectedHeight > 0,
                "OutputQuality:ExpectedHeight must be greater than zero.")
            .Validate(options => options.ExpectedFrameRateNumerator > 0,
                "OutputQuality:ExpectedFrameRateNumerator must be greater than zero.")
            .Validate(options => options.ExpectedFrameRateDenominator > 0,
                "OutputQuality:ExpectedFrameRateDenominator must be greater than zero.")
            .Validate(options => options.DurationToleranceFrames >= 0,
                "OutputQuality:DurationToleranceFrames must not be negative.")
            .Validate(options => options.MaxTelegramBytes > 0,
                "OutputQuality:MaxTelegramBytes must be greater than zero.")
            .Validate(options => options.VideoBitrateBitsPerSecond > 0,
                "OutputQuality:VideoBitrateBitsPerSecond must be greater than zero.")
            .Validate(options => options.AudioBitrateBitsPerSecond > 0,
                "OutputQuality:AudioBitrateBitsPerSecond must be greater than zero.")
            .Validate(options => options.ContainerReserveBytes >= 0,
                "OutputQuality:ContainerReserveBytes must not be negative.")
            .Validate(options => options.MaxDeliveryEncodeAttempts is >= 0 and <= 1,
                "OutputQuality:MaxDeliveryEncodeAttempts must be zero or one.")
            .Validate(options => options.DeliveryEncodeVideoBitrateBitsPerSecond > 0,
                "OutputQuality:DeliveryEncodeVideoBitrateBitsPerSecond must be greater than zero.")
            .Validate(options => options.DeliveryEncodeAudioBitrateBitsPerSecond > 0,
                "OutputQuality:DeliveryEncodeAudioBitrateBitsPerSecond must be greater than zero.");

        OptionsBuilder<AiApiOptions> aiApi = services.AddOptions<AiApiOptions>()
            .Bind(configuration.GetSection(AiApiOptions.SectionName))
            .Validate(IsAiApiConfigurationComplete,
                "AiApi settings must be either empty or contain Provider, ApiKey, and Model.")
            .Validate(options => string.IsNullOrWhiteSpace(options.Provider)
                || string.Equals(options.Provider, "deepseek", StringComparison.OrdinalIgnoreCase),
                "AiApi:Provider must be 'deepseek'.")
            .Validate(options => Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out Uri? address)
                && address.Scheme == Uri.UriSchemeHttps,
                "AiApi:BaseAddress must be an absolute HTTPS URL.")
            .Validate(options => options.InputCacheHitPriceUsdPerMillionTokens >= 0
                && options.InputCacheMissPriceUsdPerMillionTokens >= 0
                && options.OutputPriceUsdPerMillionTokens >= 0,
                "AiApi token prices must not be negative.")
            .Validate(options => options.MaxOutputTokens > 0,
                "AiApi:MaxOutputTokens must be greater than zero.")
             .Validate(options => options.TimeoutSeconds > 0,
                 "AiApi:TimeoutSeconds must be greater than zero.");

        OptionsBuilder<PlanningOptions> planning = services.AddOptions<PlanningOptions>()
            .Bind(configuration.GetSection(PlanningOptions.SectionName))
            .Validate(options => options.JobLimitUsd >= 0,
                "Planning:JobLimitUsd must not be negative.")
            .Validate(options => options.JobLimitUsd <= 0.10m,
                "Planning:JobLimitUsd must not exceed 0.10 USD.");

        OptionsBuilder<CriticOptions> critic = services.AddOptions<CriticOptions>()
            .Bind(configuration.GetSection(CriticOptions.SectionName))
            .Validate(options => options.MaxRemounts >= 0,
                "Critic:MaxRemounts must not be negative.")
            .Validate(options => options.BlackMinimumDurationMilliseconds > 0,
                "Critic:BlackMinimumDurationMilliseconds must be greater than zero.")
            .Validate(options => options.FreezeMinimumDurationMilliseconds > 0,
                "Critic:FreezeMinimumDurationMilliseconds must be greater than zero.");

        OptionsBuilder<ProcessTimeoutOptions> processTimeouts = services
            .AddOptions<ProcessTimeoutOptions>()
            .Bind(configuration.GetSection(ProcessTimeoutOptions.SectionName))
            .Validate(options => options.ProbeSeconds > 0,
                "ProcessTimeouts:ProbeSeconds must be greater than zero.")
            .Validate(options => options.DecodeSeconds > 0,
                "ProcessTimeouts:DecodeSeconds must be greater than zero.")
            .Validate(options => options.NormalizeSeconds > 0,
                "ProcessTimeouts:NormalizeSeconds must be greater than zero.")
            .Validate(options => options.RenderSeconds > 0,
                "ProcessTimeouts:RenderSeconds must be greater than zero.")
            .Validate(options => options.MaxOutputBytes > 0,
                "ProcessTimeouts:MaxOutputBytes must be greater than zero.");

        if (validateOnStart)
        {
            telegram.ValidateOnStart();
            paths.ValidateOnStart();
            storage.ValidateOnStart();
            limits.ValidateOnStart();
            worker.ValidateOnStart();
            ffmpeg.ValidateOnStart();
            whisper.ValidateOnStart();
            remotion.ValidateOnStart();
            audio.ValidateOnStart();
            outputQuality.ValidateOnStart();
            aiApi.ValidateOnStart();
            planning.ValidateOnStart();
            critic.ValidateOnStart();
            processTimeouts.ValidateOnStart();
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
