namespace VideoFactory.VideoProduction.Domain.Identifiers;

public readonly record struct RevisionId
{
    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public RevisionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Revision ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    public static RevisionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
