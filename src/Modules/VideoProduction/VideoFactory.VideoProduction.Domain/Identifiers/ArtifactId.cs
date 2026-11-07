namespace VideoFactory.VideoProduction.Domain.Identifiers;

public readonly record struct ArtifactId
{
    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public ArtifactId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Artifact ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    public static ArtifactId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
