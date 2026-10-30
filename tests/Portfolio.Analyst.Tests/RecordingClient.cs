using Microsoft.Extensions.AI;
using Workshop.Agent.Engines;

namespace Portfolio.Analyst.Tests;

/// <summary>What the model was sent on one call.</summary>
internal sealed record SentChat(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

/// <summary>A scripted model (<see cref="ScriptedChatClient"/>) that keeps every request it was sent.</summary>
internal sealed class RecordingClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private readonly Lock gate = new();
    private readonly List<SentChat> requests = [];

    public IReadOnlyList<SentChat> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    /// <summary>A scripted model that replies with <paramref name="replies"/> in turn.</summary>
    public static RecordingClient Replying(params string[] replies) =>
        new(new ScriptedChatClient(new Script([.. replies.Select(r => (ScriptStep)new ScriptStep.Reply(r))])));

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            requests.Add(new SentChat([.. messages], options));
        }

        return base.GetResponseAsync(messages, options, cancellationToken);
    }
}
