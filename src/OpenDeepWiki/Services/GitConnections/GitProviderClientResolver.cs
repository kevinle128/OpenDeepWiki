using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

public sealed class GitProviderClientResolver : IGitProviderClientResolver
{
    private readonly IReadOnlyDictionary<GitProvider, IGitProviderClient> _clients;

    public GitProviderClientResolver(IEnumerable<IGitProviderClient> clients)
    {
        _clients = clients.ToDictionary(client => client.Provider);
    }

    public IGitProviderClient Resolve(GitProvider provider)
        => Enum.IsDefined(provider) && _clients.TryGetValue(provider, out var client)
            ? client
            : throw new GitProviderException(GitProviderErrorCodes.UnsupportedProvider);
}
