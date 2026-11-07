using VideoFactory.VideoProduction.Domain.Identifiers;

namespace VideoFactory.VideoProduction.UnitTests.Identifiers;

public sealed class IdentifierTests
{
    [Fact]
    public void EmptyValuesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new JobId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new VariantId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new ArtifactId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new RevisionId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SegmentId(Guid.Empty));
    }

    [Fact]
    public void EqualValuesProduceEqualIdentifiers()
    {
        Guid value = Guid.NewGuid();

        Assert.Equal(new JobId(value), new JobId(value));
    }

    [Fact]
    public void StringRepresentationUsesCompactGuidFormat()
    {
        Guid value = Guid.Parse("b2c6d8aa-3fca-4e7e-a583-35a54a4df412");

        Assert.Equal("b2c6d8aa3fca4e7ea58335a54a4df412", new JobId(value).ToString());
    }
}
