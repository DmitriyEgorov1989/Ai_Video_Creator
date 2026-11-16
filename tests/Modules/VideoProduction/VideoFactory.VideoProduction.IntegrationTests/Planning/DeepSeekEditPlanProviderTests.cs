using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using VideoFactory.VideoProduction.Application.Planning;
using VideoFactory.VideoProduction.Domain.Editing;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;
using VideoFactory.VideoProduction.Infrastructure.Planning;

namespace VideoFactory.VideoProduction.IntegrationTests.Planning;

public sealed class DeepSeekEditPlanProviderTests
{
    [Fact]
    public async Task MapsStructuredResponseAndSendsOnlyPlanningData()
    {
        SegmentId segmentId = SegmentId.New();
        CapturingHandler handler = new(JsonResponse(segmentId));
        using HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://api.deepseek.test/")
        };
        DeepSeekEditPlanProvider provider = new(
            client,
            new DeepSeekProviderOptions
            {
                ApiKey = "test-key",
                Model = "deepseek-flash",
                InputCacheHitPriceUsdPerMillionTokens = 0.003m,
                InputCacheMissPriceUsdPerMillionTokens = 0.15m,
                OutputPriceUsdPerMillionTokens = 0.6m
            });

        ProviderPlanResponse response = await provider.CreatePlanAsync(
            new ProviderPlanningRequest(
                "Сделай динамично",
                EditTempo.Dynamic,
                2,
                true,
                [new ProviderSegment(segmentId, 2_000_000)],
                "Это расшифрованная речь.",
                0),
            CancellationToken.None);

        Assert.Equal(1, response.SchemaVersion);
        Assert.Equal(segmentId, Assert.Single(response.VideoClips).SegmentId);
        Assert.Equal(0.000002406m, response.ActualCostUsd);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("Bearer test-key", handler.Request.Headers.Authorization!.ToString());
        Assert.Contains("/chat/completions", handler.Request.RequestUri!.ToString());
        Assert.DoesNotContain("/var/", handler.Body, StringComparison.Ordinal);
        using JsonDocument request = JsonDocument.Parse(handler.Body);
        string userContent = request.RootElement
            .GetProperty("messages")[1]
            .GetProperty("content")
            .GetString()!;
        using JsonDocument userData = JsonDocument.Parse(userContent);
        Assert.Equal("Сделай динамично", userData.RootElement.GetProperty("comment").GetString());
        Assert.Equal("Это расшифрованная речь.", userData.RootElement.GetProperty("extractedText").GetString());
        Assert.Equal(segmentId.ToString(), userData.RootElement
            .GetProperty("allowedSegments")[0]
            .GetProperty("id")
            .GetString());
        Assert.Equal("json_object", request.RootElement
            .GetProperty("response_format")
            .GetProperty("type")
            .GetString());
    }

    [Fact]
    public async Task ConvertsHttpFailureToExpectedProviderDiagnosticWithoutLeakingKey()
    {
        CapturingHandler handler = new("upstream diagnostic", HttpStatusCode.TooManyRequests, raw: true);
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.deepseek.test/") };
        DeepSeekEditPlanProvider provider = new(client, new DeepSeekProviderOptions { ApiKey = "secret-key" });

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CreatePlanAsync(
            new ProviderPlanningRequest(null, EditTempo.Dynamic, null, false, []),
            CancellationToken.None));

        Assert.Contains("429", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMalformedStructuredResponse()
    {
        CapturingHandler handler = new("{invalid", HttpStatusCode.OK, raw: true);
        using HttpClient client = new(handler) { BaseAddress = new Uri("https://api.deepseek.test/") };
        DeepSeekEditPlanProvider provider = new(client, new DeepSeekProviderOptions { ApiKey = "test-key" });

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CreatePlanAsync(
            new ProviderPlanningRequest(null, EditTempo.Dynamic, null, false, []),
            CancellationToken.None));

        Assert.Contains("invalid JSON", exception.Message, StringComparison.Ordinal);
    }

    private static string JsonResponse(SegmentId segmentId) => JsonSerializer.Serialize(new
    {
        id = "test-completion",
        model = "deepseek-flash",
        choices = new[]
        {
            new
            {
                index = 0,
                finish_reason = "stop",
                message = new
                {
                    role = "assistant",
                    content = JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        seed = 7,
                        videoClips = new[]
                        {
                            new
                            {
                                segmentId = segmentId.ToString(),
                                output = new { startFrame = 0, endFrame = 60 },
                                crop = "Fit"
                            }
                        },
                        captions = Array.Empty<object>()
                    })
                }
            }
        },
        usage = new
        {
            prompt_tokens = 10,
            completion_tokens = 2,
            total_tokens = 12,
            prompt_cache_hit_tokens = 2,
            prompt_cache_miss_tokens = 8
        }
    });

    private sealed class CapturingHandler(
        string response,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        bool raw = false) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                StatusCode = statusCode,
                Content = raw
                    ? new StringContent(response)
                    : JsonContent.Create(JsonDocument.Parse(response).RootElement)
            };
        }
    }
}
