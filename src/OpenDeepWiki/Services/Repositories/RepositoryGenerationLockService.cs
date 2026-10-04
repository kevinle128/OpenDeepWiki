using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Wiki;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Generation locks of repositories and branches.
/// A repository-scope lock (no branch ID) excludes every other lock of the repository.
/// A branch-scope lock excludes the repository lock and any other lock of the same branch,
/// but not the locks of other branches. A scope must match its branch ID: repository scope has none,
/// branch scope has one.
/// </summary>
public interface IRepositoryGenerationLockService
{
    /// <summary>
    /// Returns the lock that blocks the request. With a branch ID this is the repository lock or that branch's lock.
    /// Without one it is any lock of the repository, a repository-scope lock first.
    /// </summary>
    Task<RepositoryGenerationLock?> GetLockAsync(
        string repositoryId,
        CancellationToken cancellationToken = default,
        string? branchId = null);

    Task<bool> TryAcquireAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        RepositoryGenerationLockScope scope,
        CancellationToken cancellationToken = default,
        bool bindToCurrentInstance = false,
        string? branchId = null);

    /// <param name="branchId">When set, only the lock of that branch matches.</param>
    Task HeartbeatAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        CancellationToken cancellationToken = default,
        string? branchId = null);

    /// <param name="branchId">When set, only the lock of that branch matches.</param>
    Task UnbindAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        CancellationToken cancellationToken = default,
        string? branchId = null);

    /// <param name="branchId">When set, only the lock of that branch matches.</param>
    Task ReleaseAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        CancellationToken cancellationToken = default,
        string? branchId = null);

    Task RecoverStaleLocksAsync(
        IContext context,
        CancellationToken cancellationToken = default);
}

public sealed class RepositoryGenerationLockService : IRepositoryGenerationLockService
{
    private readonly IContext _rootContext;
    private readonly WikiGenerationInstanceIdentity _instanceIdentity;
    private readonly IOptionsMonitor<WikiGeneratorOptions>? _wikiOptions;

    public RepositoryGenerationLockService(IContext rootContext)
        : this(rootContext, new WikiGenerationInstanceIdentity(), null)
    {
    }

    public RepositoryGenerationLockService(
        IContext rootContext,
        WikiGenerationInstanceIdentity instanceIdentity,
        IOptionsMonitor<WikiGeneratorOptions>? wikiOptions)
    {
        _rootContext = rootContext;
        _instanceIdentity = instanceIdentity;
        _wikiOptions = wikiOptions;
    }

