namespace VideoFactory.VideoProduction.Domain.Validation;

public sealed record ValidationIssue(string Code, string Path, string Message);
