using VideoFactory.VideoProduction.Application.Storage;
using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Infrastructure.Storage;
using VideoFactory.VideoProduction.IntegrationTests.Storage;

namespace VideoFactory.VideoProduction.IntegrationTests.Reliability;

public sealed class ArtifactPathTests
{
    [Theory]
    [InlineData("../outside.mp4")]
    [InlineData("jobs/../../outside.mp4")]
    [InlineData("C:\\outside.mp4")]
    [InlineData("/outside.mp4")]
    [InlineData("jobs/video.mp4:stream")]
    [InlineData("jobs/unsafe./video.mp4")]
    [InlineData("jobs/unsafe /video.mp4")]
    public async Task RejectsTraversalAbsoluteAndAmbiguousPaths(string path)
    {
        await using TemporarySqliteDatabase database = await TemporarySqliteDatabase.CreateAsync();
        IArtifactStore artifacts = new SqliteArtifactStore(database.ConnectionString);
        ArtifactRegistrationResult result = await artifacts.RegisterAsync(
            new ArtifactRecord(ArtifactId.New(), "OriginalVideo", path, null, 3, null, null, "test"), CancellationToken.None);
        Assert.IsType<ArtifactRegistrationResult.Rejected>(result);
    }
}
