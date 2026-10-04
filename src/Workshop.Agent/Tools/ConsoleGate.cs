namespace Workshop.Agent.Tools;

/// <summary>
/// Asks the person at the console: <c>Approve pressing "&lt;label&gt;" on &lt;screen&gt;? [y/N]</c>.
/// Only <c>y</c> approves (either case, spaces ignored); anything else, an empty line or the end of
/// input is a denial.
/// </summary>
public sealed class ConsoleGate(TextReader input, TextWriter output) : IApprovalGate
{
    public ConsoleGate()
        : this(Console.In, Console.Out)
    {
    }

    public async Task<bool> ApproveAsync(string buttonId, string label, string screenId, CancellationToken ct)
    {
        await output.WriteAsync($"Approve pressing \"{label}\" on {screenId}? [y/N] ").ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
        var answer = await input.ReadLineAsync(ct).ConfigureAwait(false);
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }
}
