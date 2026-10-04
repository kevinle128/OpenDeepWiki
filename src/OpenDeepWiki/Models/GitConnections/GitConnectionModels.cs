using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Models.GitConnections;

/// <summary>
/// Stable error codes of the connection API (provider errors use <c>GitProviderErrorCodes</c>).
/// </summary>
public static class GitConnectionErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string NotFound = "CONNECTION_NOT_FOUND";
    public const string MaintenanceForbidden = "CONNECTION_MAINTENANCE_FORBIDDEN";
    public const string Disabled = "CONNECTION_DISABLED";
    public const string InUse = "CONNECTION_IN_USE";
    public const string Conflict = "CONNECTION_CONFLICT";
    public const string AccountMismatch = "CONNECTION_ACCOUNT_MISMATCH";
    public const string SecretUnreadable = "CONNECTION_SECRET_UNREADABLE";
    public const string InvalidProvider = "INVALID_PROVIDER";
    public const string InvalidToken = "INVALID_TOKEN";
    public const string InvalidDisplayName = "INVALID_DISPLAY_NAME";
}

/// <summary>
/// Request to create a connection. The token is sent once and is never returned.
/// </summary>
public sealed class CreateGitConnectionRequest
{
    /// <summary>
    /// <c>GitHub</c> or <c>GitLab</c>.
    /// </summary>
    public string? Provider { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>
    /// HTTPS origin of a self-hosted GitLab. Empty means GitLab.com. Not used for GitHub.
    /// </summary>
    public string? ServerUrl { get; set; }

    public string? Token { get; set; }

    public override string ToString() => "CreateGitConnectionRequest(redacted)";
}

/// <summary>
/// Request to change a connection. A null or empty token keeps the current credential.
/// </summary>
public sealed class UpdateGitConnectionRequest
{
    public string? DisplayName { get; set; }

    public string? Token { get; set; }

    public override string ToString() => "UpdateGitConnectionRequest(redacted)";
}

/// <summary>
/// Connection as shown to clients. It has no token field; <see cref="HasSecret"/> only says that one is stored.
/// </summary>
public sealed record GitConnectionResponse(
    string Id,
    string Provider,
    string DisplayName,
    string ServerUrl,
    string AccountLogin,
    int RepositoryCount,
    string State,
    bool IsEnabled,
    DateTime? LastValidatedAt,
    string? LastValidationErrorCode,
    DateTime CreatedAt,
    string CreatedByUserId,
    bool CanMaintain,
    bool HasSecret);

public static class GitConnectionCreateOutcomes
{
    public const string Created = "Created";
    public const string Restored = "Restored";
    public const string Existing = "Existing";
}

/// <summary>
/// Result of a create request. <c>Existing</c> means the account already had a connection;
/// that connection is returned and its credential is not replaced.
/// </summary>
public sealed record CreateGitConnectionResult(GitConnectionResponse Connection, string Outcome);

/// <summary>
/// Result of a health check. Provider failures are reported here, not as API errors.
/// </summary>
public sealed record GitConnectionHealthResponse(bool Ok, string State, string? ErrorCode, long LatencyMs, DateTime CheckedAt);

public sealed record GitConnectionAuditEventResponse(
    string Id,
    string EventType,
    string Outcome,
    string? ActorUserId,
    string? RepositoryId,
    string? ErrorCode,
    DateTime CreatedAt);
