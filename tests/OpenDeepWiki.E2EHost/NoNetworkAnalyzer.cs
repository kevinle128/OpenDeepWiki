using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.E2EHost;

/// <summary>
/// Stands in for the Git analyzer. The host serves a fake provider, so there is no remote to clone or fetch from,
/// and any attempt to reach one would be a real network call. Every method fails at once and locally.
/// </summary>
internal sealed class NoNetworkAnalyzer : IRepositoryAnalyzer
{
    private static InvalidOperationException Refused() => new("The end-to-end host does not clone or fetch repositories.");

    public Task<string?> GetRemoteBranchHeadCommitAsync(Repository repository, string branchName, CancellationToken cancellationToken = default)
        => throw Refused();

    public Task<RepositoryWorkspace> PrepareWorkspaceAsync(
        Repository repository, string branchName, string? previousCommitId = null, CancellationToken cancellationToken = default)
        => throw Refused();

    public Task CleanupWorkspaceAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default)
        => throw Refused();

    public Task<string[]> GetChangedFilesAsync(
        RepositoryWorkspace workspace, string? fromCommitId, string toCommitId, CancellationToken cancellationToken = default)
        => throw Refused();

    public Task<string[]> GetDeletedFilesAsync(
        RepositoryWorkspace workspace, string? fromCommitId, string toCommitId, CancellationToken cancellationToken = default)
        => throw Refused();

    public Task<string?> DetectPrimaryLanguageAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default)
        => throw Refused();
}
