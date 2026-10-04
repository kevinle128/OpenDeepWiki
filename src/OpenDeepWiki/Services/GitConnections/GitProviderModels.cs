using System.Net;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Stable error codes for provider calls. They are safe to return to clients and to store:
/// they never contain provider response text, tokens, or addresses.
/// </summary>
public static class GitProviderErrorCodes
{
    public const string Unauthorized = "PROVIDER_UNAUTHORIZED";
    public const string Forbidden = "PROVIDER_FORBIDDEN";
    public const string NotFound = "PROVIDER_NOT_FOUND";
    public const string RateLimited = "PROVIDER_RATE_LIMITED";
    public const string Unavailable = "PROVIDER_UNAVAILABLE";
    public const string DnsFailure = "PROVIDER_DNS_FAILURE";
    public const string TlsFailure = "PROVIDER_TLS_FAILURE";
    public const string Timeout = "PROVIDER_TIMEOUT";
    public const string RedirectBlocked = "PROVIDER_REDIRECT_BLOCKED";
    public const string InvalidResponse = "PROVIDER_INVALID_RESPONSE";
    public const string RequestRejected = "PROVIDER_REQUEST_REJECTED";
    public const string UnsupportedProvider = "PROVIDER_UNSUPPORTED";
    public const string ServerUrlInvalid = "SERVER_URL_INVALID";
    public const string ServerUrlBlocked = "SERVER_URL_BLOCKED";
    public const string InvalidCursor = "INVALID_CURSOR";
    public const string InvalidRepositoryId = "INVALID_REPOSITORY_ID";
}

/// <summary>
/// A provider call failed. The message is built from the stable code only.
/// </summary>
public sealed class GitProviderException : Exception
{
    public GitProviderException(string code, TimeSpan? retryAfter = null)
        : base($"Git provider request failed: {code}.")
    {
        Code = code;
        RetryAfter = retryAfter;
    }

    public string Code { get; }

    /// <summary>
    /// Wait time that the provider asked for. The server never sleeps on it; callers pass it on.
    /// </summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// Operator settings for provider calls. Users cannot change them.
/// </summary>
public sealed class GitProviderOptions
{
    public const string SectionName = "GitProviders";

    /// <summary>
    /// Upper bound for one provider request, including connect and TLS.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Exact host names that may resolve to private or loopback addresses. Empty by default.
    /// </summary>
    public List<string> AllowedPrivateHosts { get; set; } = [];

    /// <summary>
    /// CIDR ranges (for example <c>10.20.0.0/16</c>) that private addresses may fall into. Empty by default.
    /// </summary>
    public List<string> AllowedPrivateCidrs { get; set; } = [];

    /// <summary>
    /// True when every configured CIDR can be parsed.
    /// </summary>
    public bool HasValidCidrs() => AllowedPrivateCidrs.All(cidr => IPNetwork.TryParse(cidr, out _));
}

/// <summary>
/// Names of the HTTP clients used for provider calls.
/// </summary>
public static class GitProviderHttpClientNames
{
    public const string GitHub = "GitHubPat";
    public const string GitLab = "GitLabPat";
}

/// <summary>
/// Everything a provider client needs for one connection. The token is plain text:
/// keep the object in a local variable and never log it.
/// </summary>
public sealed class GitProviderTarget
{
    public GitProviderTarget(string? connectionId, GitProvider provider, string serverUrl, string token)
    {
        ConnectionId = connectionId;
        Provider = provider;
        ServerUrl = serverUrl;
        Token = token;
    }

    /// <summary>
    /// Null while a token is validated before the connection exists. Cursors need a connection ID.
    /// </summary>
    public string? ConnectionId { get; }

    public GitProvider Provider { get; }

    /// <summary>
    /// Normalized server URL of the connection.
    /// </summary>
    public string ServerUrl { get; }

    public string Token { get; }

    public override string ToString() => $"GitProviderTarget({Provider}, redacted)";
}

/// <summary>
/// Account that the provider returned for a validated token.
/// </summary>
public sealed record GitProviderIdentity(string ExternalAccountId, string AccountName);

public sealed record RemoteRepository(
    string ProviderRepositoryId,
    string Name,
    string FullName,
    string? Namespace,
    string? Description,
    string CloneUrl,
    string? WebUrl,
    string? DefaultBranch,
    string Visibility,
    DateTimeOffset? UpdatedAt);

public sealed record RemoteBranch(string Name, bool IsDefault, string? CommitSha);

/// <summary>
/// One page of provider results. <see cref="NextCursor"/> is opaque and bound to one connection.
/// <see cref="TotalCount"/> is null because providers can omit the total.
/// </summary>
public sealed record ProviderPage<T>(IReadOnlyList<T> Items, string? NextCursor, int? TotalCount = null);

/// <summary>
/// Page size rules shared by all provider clients.
/// </summary>
public static class GitProviderPaging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>
    /// Values below 1 select the default. Values above the provider maximum are capped.
    /// </summary>
    public static int Normalize(int pageSize) => pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
}
