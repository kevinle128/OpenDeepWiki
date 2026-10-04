using OpenDeepWiki.Services.GitConnections;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// A Git remote failed the origin check that runs before a connection credential is attached.
/// The message holds a stable code only: no URL, user info, or token.
/// </summary>
public sealed class GitRemoteOriginException : Exception
{
    public const string OriginMismatch = "remote_origin_mismatch";

    public GitRemoteOriginException(string errorCode)
        : base($"Git remote origin check failed: {errorCode}.")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// Keeps a connection's credential on the connection's own server. Before a clone, fetch, or remote lookup of a
/// repository that uses a connection, the remote must be an HTTPS URL on the origin recorded for that connection,
/// and the host must resolve only to addresses that the operator policy allows. The credential callback repeats the
/// origin check for every URL that libgit2 asks credentials for, so a redirect cannot carry the token elsewhere.
/// </summary>
internal sealed class GitRemoteOriginGuard(GitLabServerUrlValidator validator)
{
    /// <summary>
    /// Throws unless the remote is on the connection origin and its DNS answers pass the address policy.
    /// Repositories without a connection are not checked: they hold no shared credential.
    /// </summary>
    public async Task EnsureAllowedAsync(Entities.Repository repository, string remoteUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repository.GitConnectionId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(repository.ProviderBaseUrl) || !IsSameOrigin(remoteUrl, repository.ProviderBaseUrl))
        {
            throw new GitRemoteOriginException(GitRemoteOriginException.OriginMismatch);
        }

        await validator.ResolveAllowedAddressesAsync(new Uri(remoteUrl).IdnHost, cancellationToken);
    }

    /// <summary>
    /// True when <paramref name="url"/> is an HTTPS URL without user info that has the scheme, host, and port of
    /// <paramref name="origin"/>. A credential never travels over plain HTTP.
    /// </summary>
    public static bool IsSameOrigin(string url, string origin)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var expected)
            || candidate.Scheme != Uri.UriSchemeHttps
            || expected.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        return string.Equals(candidate.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase)
               && candidate.Port == expected.Port;
    }

    /// <summary>
    /// Two remote URLs name the same repository when scheme, host, port, and path agree. A trailing slash and a
    /// <c>.git</c> suffix do not matter. A URL with user info never matches: a credential in a URL must not link
    /// a repository to a connection.
    /// </summary>
    public static bool IsSameRemote(string? left, string? right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a)
            || !Uri.TryCreate(right, UriKind.Absolute, out var b)
            || !string.IsNullOrEmpty(a.UserInfo)
            || !string.IsNullOrEmpty(b.UserInfo))
        {
            return false;
        }

        return a.Scheme == b.Scheme
               && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
               && a.Port == b.Port
               && string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }
}
