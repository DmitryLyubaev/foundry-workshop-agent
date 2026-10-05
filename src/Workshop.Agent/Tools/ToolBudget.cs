namespace Workshop.Agent.Tools;

/// <summary>The tool calls one scenario may make (spec §4.4): every call takes one, whatever it does.</summary>
public sealed class ToolBudget
{
    private int used;

    public ToolBudget(int max = Engines.AgentSettings.MaxToolCalls)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        Max = max;
    }

    public int Max { get; }

    public int Used => Volatile.Read(ref used);

    /// <summary>Takes one call, or returns false, taking nothing, when all <see cref="Max"/> are used.</summary>
    public bool TryTake()
    {
        while (true)
        {
            var current = Volatile.Read(ref used);
            if (current >= Max)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref used, current + 1, current) == current)
            {
                return true;
            }
        }
    }
}
