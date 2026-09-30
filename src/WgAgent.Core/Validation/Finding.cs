namespace WgAgent.Core.Validation;

public enum Severity { Error, Warning }

/// <summary>One validation finding: an error blocks the write (REQ-VAL-001), a warning does not
/// (REQ-VAL-002). Either carries a reason code from the closed set.</summary>
public sealed record Finding(string Code, string Message, Severity Severity)
{
    public static Finding Error(string code, string message) => new(code, message, Severity.Error);
    public static Finding Warn(string code, string message) => new(code, message, Severity.Warning);
}

/// <summary>What validation reports: at most one error — the first rule to fail — and every warning.</summary>
public sealed record ValidationResult(Finding? Error, IReadOnlyList<Finding> Warnings)
{
    public bool IsValid => Error is null;
}
