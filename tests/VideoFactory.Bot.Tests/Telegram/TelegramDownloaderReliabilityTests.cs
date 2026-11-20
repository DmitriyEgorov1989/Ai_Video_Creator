using System.Net;
using System.Text;
using System.Security.Cryptography;
using Telegram.Bot;
using VideoFactory.Bot.Telegram;
using VideoFactory.VideoProduction.Application.Media;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Media;
using VideoFactory.VideoProduction.Application.Delivery;
using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.Bot.Tests.Telegram;

public sealed class TelegramDownloaderReliabilityTests
{
    [Fact]
    public async Task DeliverySendsBytesWhoseHashMatchesFinalArtifact()
    {
        string path = Path.GetTempFileName();
        byte[] expected = [0, 1, 2, 255, 8, 13, 21];
        try
        {
            await File.WriteAllBytesAsync(path, expected);
            CaptureUpload transport = new();
            using HttpClient http = new(transport);
            TelegramVideoDelivery delivery = new(new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-token"), http));

            VideoDeliveryResult result = await delivery.SendAsync(
                new VideoDeliveryRequest(JobId.New(), VariantId.New(), path, "Готово", 123456),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            Assert.NotNull(transport.Body);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
                Convert.ToHexString(SHA256.HashData(transport.ExtractFileBytes(expected))).ToLowerInvariant());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DeliveryPreservesTelegramApiErrorCodeAndMessage()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            using HttpClient http = new(new ApiFailure());
            TelegramVideoDelivery delivery = new(new TelegramBotClient(new TelegramBotClientOptions("123456:synthetic-token"), http));
            VideoDeliveryResult result = await delivery.SendAsync(new VideoDeliveryRequest(JobId.New(), VariantId.New(), path, "Готово", 123456), CancellationToken.None);
            Assert.False(result.IsSuccess);
            Assert.Equal("telegram.api_403", result.ErrorCode);
            Assert.Equal("original Telegram diagnostic", result.ErrorMessage);
        }
        finally { File.Delete(path); }
    }

    private sealed class ApiFailure : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            { Content = new StringContent("""{"ok":false,"error_code":403,"description":"original Telegram diagnostic"}""", Encoding.UTF8, "application/json") });
    }

    private sealed class CaptureUpload : HttpMessageHandler
    {
        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsByteArrayAsync(token);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true,"result":{"message_id":42}}""", Encoding.UTF8, "application/json")
            };
        }

        public byte[] ExtractFileBytes(byte[] expected)
        {
            int start = FindSequence(Body!, expected);
            Assert.True(start >= 0, "The Telegram multipart request did not contain the final MP4 bytes.");
            return Body!.AsSpan(start, expected.Length).ToArray();
        }

        private static int FindSequence(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
            }
            return -1;
        }
    }

    [Theory]
    [InlineData("disk", "storage.insufficient_space")]
    [InlineData("network", "RequestException")]
    [InlineData("oversized", "RequestException")]
    [InlineData("spoofed", "media.container_unsupported")]
    public async Task FailedDownloadLeavesNoMediaOrAcceptedFile(string fault, string expectedCode)
    {
        string root = Path.Combine(Path.GetTempPath(), $"telegram-download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Transport transport = new(fault);
            using HttpClient http = new(transport);
            TelegramBotClient bot = new(new TelegramBotClientOptions("123456:synthetic-token"), http);
            TelegramFileDownloader downloader = new(bot, root, new Budget(fault == "disk"), new Probe(),
                new MediaInputProfile { MaxFileBytes = 4 });
            TelegramDownloadResult.Rejected result = Assert.IsType<TelegramDownloadResult.Rejected>(
                await downloader.DownloadAsync(new TelegramMediaInput("file", "unique", 3, "video/mp4", "video.mp4"), CancellationToken.None));
            Assert.Equal(expectedCode, result.Code);
            Assert.DoesNotContain("synthetic-token", result.Message);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            if (fault == "disk") Assert.Equal(0, transport.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Budget(bool reject) : IStorageBudget
    {
        public Task<StorageBudgetFailure?> CheckAsync(string path, long bytes, CancellationToken token) =>
            Task.FromResult<StorageBudgetFailure?>(reject ? new("storage.insufficient_space", "No space.") : null);
    }

    private sealed class Probe : IMediaProbe
    {
        public Task<MediaProbeResult> ProbeAsync(ProbeRequest request, CancellationToken token) =>
            Task.FromResult(MediaProbeResult.Succeeded(new MediaMetadata("matroska", 1_000_000,
                [new VideoStreamMetadata(0, "h264", 1920, 1080, null, false, 0)], [])));
    }

    private sealed class Transport(string fault) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/getFile", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"ok":true,"result":{"file_id":"file","file_unique_id":"unique","file_size":3,"file_path":"videos/file.mp4"}}""", Encoding.UTF8, "application/json")
                });
            if (fault == "network") throw new HttpRequestException("synthetic-token private-comment private-transcript");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(fault == "oversized" ? new byte[5] : new byte[3]) });
        }
    }
}
