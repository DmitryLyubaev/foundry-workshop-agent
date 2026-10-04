namespace Workshop.Agent.Tools;

/// <summary>One question the gate was asked.</summary>
public sealed record GateRequest(string ButtonId, string Label, string ScreenId);

/// <summary>A gate that gives the same answer every time, for scripted runs, and remembers what it was asked.</summary>
public sealed class ScriptedGate(bool approve) : IApprovalGate
{
    private readonly Lock gate = new();
    private readonly List<GateRequest> asked = [];

    /// <summary>Every question so far, in order.</summary>
    public IReadOnlyList<GateRequest> Asked
    {
        get
        {
            lock (gate)
            {
                return [.. asked];
            }
        }
    }

    public Task<bool> ApproveAsync(string buttonId, string label, string screenId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            asked.Add(new GateRequest(buttonId, label, screenId));
        }

        return Task.FromResult(approve);
    }
}
