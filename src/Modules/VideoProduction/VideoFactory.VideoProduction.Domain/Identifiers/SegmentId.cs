namespace VideoFactory.VideoProduction.Domain.Identifiers;

public readonly record struct SegmentId
{
    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public SegmentId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Segment ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    public static SegmentId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
