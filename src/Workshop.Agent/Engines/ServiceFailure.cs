namespace Workshop.Agent.Engines;

/// <summary>
/// Tells a failure of the model's service from a failure of the model or the loop (spec §5.4: an
/// auth failure is an error, not a task failure). A service failure is any of these, or one of
/// them wrapped as an inner exception:
/// <list type="bullet">
/// <item><see cref="HttpRequestException"/>: the network, DNS or TLS.</item>
/// <item>Azure.Identity's <c>AuthenticationFailedException</c> and <c>CredentialUnavailableException</c>: the credentials.</item>
/// <item>System.ClientModel's <c>ClientResultException</c> with <c>Status</c> 401, 403 or 500 and above: the service refusing or failing.</item>
/// </list>
/// The SDK types are known by their full names and their <c>Status</c> property, not by type: plan 2
/// has no model SDK, and taking Azure.Identity only to name two exceptions would pull in its client
/// stack. Derived types count, as the SDKs throw their own subclasses.
/// </summary>
internal static class ServiceFailure
{
    private const string AuthenticationFailed = "Azure.Identity.AuthenticationFailedException";
    private const string CredentialUnavailable = "Azure.Identity.CredentialUnavailableException";
    private const string ClientResult = "System.ClientModel.ClientResultException";

    // A cycle of inner exceptions cannot be built through the constructors, but a bound costs nothing.
    private const int MaxDepth = 16;

    public static bool Is(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var depth = 0;
        for (var current = e; current is not null && depth < MaxDepth; current = current.InnerException, depth++)
        {
            if (IsOne(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOne(Exception e) =>
        e is HttpRequestException
        || IsNamed(e, AuthenticationFailed)
        || IsNamed(e, CredentialUnavailable)
        || (IsNamed(e, ClientResult) && Status(e) is 401 or 403 or >= 500);

    /// <summary>Whether <paramref name="e"/>'s type, or one it derives from, has the full name <paramref name="fullName"/>.</summary>
    private static bool IsNamed(Exception e, string fullName)
    {
        for (var type = e.GetType(); type is not null; type = type.BaseType)
        {
            if (type.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The exception's <c>Status</c>, the HTTP status the service answered; null when it has none.</summary>
    private static int? Status(Exception e) =>
        // Not GetProperty("Status"): a subclass that hides it with its own would make that ambiguous.
        e.GetType().GetProperties().FirstOrDefault(p => p.Name == "Status" && p.PropertyType == typeof(int))?.GetValue(e) is int status ? status : null;
}
