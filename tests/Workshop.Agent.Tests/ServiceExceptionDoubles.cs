// Stand-ins for the model SDKs' exceptions, under their real namespaces and names. The engine knows
// them by name and by their Status property, not by type, so plan 2 takes no SDK package; these
// let the tests throw what a real SDK would.

namespace Azure.Identity
{
    /// <summary>As Azure.Identity's: a credential failed to authenticate.</summary>
    public class AuthenticationFailedException(string message) : Exception(message);

    /// <summary>As Azure.Identity's, which derives from <see cref="AuthenticationFailedException"/>: no credential was available.</summary>
    public class CredentialUnavailableException(string message) : AuthenticationFailedException(message);
}

namespace System.ClientModel
{
    /// <summary>
    /// As System.ClientModel's: the service answered with a failing status, or 0 when it gave no
    /// answer, the network's failure then being its inner exception.
    /// </summary>
    public class ClientResultException(int status, string message, Exception? innerException = null) : Exception(message, innerException)
    {
        public int Status { get; } = status;
    }
}

namespace Workshop.Agent.Tests
{
    /// <summary>An SDK's own exception type derived from <see cref="System.ClientModel.ClientResultException"/>.</summary>
    public sealed class DerivedClientResultException(int status, string message) : System.ClientModel.ClientResultException(status, message);
}
