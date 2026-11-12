using System.Security.Cryptography;
using System.Text;
using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Infrastructure.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Storage;

public sealed class LocalArtifactStorageTests
{
    [Fact]
    public async Task RepeatedPublicationReturnsExistingFileWithoutReplacingIt()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), $"video-factory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            JobId jobId = JobId.New();
            ArtifactId artifactId = ArtifactId.New();
            byte[] content = Encoding.UTF8.GetBytes("immutable artifact");
            LocalArtifactStorage storage = new(workspaceRoot);

            await using MemoryStream firstStream = new(content);
            ArtifactPublishResult.Succeeded first = Assert.IsType<ArtifactPublishResult.Succeeded>(
                await storage.PublishAsync(jobId, artifactId, firstStream, CancellationToken.None));
            await using MemoryStream secondStream = new(content);
            ArtifactPublishResult.Succeeded second = Assert.IsType<ArtifactPublishResult.Succeeded>(
                await storage.PublishAsync(jobId, artifactId, secondStream, CancellationToken.None));

            Assert.True(first.WasPublished);
            Assert.False(second.WasPublished);
            Assert.Equal(first.File, second.File);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedPublicationWithDifferentContentIsRejected()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), $"video-factory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            LocalArtifactStorage storage = new(workspaceRoot);
            JobId jobId = JobId.New();
            ArtifactId artifactId = ArtifactId.New();
            await using MemoryStream firstStream = new(Encoding.UTF8.GetBytes("first"));
            await storage.PublishAsync(jobId, artifactId, firstStream, CancellationToken.None);
            await using MemoryStream secondStream = new(Encoding.UTF8.GetBytes("second"));

            ArtifactPublishResult result = await storage.PublishAsync(
                jobId,
                artifactId,
                secondStream,
                CancellationToken.None);

            ArtifactPublishResult.Rejected rejected = Assert.IsType<ArtifactPublishResult.Rejected>(result);
            Assert.Contains("different content", rejected.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PublishesArtifactWithGeneratedPathHashAndSize()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), $"video-factory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            JobId jobId = JobId.New();
            ArtifactId artifactId = ArtifactId.New();
            byte[] content = Encoding.UTF8.GetBytes("immutable artifact");
            LocalArtifactStorage storage = new(workspaceRoot);

            await using MemoryStream stream = new(content);
            ArtifactPublishResult result = await storage.PublishAsync(
                jobId,
                artifactId,
                stream,
                CancellationToken.None);

            ArtifactPublishResult.Succeeded published = Assert.IsType<ArtifactPublishResult.Succeeded>(result);
            string expectedHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            string absolutePath = Path.Combine(workspaceRoot, published.File.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(published.WasPublished);
            Assert.Equal(artifactId, published.File.ArtifactId);
            Assert.Equal(expectedHash, published.File.Sha256);
            Assert.Equal(content.Length, published.File.SizeBytes);
            Assert.True(File.Exists(absolutePath));
            Assert.StartsWith($"jobs/{jobId}/", published.File.RelativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("..", published.File.RelativePath, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(absolutePath)!, "*.tmp"));
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }
}
