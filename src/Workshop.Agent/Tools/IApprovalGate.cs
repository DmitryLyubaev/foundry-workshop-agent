namespace Workshop.Agent.Tools;

/// <summary>
/// Decides whether a destructive button may be pressed. The press tool asks it about every button
/// the current screen's description flags <c>destructive</c>, and a denied press never reaches the app.
/// </summary>
public interface IApprovalGate
{
    /// <summary>True to press <paramref name="buttonId"/>, labelled <paramref name="label"/>, on the screen <paramref name="screenId"/>.</summary>
    Task<bool> ApproveAsync(string buttonId, string label, string screenId, CancellationToken ct);
}
