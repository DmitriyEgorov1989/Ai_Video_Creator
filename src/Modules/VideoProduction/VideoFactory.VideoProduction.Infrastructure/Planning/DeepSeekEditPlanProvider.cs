using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Timeline;

namespace VideoFactory.VideoProduction.Infrastructure.Planning;

/// <summary>
/// Параметры подключения к DeepSeek и расчёта стоимости вызова.
/// </summary>
public sealed class DeepSeekProviderOptions
{
    /// <summary>Ключ API; значение не должно быть <see langword="null"/>.</summary>
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Базовый URL DeepSeek API.</summary>
    public Uri BaseAddress { get; init; } = new("https://api.deepseek.com/");

    /// <summary>Имя модели DeepSeek.</summary>
    public string Model { get; init; } = "deepseek-flash";

    /// <summary>Цена входных токенов из кэша за один миллион токенов в USD.</summary>
    public decimal InputCacheHitPriceUsdPerMillionTokens { get; init; }

    /// <summary>Цена входных токенов без кэша за один миллион токенов в USD.</summary>
    public decimal InputCacheMissPriceUsdPerMillionTokens { get; init; }

    /// <summary>Цена выходных токенов за один миллион токенов в USD.</summary>
    public decimal OutputPriceUsdPerMillionTokens { get; init; }

    /// <summary>Максимальное число токенов в ответе.</summary>
    public int MaxOutputTokens { get; init; } = 2_048;

    /// <summary>Предельная длительность HTTP-вызова в секундах.</summary>
    public int TimeoutSeconds { get; init; } = 120;
}

