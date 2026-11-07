using VideoFactory.VideoProduction.Domain.Identifiers;
using VideoFactory.VideoProduction.Domain.Jobs;

namespace VideoFactory.VideoProduction.UnitTests.Jobs;

public sealed class JobRequestTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void VariantCountOutsideProductLimitIsRejected(int variantCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProductionSettings(variantCount, null, captionsEnabled: true, musicEnabled: true));
    }

    [Fact]
    public void EmptyCommentIsRejected()
    {
        ProductionSettings settings = new(1, null, captionsEnabled: true, musicEnabled: false);

        Assert.Throws<ArgumentException>(
            () => new JobRequest(ArtifactId.New(), "  ", ProductionMode.Fast, settings));
    }

    [Fact]
    public void DefaultArtifactIdIsRejected()
    {
        ProductionSettings settings = new(1, null, captionsEnabled: true, musicEnabled: false);

        Assert.Throws<ArgumentException>(
            () => new JobRequest(default, "Make it concise", ProductionMode.Fast, settings));
    }

    [Fact]
    public void ValidRequestPreservesUserIntent()
    {
        ArtifactId sourceAssetId = ArtifactId.New();
        ProductionSettings settings = new(3, 30, captionsEnabled: true, musicEnabled: false);

        JobRequest request = new(sourceAssetId, "Make it concise", ProductionMode.Auto, settings);

        Assert.Equal(sourceAssetId, request.SourceAssetId);
        Assert.Equal("Make it concise", request.Comment);
        Assert.Equal(ProductionMode.Auto, request.RequestedMode);
        Assert.Equal(3, request.Settings.VariantCount);
    }
}
