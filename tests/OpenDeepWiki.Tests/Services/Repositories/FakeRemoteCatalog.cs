using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;

namespace OpenDeepWiki.Tests.Services.Repositories;

/// <summary>
/// Provider catalog with configured repositories and branches. Branch listing pages two names at a time.
/// </summary>
internal sealed class FakeRemoteCatalog(GitProvider provider) : IGitProviderClient
{
    private readonly Dictionary<string, (RemoteRepository Remote, List<string> Branches)> _repositories = new();

    public GitProvider Provider { get; } = provider;

    public int RepositoryCalls { get; private set; }

    public List<string> TokensSeen { get; } = [];

    public Func<Task>? OnGetRepository { get; set; }

    public Exception? Failure { get; set; }

    public void AddRepository(string id, string fullName, string visibility, string defaultBranch, params string[] branches)
    {
        var name = fullName[(fullName.LastIndexOf('/') + 1)..];
        var ns = fullName.Contains('/') ? fullName[..fullName.LastIndexOf('/')] : null;
        _repositories[id] = (
            new RemoteRepository(id, name, fullName, ns, "A repository", $"https://example.invalid/{fullName}.git",
                $"https://example.invalid/{fullName}", defaultBranch, visibility, null),
            branches.ToList());
    }

    public Task<GitProviderIdentity> ValidateAsync(GitProviderTarget target, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public async Task<RemoteRepository> GetRepositoryAsync(
        GitProviderTarget target, string providerRepositoryId, CancellationToken cancellationToken)
    {
        RepositoryCalls++;
        TokensSeen.Add(target.Token);
        if (Failure is not null)
        {
            throw Failure;
        }

        if (OnGetRepository is not null)
        {
            await OnGetRepository();
        }

        return _repositories.TryGetValue(providerRepositoryId, out var entry)
            ? entry.Remote
            : throw new GitProviderException(GitProviderErrorCodes.NotFound);
    }

    public Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
        GitProviderTarget target, string? cursor, int pageSize, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
        GitProviderTarget target, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        if (!_repositories.TryGetValue(providerRepositoryId, out var entry))
        {
            throw new GitProviderException(GitProviderErrorCodes.NotFound);
        }

        var start = cursor is null ? 0 : int.Parse(cursor);
        var page = entry.Branches.Skip(start).Take(2).ToList();
        var next = start + 2 < entry.Branches.Count ? (start + 2).ToString() : null;
        return Task.FromResult(new ProviderPage<RemoteBranch>(
            page.Select(name => new RemoteBranch(name, name == entry.Remote.DefaultBranch, null)).ToList(), next));
    }
}