    public Task<RepositoryGenerationLock?> GetLockAsync(
        string repositoryId,
        CancellationToken cancellationToken = default,
        string? branchId = null)
    {
        return _rootContext.RepositoryGenerationLocks
            .AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId && !item.IsDeleted)
            .Where(item => branchId == null || item.BranchId == null || item.BranchId == branchId)
            .OrderBy(item => item.BranchId != null)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> TryAcquireAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        RepositoryGenerationLockScope scope,
        CancellationToken cancellationToken = default,
        bool bindToCurrentInstance = false,
        string? branchId = null)
    {
        ValidateScope(scope, branchId);

        // The filtered unique indexes only stop two locks with the same key. Whether a repository lock and a
        // branch lock may coexist is decided here, so concurrent requests for one repository must run one
        // after the other: the first statement takes a write lock on the repository row until commit.
        await using var ownTransaction = await BeginOwnTransactionAsync(context, cancellationToken);
        await SerializeRepositoryAsync(context, repositoryId, cancellationToken);

        var acquired = await TryAcquireCoreAsync(
            context, repositoryId, ownerType, ownerId, scope, bindToCurrentInstance, branchId, cancellationToken);

        if (ownTransaction is not null)
        {
            if (acquired)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }
            else
            {
                await ownTransaction.RollbackAsync(CancellationToken.None);
            }
        }

        return acquired;
    }

    private async Task<bool> TryAcquireCoreAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        RepositoryGenerationLockScope scope,
        bool bindToCurrentInstance,
        string? branchId,
        CancellationToken cancellationToken)
    {
        var locks = await context.RepositoryGenerationLocks
            .Where(item => item.RepositoryId == repositoryId && !item.IsDeleted)
            .ToListAsync(cancellationToken);

        var existing = locks.FirstOrDefault(item => item.BranchId == branchId);
        var blockers = locks
            .Where(item => !ReferenceEquals(item, existing) && Excludes(branchId, item))
            .ToList();

        if (blockers.Any(blocker => !IsStale(blocker)))
        {
            return false;
        }

        if (blockers.Count > 0)
        {
            foreach (var blocker in blockers)
            {
                await RecoverLockWorkAsync(context, blocker, cancellationToken);
                context.RepositoryGenerationLocks.Remove(blocker);
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        if (existing is null)
        {
            var generationLock = CreateLock(repositoryId, branchId, ownerType, ownerId, scope, bindToCurrentInstance);
            context.RepositoryGenerationLocks.Add(generationLock);

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                if (context is DbContext dbContext)
                {
                    dbContext.Entry(generationLock).State = EntityState.Detached;
                }

                return false;
            }
        }

        if (IsStale(existing))
        {
            await RecoverLockWorkAsync(context, existing, cancellationToken);
            ApplyOwnership(existing, ownerType, ownerId, scope, bindToCurrentInstance);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (existing.OwnerType != ownerType || existing.OwnerId != ownerId)
        {
            return false;
        }

        if (!bindToCurrentInstance)
        {
            return true;
        }

        if (CanBindToCurrentInstance(existing))
        {
            BindToCurrentInstance(existing);
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    /// <summary>
    /// A repository-scope request (no branch ID) excludes every other lock of the repository.
    /// A branch request excludes the repository-scope lock only; other branches stay independent.
    /// The lock with the same key is handled separately as the lock to reuse.
    /// </summary>
    private static bool Excludes(string? requestedBranchId, RepositoryGenerationLock other)
    {
        return requestedBranchId is null || other.BranchId is null;
    }

    private static void ValidateScope(RepositoryGenerationLockScope scope, string? branchId)
    {
        if (scope == RepositoryGenerationLockScope.Branch && string.IsNullOrWhiteSpace(branchId))
        {
            throw new ArgumentException("A branch-scope lock needs a branch ID.", nameof(branchId));
        }

        if (scope == RepositoryGenerationLockScope.Repository && branchId is not null)
        {
            throw new ArgumentException("A repository-scope lock cannot have a branch ID.", nameof(branchId));
        }
    }

    private static async Task<IDbContextTransaction?> BeginOwnTransactionAsync(
        IContext context,
        CancellationToken cancellationToken)
    {
        if (!EfContextCapabilities.SupportsExecuteUpdate(context) ||
            context is not DbContext { Database.CurrentTransaction: null } dbContext)
        {
            return null;
        }

        return await dbContext.Database.BeginTransactionAsync(cancellationToken);
    }

    /// <summary>
    /// Updates the repository row to its own value. The row stays locked until the transaction ends,
    /// so another acquire for the same repository waits and then sees this transaction's lock rows.
    /// </summary>
    private static async Task SerializeRepositoryAsync(
        IContext context,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        if (!EfContextCapabilities.SupportsExecuteUpdate(context) || context is not DbContext { Database.CurrentTransaction: not null })
        {
            return;
        }

        await context.Repositories
            .Where(item => item.Id == repositoryId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.UpdatedAt, item => item.UpdatedAt),
                cancellationToken);
    }

    public async Task HeartbeatAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        CancellationToken cancellationToken = default,
        string? branchId = null)
    {
        var now = DateTime.UtcNow;
        var instanceId = _instanceIdentity.InstanceId;

        if (EfContextCapabilities.SupportsExecuteUpdate(context))
        {
            await context.RepositoryGenerationLocks
                .Where(item =>
                    item.RepositoryId == repositoryId &&
                    item.OwnerType == ownerType &&
                    item.OwnerId == ownerId &&
                    (branchId == null || item.BranchId == branchId) &&
                    item.InstanceId == instanceId &&
                    !item.IsDeleted)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.HeartbeatAt, now)
                        .SetProperty(item => item.UpdatedAt, now),
                    cancellationToken);
            return;
        }

        var generationLock = await context.RepositoryGenerationLocks
            .FirstOrDefaultAsync(item =>
                item.RepositoryId == repositoryId &&
                item.OwnerType == ownerType &&
                item.OwnerId == ownerId &&
                (branchId == null || item.BranchId == branchId) &&
                item.InstanceId == instanceId &&
                !item.IsDeleted,
                cancellationToken);

        if (generationLock is null)
        {
            return;
        }

        generationLock.HeartbeatAt = now;
        generationLock.UpdateTimestamp();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UnbindAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        CancellationToken cancellationToken = default,
        string? branchId = null)
    {
        var generationLock = await context.RepositoryGenerationLocks
            .FirstOrDefaultAsync(item =>
                item.RepositoryId == repositoryId &&
                item.OwnerType == ownerType &&
                item.OwnerId == ownerId &&
                (branchId == null || item.BranchId == branchId) &&
                item.InstanceId == _instanceIdentity.InstanceId &&
                !item.IsDeleted,
                cancellationToken);

        if (generationLock is null)
        {
            return;
        }

        generationLock.InstanceId = null;
        generationLock.HeartbeatAt = null;
        generationLock.UpdateTimestamp();
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ReleaseAsync(
        IContext context,
        string repositoryId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        CancellationToken cancellationToken = default,
        string? branchId = null)
    {
        var generationLock = await context.RepositoryGenerationLocks
            .FirstOrDefaultAsync(item =>
                item.RepositoryId == repositoryId &&
                item.OwnerType == ownerType &&
                item.OwnerId == ownerId &&
                (branchId == null || item.BranchId == branchId) &&
                !item.IsDeleted,
                cancellationToken);

        if (generationLock is null)
        {
            return;
        }

        context.RepositoryGenerationLocks.Remove(generationLock);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task RecoverStaleLocksAsync(
        IContext context,
        CancellationToken cancellationToken = default)
    {
        var staleBefore = GetStaleBefore();
        var staleLocks = await context.RepositoryGenerationLocks
            .Where(item =>
                !item.IsDeleted &&
                item.InstanceId != null &&
                item.HeartbeatAt != null &&
                item.HeartbeatAt < staleBefore)
            .Take(50)
            .ToListAsync(cancellationToken);

        foreach (var generationLock in staleLocks)
        {
            await RecoverLockWorkAsync(context, generationLock, cancellationToken);
            generationLock.InstanceId = null;
            generationLock.HeartbeatAt = null;
            generationLock.UpdateTimestamp();
        }

        if (staleLocks.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    private RepositoryGenerationLock CreateLock(
        string repositoryId,
        string? branchId,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        RepositoryGenerationLockScope scope,
        bool bindToCurrentInstance)
    {
        var now = DateTime.UtcNow;
        return new RepositoryGenerationLock
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repositoryId,
            BranchId = branchId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            Scope = scope,
            AcquiredAt = now,
            CreatedAt = now,
            InstanceId = bindToCurrentInstance ? _instanceIdentity.InstanceId : null,
            HeartbeatAt = bindToCurrentInstance ? now : null
        };
    }

    private void ApplyOwnership(
        RepositoryGenerationLock generationLock,
        RepositoryGenerationLockOwnerType ownerType,
        string ownerId,
        RepositoryGenerationLockScope scope,
        bool bindToCurrentInstance)
    {
        generationLock.OwnerType = ownerType;
        generationLock.OwnerId = ownerId;
        generationLock.Scope = scope;
        generationLock.AcquiredAt = DateTime.UtcNow;
        generationLock.InstanceId = bindToCurrentInstance ? _instanceIdentity.InstanceId : null;
        generationLock.HeartbeatAt = bindToCurrentInstance ? DateTime.UtcNow : null;
        generationLock.UpdateTimestamp();
    }

    private void BindToCurrentInstance(RepositoryGenerationLock generationLock)
    {
        var now = DateTime.UtcNow;
        generationLock.InstanceId = _instanceIdentity.InstanceId;
        generationLock.HeartbeatAt = now;
        generationLock.UpdateTimestamp();
    }

    private bool CanBindToCurrentInstance(RepositoryGenerationLock generationLock)
    {
        return string.IsNullOrWhiteSpace(generationLock.InstanceId) ||
               string.Equals(generationLock.InstanceId, _instanceIdentity.InstanceId, StringComparison.Ordinal) ||
               IsStale(generationLock);
    }

    private bool IsStale(RepositoryGenerationLock generationLock)
    {
        return !string.IsNullOrWhiteSpace(generationLock.InstanceId) &&
               generationLock.HeartbeatAt is { } heartbeat &&
               heartbeat < GetStaleBefore();
    }

    private DateTime GetStaleBefore()
    {
        var timeoutSeconds = _wikiOptions?.CurrentValue.GetGenerationLeaseTimeoutSeconds() ??
                             WikiGeneratorOptions.DefaultGenerationLeaseTimeoutSeconds;
        return DateTime.UtcNow.AddSeconds(-timeoutSeconds);
    }

    private static async Task RecoverLockWorkAsync(
        IContext context,
        RepositoryGenerationLock generationLock,
        CancellationToken cancellationToken)
    {
        if (generationLock.OwnerType == RepositoryGenerationLockOwnerType.Repository)
        {
            var repository = await context.Repositories
                .FirstOrDefaultAsync(item => item.Id == generationLock.RepositoryId && !item.IsDeleted, cancellationToken);
            if (repository is { Status: RepositoryStatus.Processing })
            {
                repository.Status = RepositoryStatus.Pending;
                repository.UpdateTimestamp();
            }

            return;
        }

        if (generationLock.OwnerType == RepositoryGenerationLockOwnerType.BranchTask)
        {
            var task = await context.BranchGenerationTasks
                .FirstOrDefaultAsync(item => item.Id == generationLock.OwnerId && !item.IsDeleted, cancellationToken);
            if (task is { Status: BranchGenerationTaskStatus.Processing })
            {
                task.Status = BranchGenerationTaskStatus.Pending;
                task.StartedAt = null;
                task.ErrorMessage = null;
                task.UpdateTimestamp();
            }

            var branch = await context.RepositoryBranches
                .FirstOrDefaultAsync(item =>
                    item.Id == (task != null ? task.BranchId : string.Empty) && !item.IsDeleted,
                    cancellationToken);
            if (branch is { GenerationStatus: BranchGenerationTaskStatus.Processing })
            {
                branch.GenerationStatus = BranchGenerationTaskStatus.Pending;
                branch.LastGenerationError = null;
                branch.UpdateTimestamp();
            }

            return;
        }

        if (generationLock.OwnerType == RepositoryGenerationLockOwnerType.IncrementalTask)
        {
            var task = await context.IncrementalUpdateTasks
                .FirstOrDefaultAsync(item => item.Id == generationLock.OwnerId && !item.IsDeleted, cancellationToken);
            if (task is { Status: IncrementalUpdateStatus.Processing })
            {
                task.Status = IncrementalUpdateStatus.Pending;
                task.StartedAt = null;
                task.ErrorMessage = null;
                task.UpdatedAt = DateTime.UtcNow;
            }
        }
    }
}
