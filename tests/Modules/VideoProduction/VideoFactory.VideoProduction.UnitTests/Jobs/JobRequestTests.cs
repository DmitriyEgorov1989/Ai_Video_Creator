using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.UnitTests.Jobs;

public sealed class JobRequestTests
{
    [Fact]
    public void OrderedSourcesAndRequestedStyleArePreserved()
    {
        ArtifactId first = ArtifactId.New();
        ArtifactId second = ArtifactId.New();
        ProductionSettings settings = new(
            30,
            captionsEnabled: true,
            musicEnabled: false,
            EditTempo.Dynamic,
            RequestedReelsStyle.Cinematic);

        JobRequest request = new(
            [first, second],
            "Make it concise",
            ProductionMode.Fast,
            settings);

        Assert.Equal([first, second], request.SourceAssetIds.ToArray());
        Assert.Equal(first, request.PrimarySourceAssetId);
        Assert.Equal(RequestedReelsStyle.Cinematic, request.Settings.RequestedStyle);
    }

    [Fact]
    public void DuplicateSourcesAreRejected()
    {
        ArtifactId source = ArtifactId.New();

        Assert.Throws<ArgumentException>(() => new JobRequest(
            [source, source],
            "Make it concise",
            ProductionMode.Fast,
            new ProductionSettings(null, true, false)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void SourceCountOutsideOneToFiveIsRejected(int sourceCount)
    {
        ArtifactId[] sources = Enumerable.Range(0, sourceCount)
            .Select(_ => ArtifactId.New())
            .ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => new JobRequest(
            sources,
            "Make it concise",
            ProductionMode.Fast,
            new ProductionSettings(null, true, false)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTargetDurationIsRejected(int targetDurationSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProductionSettings(targetDurationSeconds, captionsEnabled: true, musicEnabled: true));
    }

    [Fact]
    public void EmptyCommentIsRejected()
    {
        ProductionSettings settings = new(null, captionsEnabled: true, musicEnabled: false);

        Assert.Throws<ArgumentException>(
            () => new JobRequest(ArtifactId.New(), "  ", ProductionMode.Fast, settings));
    }

    [Fact]
    public void DefaultArtifactIdIsRejected()
    {
        ProductionSettings settings = new(null, captionsEnabled: true, musicEnabled: false);

        Assert.Throws<ArgumentException>(
            () => new JobRequest(default(ArtifactId), "Make it concise", ProductionMode.Fast, settings));
    }

    [Fact]
    public void ValidRequestPreservesUserIntent()
    {
        ArtifactId sourceAssetId = ArtifactId.New();
        ProductionSettings settings = new(30, captionsEnabled: true, musicEnabled: false);

        JobRequest request = new(sourceAssetId, "Make it concise", ProductionMode.Auto, settings);

        Assert.Equal(sourceAssetId, request.SourceAssetId);
        Assert.Equal("Make it concise", request.Comment);
        Assert.Equal(ProductionMode.Auto, request.RequestedMode);
        Assert.Equal(30, request.Settings.TargetDurationSeconds);
        Assert.True(request.Settings.CaptionsEnabled);
        Assert.False(request.Settings.MusicEnabled);
    }
}