/// <summary>
/// Реализует нейтральный application-контракт планирования через DeepSeek Chat Completions API.
/// </summary>
public sealed class DeepSeekEditPlanProvider : IEditPlanProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient httpClient;
    private readonly DeepSeekProviderOptions options;

    /// <summary>Создаёт DeepSeek provider.</summary>
    public DeepSeekEditPlanProvider(HttpClient httpClient, DeepSeekProviderOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("DeepSeek API key is required.", nameof(options));
        }

        if (options.TimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "DeepSeek timeout must be greater than zero.");
        }

        httpClient.BaseAddress ??= options.BaseAddress;
        httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    /// <inheritdoc />
    public Task<PlanningCostEstimate> EstimateCostAsync(
        ProviderPlanningRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string requestText = BuildUserData(request);
        int inputTokens = checked((int)Math.Ceiling(requestText.Length / 4m));
        int estimatedOutputTokens = options.MaxOutputTokens;
        decimal estimate = DeepSeekCostCalculator.Calculate(
            inputCacheHitTokens: 0,
            inputCacheMissTokens: inputTokens,
            outputTokens: estimatedOutputTokens,
            options);

        return Task.FromResult(new PlanningCostEstimate(
            estimate,
            "deepseek",
            options.Model));
    }

    /// <inheritdoc />
    public async Task<ProviderPlanResponse> CreatePlanAsync(
        ProviderPlanningRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        DeepSeekRequest payload = new(
            options.Model,
            [
                new DeepSeekMessage("system", BuildSystemInstruction()),
                new DeepSeekMessage("user", BuildUserData(request))
            ],
            new DeepSeekResponseFormat("json_object"),
            options.MaxOutputTokens,
            false);

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        using HttpResponseMessage response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string diagnostic = SanitizeDiagnostic(responseBody);
            throw new HttpRequestException(
                $"DeepSeek API returned HTTP {(int)response.StatusCode} ({response.StatusCode}). " +
                $"Provider diagnostic: {diagnostic}",
                null,
                response.StatusCode);
        }

        DeepSeekResponse providerResponse;
        try
        {
            providerResponse = JsonSerializer.Deserialize<DeepSeekResponse>(responseBody, JsonOptions)
                ?? throw new JsonException("Response body is empty.");
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException(
                "DeepSeek returned an invalid JSON response.",
                exception);
        }

        DeepSeekChoice? choice = providerResponse.Choices?.FirstOrDefault();
        decimal actualCostUsd = DeepSeekCostCalculator.Calculate(
            providerResponse.Usage?.PromptCacheHitTokens ?? 0,
            providerResponse.Usage?.PromptCacheMissTokens
                ?? providerResponse.Usage?.PromptTokens
                ?? 0,
            providerResponse.Usage?.CompletionTokens ?? 0,
            options);
        if (choice?.FinishReason is not "stop")
        {
            throw new PlanningProviderException(
                $"DeepSeek stopped before a complete planning response (finish_reason: {choice?.FinishReason ?? "missing"}).",
                actualCostUsd);
        }

        string? content = choice.Message.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new PlanningProviderException(
                "DeepSeek returned an empty structured planning response.",
                actualCostUsd);
        }

        DeepSeekPlanDocument plan;
        try
        {
            plan = JsonSerializer.Deserialize<DeepSeekPlanDocument>(content, JsonOptions)
                ?? throw new JsonException("Structured plan is empty.");
        }
        catch (JsonException exception)
        {
            throw new PlanningProviderException(
                "DeepSeek returned an invalid structured planning response.",
                actualCostUsd,
                exception);
        }

        try
        {
            return new ProviderPlanResponse(
                plan.SchemaVersion,
                plan.Seed,
                plan.VideoClips.Select(MapVideoClip).ToImmutableArray(),
                plan.Captions.Select(caption => new ProviderCaption(
                    caption.Text,
                    new OutputRange(caption.Output.StartFrame, caption.Output.EndFrame)))
                    .ToImmutableArray(),
                actualCostUsd);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new PlanningProviderException(
                "DeepSeek returned a structured plan with invalid values.",
                actualCostUsd,
                exception);
        }
    }

    private static ProviderVideoClip MapVideoClip(DeepSeekVideoClip clip)
    {
        if (!Guid.TryParse(clip.SegmentId, out Guid segmentGuid))
        {
            throw new FormatException("The provider returned an invalid segment ID.");
        }

        if (!Enum.TryParse(clip.Crop, ignoreCase: true, out CropPreset crop))
        {
            throw new FormatException("The provider returned an unsupported crop preset.");
        }

        return new ProviderVideoClip(
            new SegmentId(segmentGuid),
            new OutputRange(clip.Output.StartFrame, clip.Output.EndFrame),
            crop);
    }

    private static string BuildSystemInstruction() =>
        "Return JSON only. Select video clips only from the supplied allowedSegments. " +
        "Do not invent files, assets, segment IDs, timestamps, templates, or operations. " +
        "Use schemaVersion 1, crop Fit or CenterCrop, and output frame ranges. " +
        "The response must contain schemaVersion, seed, videoClips, and captions.";

    private static string BuildUserData(ProviderPlanningRequest request) =>
        JsonSerializer.Serialize(new
        {
            comment = request.Comment,
            extractedText = request.ExtractedText,
            tempo = request.Tempo.ToString(),
            targetDurationSeconds = request.TargetDurationSeconds,
            captionsEnabled = request.CaptionsEnabled,
            allowedSegments = request.AllowedSegments.Select(segment => new
            {
                id = segment.Id.ToString(),
                durationMicroseconds = segment.DurationMicroseconds
            }),
            repairAttempt = request.RepairAttempt
        }, JsonOptions);

    private string SanitizeDiagnostic(string diagnostic)
    {
        string withoutKey = diagnostic.Replace(options.ApiKey, "[redacted]", StringComparison.Ordinal);
        return withoutKey.Length <= 4_096 ? withoutKey : withoutKey[..4_096];
    }

    private sealed record DeepSeekRequest(
        [property: JsonPropertyName("model")]
        string Model,
        [property: JsonPropertyName("messages")]
        IReadOnlyList<DeepSeekMessage> Messages,
        [property: JsonPropertyName("response_format")]
        DeepSeekResponseFormat ResponseFormat,
        [property: JsonPropertyName("max_tokens")]
        int MaxTokens,
        [property: JsonPropertyName("stream")]
        bool Stream);

    private sealed record DeepSeekMessage(string Role, string Content);

    private sealed record DeepSeekResponseFormat(string Type);

    private sealed record DeepSeekResponse(
        IReadOnlyList<DeepSeekChoice>? Choices,
        DeepSeekUsage? Usage);

    private sealed record DeepSeekChoice(
        DeepSeekMessage Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed class DeepSeekUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public int PromptTokens { get; init; }

        [JsonPropertyName("completion_tokens")]
        public int CompletionTokens { get; init; }

        [JsonPropertyName("prompt_cache_hit_tokens")]
        public int PromptCacheHitTokens { get; init; }

        [JsonPropertyName("prompt_cache_miss_tokens")]
        public int PromptCacheMissTokens { get; init; }
    }

    private sealed record DeepSeekPlanDocument(
        int SchemaVersion,
        int Seed,
        IReadOnlyList<DeepSeekVideoClip> VideoClips,
        IReadOnlyList<DeepSeekCaption> Captions);

    private sealed record DeepSeekVideoClip(
        string SegmentId,
        DeepSeekOutputRange Output,
        string Crop);

    private sealed record DeepSeekCaption(string Text, DeepSeekOutputRange Output);

    private sealed record DeepSeekOutputRange(int StartFrame, int EndFrame);
}

/// <summary>Выполняет расчёт стоимости DeepSeek в долларах США.</summary>
public static class DeepSeekCostCalculator
{
    /// <summary>Рассчитывает стоимость использованных токенов.</summary>
    public static decimal Calculate(
        int inputCacheHitTokens,
        int inputCacheMissTokens,
        int outputTokens,
        DeepSeekProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (inputCacheHitTokens < 0 || inputCacheMissTokens < 0 || outputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputCacheHitTokens));
        }

        return inputCacheHitTokens * options.InputCacheHitPriceUsdPerMillionTokens / 1_000_000m
            + inputCacheMissTokens * options.InputCacheMissPriceUsdPerMillionTokens / 1_000_000m
            + outputTokens * options.OutputPriceUsdPerMillionTokens / 1_000_000m;
    }
}
