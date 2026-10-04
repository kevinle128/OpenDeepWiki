using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Provider calls for one Git hosting provider. Every call fails with <see cref="GitProviderException"/>
/// and a stable code; no message carries provider text or the token.
/// </summary>
public interface IGitProviderClient
{
    GitProvider Provider { get; }

    /// <summary>
    /// Sends the token to the provider and returns the account it belongs to. Callers must trust
    /// only this identity, never account data that the user supplied.
    /// </summary>
    Task<GitProviderIdentity> ValidateAsync(GitProviderTarget target, CancellationToken cancellationToken);

    /// <summary>
    /// Reads current metadata of one repository by its stable provider ID.
    /// </summary>
    Task<RemoteRepository> GetRepositoryAsync(GitProviderTarget target, string providerRepositoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns one page of repositories that the token can see. Pass the previous opaque cursor to continue.
    /// </summary>
    Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
        GitProviderTarget target, string? cursor, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Returns one page of branches of a repository, identified by its stable provider ID.
    /// </summary>
    Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
        GitProviderTarget target, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken);
}

/// <summary>
/// Selects the client for a provider.
/// </summary>
public interface IGitProviderClientResolver
{
    /// <summary>
    /// Throws <see cref="GitProviderException"/> with <see cref="GitProviderErrorCodes.UnsupportedProvider"/>
    /// when no client exists.
    /// </summary>
    IGitProviderClient Resolve(GitProvider provider);
}
