namespace VideoFactory.VideoProduction.Domain.Identifiers;

public readonly record struct JobId
{
    public Guid Value { get; }

    public bool IsEmpty => Value == Guid.Empty;

    public JobId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Job ID cannot be empty.", nameof(value));
        }
        Value = value;
    }

    public static JobId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
