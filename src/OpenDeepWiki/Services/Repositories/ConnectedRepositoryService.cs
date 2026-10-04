using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models.ConnectedRepositories;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;

namespace OpenDeepWiki.Services.Repositories;

public sealed class ConnectedRepositoryService(
    IContext context,
    IUserContext userContext,
    IGitConnectionAuthorizationService authorization,
    IGitConnectionSecretProtector protector,
    IGitProviderClientResolver clients,
    IRepositoryGenerationLockService lockService,
    IBranchActionAuditor auditor,
    ILogger<ConnectedRepositoryService> logger) : IConnectedRepositoryService
{
    private const int MaxBranchesPerRequest = 50;
    private const int MaxBranchNameLength = 200;
    private const int MaxNameLength = 100;
    private const int MaxDescriptionLength = 1000;
    private const int BranchPageSize = GitProviderPaging.MaxPageSize;
    private const int MaxBranchPages = 100;
    private const int MaxWriteAttempts = 6;
    private const int FullGenerationPriority = 100;
    private const string DefaultLanguageCode = "en";

    private static readonly Regex LanguageCodePattern = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.Compiled);
    private static readonly Regex PathSegmentPattern = new(@"^[\p{L}\p{N}_.\-]+$", RegexOptions.Compiled);

    public async Task<ConnectedRepositoryResponse> ConnectAsync(ConnectRepositoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = RequireUser();
        var connectionId = RequireId(request.ConnectionId, 36);
        var remoteId = RequireId(request.ProviderRepositoryId, 128);
        var branches = NormalizeBranches(request.Branches);
        var language = NormalizeLanguage(request.LanguageCode);

        var connection = await LoadUsableConnectionAsync(connectionId, cancellationToken);
        var (client, target) = OpenProvider(connection);
        var remote = await FetchRemoteAsync(client, target, remoteId, cancellationToken);
        await EnsureBranchesExistAsync(client, target, remote.ProviderRepositoryId, branches, cancellationToken);

        return await WriteAsync(new WritePlan(connection, remote, branches, language, userId, ExistingRepositoryId: null, request.GenerateSkill), cancellationToken);
    }

    public async Task<ConnectedRepositoryResponse> AddIndexedBranchesAsync(
        string repositoryId, AddIndexedBranchesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = RequireUser();
        var branches = NormalizeBranches(request.Branches);
        var language = NormalizeLanguage(request.LanguageCode);

        var repository = await context.Repositories
            .AsNoTracking()
            .Where(RepositoryReadAccess.VisibleTo(userContext))
            .FirstOrDefaultAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken)
            ?? throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.RepositoryNotFound);
        if (string.IsNullOrWhiteSpace(repository.GitConnectionId)
            || repository.Provider is null
            || string.IsNullOrWhiteSpace(repository.ProviderRepositoryId))
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.RepositoryNotConnected);
        }

        var connection = await LoadUsableConnectionAsync(repository.GitConnectionId, cancellationToken);
        var (client, target) = OpenProvider(connection);
        var remote = await FetchRemoteAsync(client, target, repository.ProviderRepositoryId, cancellationToken);
        await EnsureBranchesExistAsync(client, target, remote.ProviderRepositoryId, branches, cancellationToken);

        return await WriteAsync(new WritePlan(connection, remote, branches, language, userId, repository.Id), cancellationToken);
    }

    public async Task<IReadOnlyList<IndexedBranchSummary>> ListIndexedBranchesAsync(string repositoryId, CancellationToken cancellationToken)
    {
        RequireUser();
        var repositoryExists = await context.Repositories
            .AsNoTracking()
            .Where(RepositoryReadAccess.VisibleTo(userContext))
            .AnyAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken);
        if (!repositoryExists)
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.RepositoryNotFound);
        }

        var branches = await context.RepositoryBranches
            .AsNoTracking()
            .Where(item => item.RepositoryId == repositoryId && !item.IsDeleted)
            .OrderBy(item => item.BranchName)
            .ToListAsync(cancellationToken);
        var branchIds = branches.Select(item => item.Id).ToList();

        var languages = (await context.BranchLanguages
                .AsNoTracking()
                .Where(item => branchIds.Contains(item.RepositoryBranchId) && !item.IsDeleted)
                .Select(item => new { item.RepositoryBranchId, item.LanguageCode })
                .ToListAsync(cancellationToken))
            .ToLookup(item => item.RepositoryBranchId, item => item.LanguageCode);
        var fullTasks = await context.BranchGenerationTasks
            .AsNoTracking()
            .Where(item => branchIds.Contains(item.BranchId) && !item.IsDeleted &&
                           (item.Status == BranchGenerationTaskStatus.Pending || item.Status == BranchGenerationTaskStatus.Processing))
            .ToListAsync(cancellationToken);
        var incrementalTasks = await context.IncrementalUpdateTasks
            .AsNoTracking()
            .Where(item => branchIds.Contains(item.BranchId) && !item.IsDeleted &&
                           (item.Status == IncrementalUpdateStatus.Pending || item.Status == IncrementalUpdateStatus.Processing))
            .ToListAsync(cancellationToken);

        return branches.Select(branch =>
        {
            var full = fullTasks.FirstOrDefault(item => item.BranchId == branch.Id);
            var incremental = incrementalTasks.FirstOrDefault(item => item.BranchId == branch.Id);
            return new IndexedBranchSummary(
                branch.Id,
                branch.BranchName,
                branch.LastCommitId,
                branch.GenerationStatus?.ToString(),
                branch.LastGenerationTaskId,
                branch.LastGenerationError,
                branch.LastProcessedAt,
                languages[branch.Id].Order().ToList(),
                full?.Id ?? incremental?.Id,
                full is not null ? "Full" : incremental is not null ? "Incremental" : null);
        }).ToList();
    }

    public async Task AdoptLegacyRepositoryAsync(string repositoryId, string connectionId, CancellationToken cancellationToken)
    {
        var userId = RequireUser();
        var connection = await LoadUsableConnectionAsync(RequireId(connectionId, 36), cancellationToken);
        EnsureSecretIsReadable(connection);

        await using var transaction = await EfContextTransaction.BeginIfSupportedAsync(context, cancellationToken);
        try
        {
            // Same guard as the connect path: a connection delete that read the old stamp fails its own check.
            await EnsureConnectionUsableAsync(connection.Id, cancellationToken);

            var repository = await context.Repositories.FirstOrDefaultAsync(
                item => item.Id == repositoryId && !item.IsDeleted, cancellationToken)
                ?? throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.RepositoryNotFound);

            if (repository.GitConnectionId == connection.Id)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(repository.GitConnectionId))
            {
                throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.AlreadyConnected);
            }

            // The credential travels only to the origin of the connection, and never inside a URL.
            if (!GitRemoteOriginGuard.IsSameOrigin(repository.GitUrl, connection.NormalizedServerUrl)
                || (repository.Provider is not null && repository.Provider != connection.Provider))
            {
                throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.RemoteInvalid);
            }

            repository.GitConnectionId = connection.Id;
            repository.Provider = connection.Provider;
            repository.ProviderBaseUrl = connection.NormalizedServerUrl;
            repository.UpdateTimestamp();
            auditor.Stage(repository, userId, GitConnectionAuditEventType.RepositoryAssigned);

            await context.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            EfContextTransaction.ClearPendingChanges(context);
            throw;
        }
    }

    // ---- provider access ----

    private async Task<GitConnection> LoadUsableConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        var connection = await context.GitConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == connectionId && !item.IsDeleted, cancellationToken)
            ?? throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.ConnectionNotFound);

        if (!connection.IsEnabled)
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.ConnectionDisabled);
        }

        if (!authorization.CanUse(connection))
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.Unauthorized);
        }

        return connection;
    }

    private void EnsureSecretIsReadable(GitConnection connection)
    {
        try
        {
            protector.Unprotect(connection.ProtectedToken);
        }
        catch (GitConnectionSecretException)
        {
            logger.LogWarning("Git connection secret cannot be read. ConnectionId: {ConnectionId}", connection.Id);
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.ConnectionSecretUnreadable);
        }
    }

    private (IGitProviderClient Client, GitProviderTarget Target) OpenProvider(GitConnection connection)
    {
        string token;
        try
        {
            token = protector.Unprotect(connection.ProtectedToken);
        }
        catch (GitConnectionSecretException)
        {
            logger.LogWarning("Git connection secret cannot be read. ConnectionId: {ConnectionId}", connection.Id);
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.ConnectionSecretUnreadable);
        }

        return (
            clients.Resolve(connection.Provider),
            new GitProviderTarget(connection.Id, connection.Provider, connection.NormalizedServerUrl, token));
    }

    private static async Task<RemoteRepository> FetchRemoteAsync(
        IGitProviderClient client, GitProviderTarget target, string remoteId, CancellationToken cancellationToken)
    {
        var remote = await client.GetRepositoryAsync(target, remoteId, cancellationToken);
        // The path becomes a URL, a route segment, and a directory name, so it must be plain path segments.
        if (!TryDeriveNames(remote, out _, out _))
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.RemoteInvalid);
        }

        return remote;
    }

    /// <summary>
    /// Every selected branch must exist on the remote before any write starts, so a stale selection never leaves
    /// a partial batch behind. The provider lists branches by page; the scan stops when all names are found.
    /// </summary>
    private static async Task EnsureBranchesExistAsync(
        IGitProviderClient client,
        GitProviderTarget target,
        string remoteId,
        IReadOnlyList<string> branches,
        CancellationToken cancellationToken)
    {
        var missing = new HashSet<string>(branches, StringComparer.Ordinal);
        string? cursor = null;
        var listingFinished = false;
        for (var page = 0; page < MaxBranchPages && missing.Count > 0; page++)
        {
            var result = await client.ListBranchesAsync(target, remoteId, cursor, BranchPageSize, cancellationToken);
            foreach (var branch in result.Items)
            {
                missing.Remove(branch.Name);
            }

            if (result.NextCursor is null)
            {
                listingFinished = true;
                break;
            }

            cursor = result.NextCursor;
        }

        if (missing.Count == 0)
        {
            return;
        }

        var names = branches.Where(missing.Contains).ToList();
        throw new ConnectedRepositoryException(
            listingFinished ? ConnectedRepositoryErrorCodes.RemoteBranchNotFound : ConnectedRepositoryErrorCodes.RemoteBranchLookupLimit,
            names);
    }

    // ---- the atomic write ----

    private async Task<ConnectedRepositoryResponse> WriteAsync(WritePlan plan, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = await EfContextTransaction.BeginIfSupportedAsync(context, cancellationToken);
            try
            {
                var outcome = await WriteOnceAsync(plan, cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                return await BuildResponseAsync(plan, outcome, cancellationToken);
            }
            catch (DbUpdateException ex) when (attempt < MaxWriteAttempts)
            {
                // A concurrent request can win a unique index (the remote identity, the route slug, a lock).
                // The next attempt re-reads, so it converges on what the winner stored.
                logger.LogInformation(ex, "Connected repository write conflicted; reading again. Attempt: {Attempt}", attempt);
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }

                EfContextTransaction.ClearPendingChanges(context);
            }
            catch (DbUpdateException ex)
            {
                logger.LogWarning(ex, "Connected repository write failed after {Attempts} attempts", attempt);
                throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.Conflict);
            }
            catch (Exception)
            {
                EfContextTransaction.ClearPendingChanges(context);
                throw;
            }
        }
    }

    private async Task<WriteOutcome> WriteOnceAsync(WritePlan plan, CancellationToken cancellationToken)
    {
        // First statement of the transaction: it proves that the connection still can be used and changes its
        // stamp. A connection delete that read the old stamp then fails its stamp check instead of deleting a
        // connection that this write is about to reference. It also makes concurrent writes of one connection wait in turn.
        await EnsureConnectionUsableAsync(plan.Connection.Id, cancellationToken);

        var remote = plan.Remote;
        var created = false;
        var attached = false;

        var repository = plan.ExistingRepositoryId is null
            ? await FindByIdentityAsync(plan.Connection, remote.ProviderRepositoryId, cancellationToken)
              ?? await FindAdoptableLegacyAsync(plan.Connection, remote, cancellationToken)
            : await context.Repositories.FirstOrDefaultAsync(
                item => item.Id == plan.ExistingRepositoryId && !item.IsDeleted, cancellationToken);

        if (repository is null && plan.ExistingRepositoryId is not null)
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.RepositoryNotFound);
        }

        if (repository is null)
        {
            repository = await CreateRepositoryAsync(plan, cancellationToken);
            created = true;
        }
        else
        {
            attached = await RefreshRepositoryAsync(repository, plan, cancellationToken);
        }

        if (repository.Status is RepositoryStatus.Pending or RepositoryStatus.Processing)
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.RepositoryGenerationActive);
        }

        var branchPlans = await StageBranchesAsync(repository, plan, cancellationToken);

        // The repository, branches, and languages go to the database first. A unique index conflict surfaces here
        // as a write failure that the caller retries, and not as a failed lock below.
        await context.SaveChangesAsync(cancellationToken);

        foreach (var branchPlan in branchPlans.Where(item => item.NewTask is not null))
        {
            var task = branchPlan.NewTask!;
            var acquired = await lockService.TryAcquireAsync(
                context,
                repository.Id,
                RepositoryGenerationLockOwnerType.BranchTask,
                task.Id,
                RepositoryGenerationLockScope.Branch,
                cancellationToken,
                branchId: branchPlan.Branch.Id);
            if (!acquired)
            {
                throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.GenerationLockConflict);
            }

            context.BranchGenerationTasks.Add(task);
            auditor.Stage(repository, plan.UserId, GitConnectionAuditEventType.BranchAdded);
        }

        if (created || attached)
        {
            auditor.Stage(repository, plan.UserId, GitConnectionAuditEventType.RepositoryAssigned);
        }

        await context.SaveChangesAsync(cancellationToken);
        return new WriteOutcome(repository, created, branchPlans);
    }

    private async Task EnsureConnectionUsableAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (await TouchConnectionAsync(connectionId, cancellationToken))
        {
            return;
        }

        var current = await context.GitConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == connectionId, cancellationToken);
        throw new ConnectedRepositoryException(
            current is null or { IsDeleted: true }
                ? ConnectedRepositoryErrorCodes.ConnectionNotFound
                : ConnectedRepositoryErrorCodes.ConnectionDisabled);
    }

    /// <summary>
    /// Changes the stamp of a connection that is not deleted and is enabled.
    /// </summary>
    /// <returns>False when the connection is deleted, disabled, or missing.</returns>
    private async Task<bool> TouchConnectionAsync(string connectionId, CancellationToken cancellationToken)
        => EfContextCapabilities.SupportsExecuteUpdate(context)
            ? await TouchConnectionWithUpdateAsync(connectionId, cancellationToken)
            : await TouchConnectionTrackedAsync(connectionId, cancellationToken);

    private async Task<bool> TouchConnectionWithUpdateAsync(string connectionId, CancellationToken cancellationToken)
    {
        var stamp = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;
        var rows = await context.GitConnections
            .Where(item => item.Id == connectionId && !item.IsDeleted && item.IsEnabled)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.ConcurrencyStamp, stamp)
                    .SetProperty(item => item.UpdatedAt, now),
                cancellationToken);
        return rows == 1;
    }

    private async Task<bool> TouchConnectionTrackedAsync(string connectionId, CancellationToken cancellationToken)
    {
        var connection = await context.GitConnections
            .FirstOrDefaultAsync(item => item.Id == connectionId && !item.IsDeleted && item.IsEnabled, cancellationToken);
        if (connection is null)
        {
            return false;
        }

        connection.ConcurrencyStamp = Guid.NewGuid().ToString();
        connection.UpdateTimestamp();
        return true;
    }

    // ---- repository get-or-create ----

    private Task<Repository?> FindByIdentityAsync(GitConnection connection, string remoteId, CancellationToken cancellationToken)
        => context.Repositories.FirstOrDefaultAsync(
            item => item.Provider == connection.Provider
                    && item.ProviderBaseUrl == connection.NormalizedServerUrl
                    && item.ProviderRepositoryId == remoteId
                    && !item.IsDeleted,
            cancellationToken);

    /// <summary>
    /// A repository registered by URL before stable identities existed names the same remote when its URL matches.
    /// Adopting it keeps one repository per remote. URLs with user info never match, so a credential in a URL
    /// cannot link a row to a connection.
    /// </summary>
    private async Task<Repository?> FindAdoptableLegacyAsync(
        GitConnection connection, RemoteRepository remote, CancellationToken cancellationToken)
    {
        TryDeriveNames(remote, out _, out var repoName);
        var lowerName = repoName.ToLowerInvariant();
        var candidates = await context.Repositories
            .Where(item => item.ProviderRepositoryId == null
                           && !item.IsDeleted
                           && item.RepoName.ToLower() == lowerName)
            .ToListAsync(cancellationToken);

        var remoteUrl = BuildGitUrl(connection, remote);
        // Duplicate rows of one remote must give the same answer on every run: an assigned row first, then the
        // oldest row, then the lowest ID.
        return candidates
            .Where(item => GitRemoteOriginGuard.IsSameRemote(item.GitUrl, remoteUrl))
            .OrderByDescending(item => item.GitConnectionId != null)
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private async Task<Repository> CreateRepositoryAsync(WritePlan plan, CancellationToken cancellationToken)
    {
        var (org, name) = await ChooseNamesAsync(plan.Connection, plan.Remote, selfId: null, cancellationToken);
        var repository = new Repository
        {
            Id = Guid.NewGuid().ToString(),
            OwnerUserId = plan.UserId,
            GitUrl = BuildGitUrl(plan.Connection, plan.Remote),
            OrgName = org,
            RepoName = name,
            // The first generation is queued as branch tasks. Pending would send the repository to the
            // repository-wide worker, which runs branches one after another.
            Status = RepositoryStatus.Completed,
            GitConnectionId = plan.Connection.Id,
            GenerateSkill = plan.GenerateSkill
        };
        ApplyRemoteMetadata(repository, plan);
        context.Repositories.Add(repository);
        return repository;
    }

    /// <summary>
    /// Refreshes provider metadata of a repository that already exists and links it to the connection when it has
    /// none (an App import, or a legacy row) or when its connection is deleted. A repository of another live
    /// connection fails before any change, so another user's request cannot change its metadata or use its credential.
    /// </summary>
    /// <returns>True when the connection link was set by this call.</returns>
    private async Task<bool> RefreshRepositoryAsync(Repository repository, WritePlan plan, CancellationToken cancellationToken)
    {
        var attached = false;
        if (repository.GitConnectionId is null)
        {
            repository.GitConnectionId = plan.Connection.Id;
            attached = true;
        }
        else if (repository.GitConnectionId != plan.Connection.Id)
        {
            var existing = await context.GitConnections
                .AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == repository.GitConnectionId, cancellationToken);
            if (existing is null or { IsDeleted: true })
            {
                repository.GitConnectionId = plan.Connection.Id;
                attached = true;
            }
            else if (!existing.IsEnabled)
            {
                // Disabling a connection blocks new indexing of its repositories, whoever asks.
                throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.ConnectionDisabled);
            }
            else
            {
                // A repository of another live connection is not changed by a manual request. Only the legacy
                // migration links a repository that exists already.
                throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.AlreadyConnected);
            }
        }

        repository.Provider = plan.Connection.Provider;
        repository.ProviderBaseUrl = plan.Connection.NormalizedServerUrl;
        repository.ProviderRepositoryId = plan.Remote.ProviderRepositoryId;
        repository.GitUrl = BuildGitUrl(plan.Connection, plan.Remote);
        ApplyRemoteMetadata(repository, plan);

        TryDeriveNames(plan.Remote, out var org, out var name);
        if (!KeepsCurrentNames(repository, org, name))
        {
            (repository.OrgName, repository.RepoName) = await ChooseNamesAsync(plan.Connection, plan.Remote, repository.Id, cancellationToken);
        }

        repository.UpdateTimestamp();
        return attached;
    }

    private static void ApplyRemoteMetadata(Repository repository, WritePlan plan)
    {
        var remote = plan.Remote;
        repository.Provider = plan.Connection.Provider;
        repository.ProviderBaseUrl = plan.Connection.NormalizedServerUrl;
        repository.ProviderRepositoryId = remote.ProviderRepositoryId;
        repository.DefaultBranch = Truncate(remote.DefaultBranch, MaxBranchNameLength);
        repository.Description = Truncate(remote.Description, MaxDescriptionLength);
        repository.IsPublic = string.Equals(remote.Visibility, "Public", StringComparison.OrdinalIgnoreCase);
    }

    private static bool KeepsCurrentNames(Repository repository, string org, string name)
        => repository.RepoName == name
           && (repository.OrgName == org || repository.OrgName.StartsWith(org + "~", StringComparison.Ordinal));

    /// <summary>
    /// Route and workspace paths are keyed by organization and repository name, and that pair stays unique.
    /// The first free name wins: the provider's own path, then the path with the provider, then numbered variants.
    /// </summary>
    private async Task<(string Org, string Name)> ChooseNamesAsync(
        GitConnection connection, RemoteRepository remote, string? selfId, CancellationToken cancellationToken)
    {
        TryDeriveNames(remote, out var org, out var name);
        var provider = connection.Provider.ToString().ToLowerInvariant();
        for (var index = 0; index < 1000; index++)
        {
            var suffix = index switch
            {
                0 => string.Empty,
                1 => $"~{provider}",
                _ => $"~{provider}~{index}"
            };
            var candidate = Truncate(org, MaxNameLength - suffix.Length) + suffix;
            var taken = await context.Repositories.AnyAsync(
                item => item.OrgName == candidate && item.RepoName == name && item.Id != selfId, cancellationToken);
            if (!taken)
            {
                return (candidate, name);
            }
        }

        throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.Conflict);
    }

    private static bool TryDeriveNames(RemoteRepository remote, out string org, out string name)
    {
        org = string.Empty;
        name = string.Empty;
        var segments = (remote.FullName ?? string.Empty).Trim('/').Split('/');
        if (segments.Length < 2 || segments.Any(segment => !IsSafePathSegment(segment)))
        {
            return false;
        }

        // A GitLab subgroup path has more than two segments. The route needs one segment, so the namespace is joined.
        org = Truncate(string.Join('_', segments[..^1]), MaxNameLength)!;
        name = Truncate(segments[^1], MaxNameLength)!;
        return true;
    }

    private static bool IsSafePathSegment(string segment)
        => segment.Length > 0
           && segment is not "." and not ".."
           && !segment.Contains("..", StringComparison.Ordinal)
           && PathSegmentPattern.IsMatch(segment);

    /// <summary>
    /// The clone URL is built from the connection origin and the provider path, not taken from the provider answer,
    /// so the credential can only ever go to the origin that the connection stores.
    /// </summary>
    private static string BuildGitUrl(GitConnection connection, RemoteRepository remote)
        => $"{connection.NormalizedServerUrl}/{remote.FullName.Trim('/')}.git";

    // ---- branches ----

    private async Task<List<BranchPlan>> StageBranchesAsync(Repository repository, WritePlan plan, CancellationToken cancellationToken)
    {
        var existing = await context.RepositoryBranches
            .Where(item => item.RepositoryId == repository.Id && plan.Branches.Contains(item.BranchName))
            .ToListAsync(cancellationToken);
        var existingIds = existing.Select(item => item.Id).ToList();
        var activeFullTasks = await context.BranchGenerationTasks
            .Where(item => existingIds.Contains(item.BranchId) && !item.IsDeleted &&
                           (item.Status == BranchGenerationTaskStatus.Pending || item.Status == BranchGenerationTaskStatus.Processing))
            .ToListAsync(cancellationToken);

        var plans = new List<BranchPlan>();
        foreach (var name in plan.Branches)
        {
            var branch = existing.FirstOrDefault(item => string.Equals(item.BranchName, name, StringComparison.Ordinal));
            if (branch is { IsDeleted: false })
            {
                plans.Add(new BranchPlan(branch, NewTask: null, activeFullTasks.FirstOrDefault(item => item.BranchId == branch.Id)));
                continue;
            }

            if (branch is null)
            {
                branch = new RepositoryBranch { Id = Guid.NewGuid().ToString(), RepositoryId = repository.Id, BranchName = name };
                context.RepositoryBranches.Add(branch);
                context.BranchLanguages.Add(NewLanguage(branch.Id, plan.Language));
            }
            else
            {
                await RestoreBranchAsync(branch, plan.Language, activeFullTasks, cancellationToken);
            }

            var task = BranchGenerationTaskService.NewFullGenerationTask(
                repository.Id, branch.Id, plan.UserId, FullGenerationPriority);
            BranchGenerationTaskService.MarkBranchQueued(branch, task.Id);
            plans.Add(new BranchPlan(branch, task, ActiveTask: null));
        }

        return plans;
    }

    /// <summary>
    /// A soft-deleted branch row would block the same name through the unique index. It is restored as a fresh branch:
    /// no baseline commit, a pending state, one active language, and no leftover work.
    /// </summary>
    private async Task RestoreBranchAsync(
        RepositoryBranch branch, string language, List<BranchGenerationTask> activeTasks, CancellationToken cancellationToken)
    {
        branch.IsDeleted = false;
        branch.DeletedAt = null;
        branch.LastCommitId = null;
        branch.LastProcessedAt = null;
        branch.LastGenerationError = null;
        branch.LastGenerationStartedAt = null;
        branch.LastGenerationCompletedAt = null;
        branch.UpdateTimestamp();

        foreach (var stale in activeTasks.Where(item => item.BranchId == branch.Id))
        {
            stale.Status = BranchGenerationTaskStatus.Cancelled;
            stale.CompletedAt = DateTime.UtcNow;
            stale.UpdateTimestamp();
        }

        var staleLocks = await context.RepositoryGenerationLocks
            .Where(item => item.BranchId == branch.Id)
            .ToListAsync(cancellationToken);
        context.RepositoryGenerationLocks.RemoveRange(staleLocks);

        var languages = await context.BranchLanguages
            .Where(item => item.RepositoryBranchId == branch.Id)
            .ToListAsync(cancellationToken);
        var match = languages.FirstOrDefault(item => string.Equals(item.LanguageCode, language, StringComparison.Ordinal));
        if (match is null)
        {
            context.BranchLanguages.Add(NewLanguage(branch.Id, language));
        }
        else
        {
            match.IsDeleted = false;
            match.DeletedAt = null;
            match.UpdateTimestamp();
        }
    }

    private static BranchLanguage NewLanguage(string branchId, string language) => new()
    {
        Id = Guid.NewGuid().ToString(),
        RepositoryBranchId = branchId,
        LanguageCode = language,
        UpdateSummary = string.Empty,
        IsDefault = true
    };

    private async Task<ConnectedRepositoryResponse> BuildResponseAsync(
        WritePlan plan, WriteOutcome outcome, CancellationToken cancellationToken)
    {
        var branchIds = outcome.Branches.Select(item => item.Branch.Id).ToList();
        var activeTasks = await context.BranchGenerationTasks
            .AsNoTracking()
            .Where(item => branchIds.Contains(item.BranchId) && !item.IsDeleted &&
                           (item.Status == BranchGenerationTaskStatus.Pending || item.Status == BranchGenerationTaskStatus.Processing))
            .ToListAsync(cancellationToken);

        var results = outcome.Branches.Select(item =>
        {
            var task = item.NewTask ?? item.ActiveTask ?? activeTasks.FirstOrDefault(active => active.BranchId == item.Branch.Id);
            return new IndexedBranchResult(
                item.Branch.Id, item.Branch.BranchName, item.NewTask is not null, task?.Id, task?.Status.ToString());
        }).ToList();

        return new ConnectedRepositoryResponse(
            outcome.Repository.Id,
            outcome.Repository.OrgName,
            outcome.Repository.RepoName,
            outcome.Repository.GitConnectionId ?? plan.Connection.Id,
            outcome.Created,
            results);
    }

    // ---- validation ----

    private string RequireUser()
    {
        var userId = userContext.UserId;
        return userContext.IsAuthenticated && !string.IsNullOrWhiteSpace(userId)
            ? userId
            : throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.Unauthorized);
    }

    private static string RequireId(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > maxLength || trimmed.Any(char.IsControl)
            ? throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.InvalidRequest)
            : trimmed;
    }

    private static string NormalizeLanguage(string? languageCode)
    {
        var trimmed = languageCode?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return DefaultLanguageCode;
        }

        return trimmed.Length <= 50 && LanguageCodePattern.IsMatch(trimmed)
            ? trimmed
            : throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.InvalidRequest);
    }

    private static IReadOnlyList<string> NormalizeBranches(IReadOnlyList<string>? branches)
    {
        var names = (branches ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (names.Count == 0)
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.NoBranchesSelected);
        }

        if (names.Count > MaxBranchesPerRequest)
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.TooManyBranches);
        }

        if (names.Any(name => !IsValidBranchName(name)))
        {
            throw new ConnectedRepositoryException(ConnectedRepositoryErrorCodes.InvalidBranchName);
        }

        return names;
    }

    /// <summary>
    /// The rules of <c>git check-ref-format</c> that matter here: the name ends up in refs, URLs, and directory names.
    /// </summary>
    private static bool IsValidBranchName(string name)
        => name.Length <= MaxBranchNameLength
           && !name.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || "~^:?*[\\".Contains(character))
           && !name.Contains("..", StringComparison.Ordinal)
           && !name.Contains("@{", StringComparison.Ordinal)
           && !name.Contains("//", StringComparison.Ordinal)
           && !name.StartsWith('-') && !name.StartsWith('/')
           && !name.EndsWith('/') && !name.EndsWith('.')
           && !name.EndsWith(".lock", StringComparison.Ordinal)
           && name != "@";

    private static string? Truncate(string? value, int maxLength)
        => value is null || value.Length <= maxLength ? value : value[..maxLength];

    private sealed record WritePlan(
        GitConnection Connection,
        RemoteRepository Remote,
        IReadOnlyList<string> Branches,
        string Language,
        string UserId,
        string? ExistingRepositoryId,
        bool GenerateSkill = true);

    private sealed record BranchPlan(RepositoryBranch Branch, BranchGenerationTask? NewTask, BranchGenerationTask? ActiveTask);

    private sealed record WriteOutcome(Repository Repository, bool Created, IReadOnlyList<BranchPlan> Branches);
}
