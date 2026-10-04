using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Plain Git credential for one operation. Keep it in a local variable only; do not store, cache, or log it.
/// </summary>
public sealed class GitCredential
{
    public GitCredential(string username, string password)
    {
        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }

    public override string ToString() => "GitCredential(redacted)";
}

/// <summary>
/// Stable codes for credential resolution failures.
/// </summary>
public static class GitCredentialErrorCodes
{
    public const string ConnectionNotFound = "connection_not_found";
    public const string ConnectionDisabled = "connection_disabled";
    public const string SecretUnreadable = "connection_secret_unreadable";
}

/// <summary>
/// A repository credential cannot be resolved. Callers must stop the Git operation; there is no fallback.
/// </summary>
public sealed class GitCredentialResolutionException : Exception
{
    public GitCredentialResolutionException(string errorCode)
        : base($"Git credential cannot be resolved: {errorCode}.")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// Resolves the credential for a repository just before a Git operation.
/// </summary>
public interface IGitCredentialResolver
{
    /// <summary>
    /// Returns the credential, or null when the repository needs none.
    /// When the repository references a connection, a missing, deleted, disabled, or unreadable connection
    /// throws <see cref="GitCredentialResolutionException"/>.
    /// </summary>
    Task<GitCredential?> ResolveAsync(Repository repository, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the repository has a credential that a Git operation could use now. With a connection, the
    /// connection must exist, be enabled, and decrypt; the legacy fields are never consulted. Without a connection,
    /// a legacy password counts. It never throws for a bad credential and never exposes one.
    /// </summary>
    Task<bool> HasUsableCredentialAsync(Repository repository, CancellationToken cancellationToken = default);
}
