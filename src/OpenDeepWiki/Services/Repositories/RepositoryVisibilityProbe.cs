using OpenDeepWiki.Services.GitConnections;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Reads the current visibility of a repository from its own Git provider.
/// </summary>
public interface IRepositoryVisibilityProbe
{
    /// <summary>
    /// Returns true for a public remote and false for a private or internal one.
    /// Returns null when the repository has no provider identity or connection, so no answer exists.
    /// A provider or credential failure throws; the caller keeps the stored visibility.
    /// </summary>
    Task<bool?> GetIsPublicAsync(Entities.Repository repository, CancellationToken cancellationToken);
}

/// <summary>
/// Asks the provider client that matches the repository's provider, with the repository's connection credential.
/// It never assumes GitHub: a GitLab repository is checked on its GitLab server.
/// </summary>
public sealed class ProviderRepositoryVisibilityProbe(
    IGitCredentialResolver credentialResolver,
    IGitProviderClientResolver clientResolver) : IRepositoryVisibilityProbe
{
    public async Task<bool?> GetIsPublicAsync(Entities.Repository repository, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (repository.Provider is not { } provider
            || string.IsNullOrWhiteSpace(repository.GitConnectionId)
            || string.IsNullOrWhiteSpace(repository.ProviderBaseUrl)
            || string.IsNullOrWhiteSpace(repository.ProviderRepositoryId))
        {
            return null;
        }

        var credential = await credentialResolver.ResolveAsync(repository, cancellationToken);
        if (credential is null)
        {
            return null;
        }

        var target = new GitProviderTarget(
            repository.GitConnectionId, provider, repository.ProviderBaseUrl, credential.Password);
        var remote = await clientResolver.Resolve(provider)
            .GetRepositoryAsync(target, repository.ProviderRepositoryId, cancellationToken);

        return string.Equals(remote.Visibility, "Public", StringComparison.OrdinalIgnoreCase);
    }
}
