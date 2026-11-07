namespace VideoFactory.VideoProduction.Domain.Identifiers;

public readonly record struct VariantId
{
    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public VariantId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Variant ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    public static VariantId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
