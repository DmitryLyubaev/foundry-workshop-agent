namespace Workshop.Agent.Surface;

/// <summary>
/// The endpoint answered with a status other than 2xx. <see cref="Body"/> is its answer as sent,
/// normally <c>{"error":"&lt;code&gt;"}</c>.
/// </summary>
public sealed class SurfaceHttpException(int status, string body)
    : Exception($"The workshop app's endpoint answered {status}: {body}")
{
    public int Status { get; } = status;

    public string Body { get; } = body;
}
