using Microsoft.Extensions.AI;

namespace Workshop.Agent.Engines;

/// <summary>
/// Waits out throttling (spec §4.4): on <see cref="ThrottledException"/> it waits the time the
/// model asks for and tries again, within one wait budget for its whole life, 60 s by default.
/// A wait that would pass the budget is not started: the exception goes on to the engine, whose
/// outcome is then <see cref="EngineOutcome.Throttled"/>. The engine builds one per run.
/// </summary>
public sealed class ThrottleRetryChatClient : DelegatingChatClient
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(60);

    private readonly TimeSpan budget;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Lock gate = new();
    private TimeSpan waited;

    /// <param name="inner">The model's client.</param>
    /// <param name="budget">The total wait allowed; <see cref="DefaultBudget"/> when null.</param>
    /// <param name="delay">How to wait; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null.</param>
    public ThrottleRetryChatClient(IChatClient inner, TimeSpan? budget = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        : base(inner)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget ?? DefaultBudget, TimeSpan.Zero, nameof(budget));
        this.budget = budget ?? DefaultBudget;
        this.delay = delay ?? Task.Delay;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Read once: a retry sends the same messages again.
        IEnumerable<ChatMessage> sent = messages is IReadOnlyCollection<ChatMessage> ? messages : messages.ToList();
        while (true)
        {
            try
            {
                return await base.GetResponseAsync(sent, options, cancellationToken).ConfigureAwait(false);
            }
            catch (ThrottledException e) when (TryTake(e.RetryAfter))
            {
                await delay(Clamp(e.RetryAfter), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The engine never streams; a stream here would not be retried, so it is refused.</summary>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The engine calls the model without streaming.");

    private static TimeSpan Clamp(TimeSpan retryAfter) => retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;

    /// <summary>Takes the wait from the budget, or returns false, taking nothing, when it would pass it.</summary>
    private bool TryTake(TimeSpan retryAfter)
    {
        lock (gate)
        {
            var after = waited + Clamp(retryAfter);
            if (after > budget)
            {
                return false;
            }

            waited = after;
            return true;
        }
    }
}
