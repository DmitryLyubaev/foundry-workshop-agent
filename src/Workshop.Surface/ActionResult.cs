namespace Workshop.Surface;

/// <summary>
/// What an action did: its <see cref="Outcome"/> (one of <see cref="Outcomes"/>), the app's message
/// if it has one, and the current screen after the action.
/// </summary>
public sealed record ActionResult(string Outcome, string? Message, ScreenDescription Screen);

public static class Outcomes
{
    public const string Ok = "ok";
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Disabled = "disabled";
}
