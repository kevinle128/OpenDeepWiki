using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.E2EHost;

/// <summary>
/// The real coordinator with two test rules. A sync (incremental) job always finds the cluster full and stays Pending,
/// because this host never fetches from a remote. While <see cref="BranchProcessorSwitches.Pause"/> is on, a branch
/// job finds the cluster full too. Every other call goes to the real coordinator unchanged.
/// </summary>
internal sealed class PausableCoordinator(IWikiGenerationCoordinator inner) : IWikiGenerationCoordinator
{
    public Task RecoverStaleWorkAsync(IContext context, CancellationToken cancellationToken = default)
        => inner.RecoverStaleWorkAsync(context, cancellationToken);

    public Task<(WikiGenerationAcquireStatus Status, WikiGenerationWorkLease? Lease)> TryBeginAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        RepositoryGenerationLockScope scope,
        WikiGenerationWorkType workType,
        CancellationToken cancellationToken = default,
        string? branchId = null)
    {
        if (workType == WikiGenerationWorkType.IncrementalTask
            || (BranchProcessorSwitches.Pause && workType == WikiGenerationWorkType.BranchTask))
        {
            return Task.FromResult<(WikiGenerationAcquireStatus, WikiGenerationWorkLease?)>((WikiGenerationAcquireStatus.ClusterFull, null));
        }

        return inner.TryBeginAsync(context, repositoryId, ownerType, ownerId, scope, workType, cancellationToken, branchId);
    }

    public Task HeartbeatAsync(IContext context, WikiGenerationWorkLease lease, CancellationToken cancellationToken = default)
        => inner.HeartbeatAsync(context, lease, cancellationToken);

    public Task ReleaseAsync(IContext context, WikiGenerationWorkLease lease, CancellationToken cancellationToken = default)
        => inner.ReleaseAsync(context, lease, cancellationToken);
}
