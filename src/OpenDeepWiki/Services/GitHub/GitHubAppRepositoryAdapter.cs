using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models.Admin;

namespace OpenDeepWiki.Services.GitHub;

/// <summary>
/// Compatibility layer for GitHub App imports. The App routes and request shapes stay as they are, and the imported
/// repository gets the same stable remote identity as a repository that a Git connection registers. A remote that is
/// already registered under that identity is skipped, so App imports and connection imports never create two rows.
/// </summary>
internal static class GitHubAppRepositoryAdapter
{
    public const string ServerUrl = "https://github.com";

    /// <summary>
    /// The stable remote ID of an import entry, or null when the client did not send one.
    /// </summary>
    public static string? RemoteIdOf(BatchImportRepo repo)
        => repo.Id is > 0 ? repo.Id.Value.ToString(CultureInfo.InvariantCulture) : null;

    public static string RemoteIdOf(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Active repositories that already carry the GitHub identity of one of the IDs.
    /// </summary>
    public static async Task<HashSet<string>> FindRegisteredRemoteIdsAsync(
        IContext context, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken)
    {
        if (remoteIds.Count == 0)
        {
            return [];
        }

        var found = await context.Repositories
            .AsNoTracking()
            .Where(item => item.Provider == GitProvider.GitHub
                           && item.ProviderBaseUrl == ServerUrl
                           && item.ProviderRepositoryId != null
                           && remoteIds.Contains(item.ProviderRepositoryId)
                           && !item.IsDeleted)
            .Select(item => item.ProviderRepositoryId!)
            .ToListAsync(cancellationToken);
        return found.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gives an App-imported repository its stable identity. It has no connection yet: the first connection that
    /// selects the remote attaches itself.
    /// </summary>
    public static void AssignIdentity(Repository repository, string remoteId, string? defaultBranch)
    {
        repository.Provider = GitProvider.GitHub;
        repository.ProviderBaseUrl = ServerUrl;
        repository.ProviderRepositoryId = remoteId;
        repository.DefaultBranch = defaultBranch;
    }
}
