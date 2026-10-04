using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Single place for Git connection permission decisions. Repository ownership plays no role here.
/// </summary>
public interface IGitConnectionAuthorizationService
{
    /// <summary>
    /// The caller is authenticated and the connection is enabled and not deleted.
    /// </summary>
    bool CanUse(GitConnection connection);

    /// <summary>
    /// The caller is the creator or a current Admin. Both facts are read from the database, not from JWT claims.
    /// </summary>
    Task<bool> CanMaintainAsync(GitConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller still exists and has an active Admin role in the database. A token that still claims the
    /// role after the database revoked it does not count.
    /// </summary>
    Task<bool> IsAdminAsync(CancellationToken cancellationToken = default);
}
