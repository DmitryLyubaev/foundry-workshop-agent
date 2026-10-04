namespace Workshop.Core;

/// <summary>
/// The outcome of a write. A refusal always carries a message for the person; a success may carry
/// one too, when something went differently from what was asked (a part going on order).
/// </summary>
public sealed record RuleResult
{
    private RuleResult(bool ok, string? message)
    {
        Ok = ok;
        Message = message;
    }

    public bool Ok { get; }

    public string? Message { get; }

    public static RuleResult Success { get; } = new(true, null);

    public static RuleResult SuccessWith(string message) => new(true, message);

    public static RuleResult Fail(string message) => new(false, message);
}
