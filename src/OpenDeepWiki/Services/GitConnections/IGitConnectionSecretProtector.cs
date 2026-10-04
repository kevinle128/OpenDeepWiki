namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Protects Git connection tokens before they are stored and reads them back for server-side use only.
/// </summary>
public interface IGitConnectionSecretProtector
{
    /// <summary>
    /// Returns the protected text for a plain token. The result is safe to store.
    /// </summary>
    string Protect(string plainToken);

    /// <summary>
    /// Returns the plain token. Fails closed with <see cref="GitConnectionSecretException"/>
    /// when the payload is changed, empty, or was protected for another purpose or application.
    /// </summary>
    string Unprotect(string protectedToken);
}

/// <summary>
/// A protected token cannot be read. The message never contains token or payload values.
/// </summary>
public sealed class GitConnectionSecretException : Exception
{
    public GitConnectionSecretException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
