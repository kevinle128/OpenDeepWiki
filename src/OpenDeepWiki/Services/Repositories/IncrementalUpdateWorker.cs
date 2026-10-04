using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Wiki;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Background worker that processes manual incremental tasks and performs scheduled scans.
/// </summary>
public class IncrementalUpdateWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IncrementalUpdateWorker> _logger;
    private readonly IncrementalUpdateOptions _options;

    public IncrementalUpdateWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<IncrementalUpdateWorker> logger,
        IOptions<IncrementalUpdateOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "IncrementalUpdateWorker started. PollingInterval: {PollingInterval}s, ScheduledEnabled: {Enabled}",
            _options.PollingIntervalSeconds,
            _options.Enabled);

        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingTasksAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during incremental update polling");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.PollingIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("IncrementalUpdateWorker stopped gracefully");
    }

    private async Task ProcessPendingTasksAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var gitPlatformService = scope.ServiceProvider.GetRequiredService<IGitPlatformService>();
        var repositoryAnalyzer = scope.ServiceProvider.GetRequiredService<IRepositoryAnalyzer>();
        var coordinator = scope.ServiceProvider.GetService<IWikiGenerationCoordinator>();
        var wikiOptions = scope.ServiceProvider.GetService<IOptionsMonitor<WikiGeneratorOptions>>();
        var visibilityProbe = scope.ServiceProvider.GetService<IRepositoryVisibilityProbe>();

        if (coordinator is not null)
        {
            await coordinator.RecoverStaleWorkAsync(context, stoppingToken);
        }

        var pendingTasks = await GetPendingTasksAsync(context, stoppingToken);

        foreach (var task in pendingTasks)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Cancellation requested, stopping task processing");
                break;
            }

            var shouldStop = await ProcessSingleTaskAsync(
                coordinator,
                wikiOptions?.CurrentValue,
                task,
                stoppingToken);
            if (shouldStop)
            {
                break;
            }
        }

        await CheckScheduledUpdatesAsync(context, gitPlatformService, repositoryAnalyzer, visibilityProbe, stoppingToken);
    }

    private async Task<List<IncrementalUpdateTask>> GetPendingTasksAsync(
        IContext context,
        CancellationToken stoppingToken)
    {
        return await context.IncrementalUpdateTasks
            .AsNoTracking()
            .Where(t => !t.IsDeleted && t.Status == IncrementalUpdateStatus.Pending)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .Take(20)
            .ToListAsync(stoppingToken);
    }

    private async Task<bool> ProcessSingleTaskAsync(
        IWikiGenerationCoordinator? coordinator,
        WikiGeneratorOptions? wikiOptions,
        IncrementalUpdateTask pendingTask,
        CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IContext>();
        var updateService = scope.ServiceProvider.GetRequiredService<IIncrementalUpdateService>();
        var jobCoordinator = coordinator ?? scope.ServiceProvider.GetService<IWikiGenerationCoordinator>();

        WikiGenerationWorkLease? lease = null;
        if (jobCoordinator is not null)
        {
            var (status, acquiredLease) = await jobCoordinator.TryBeginAsync(
                context,
                pendingTask.RepositoryId,
                RepositoryGenerationLockOwnerType.IncrementalTask,
                pendingTask.Id,
                RepositoryGenerationLockScope.Branch,
                WikiGenerationWorkType.IncrementalTask,
                stoppingToken,
                pendingTask.BranchId);

            if (status == WikiGenerationAcquireStatus.ClusterFull)
            {
                _logger.LogInformation(
                    "Wiki generation cluster is full, deferring incremental updates. TaskId: {TaskId}",
                    pendingTask.Id);
                return true;
            }

            if (status != WikiGenerationAcquireStatus.Acquired || acquiredLease is null)
            {
                _logger.LogDebug(
                    "Skipping incremental task because the branch or repository is busy. TaskId: {TaskId}, RepositoryId: {RepositoryId}, BranchId: {BranchId}",
                    pendingTask.Id, pendingTask.RepositoryId, pendingTask.BranchId);
                return false;
            }

            lease = acquiredLease;
        }

        var task = await TryClaimIncrementalTaskAsync(context, pendingTask.Id, stoppingToken);
        if (task is null)
        {
            if (lease is not null && jobCoordinator is not null)
            {
                await jobCoordinator.ReleaseAsync(context, lease, CancellationToken.None);
            }

            return false;
        }

        IAsyncDisposable? heartbeat = lease is not null && wikiOptions is not null
            ? WikiGenerationHeartbeat.Start(_scopeFactory, lease, wikiOptions, _logger, stoppingToken)
            : null;

        _logger.LogInformation(
            "Processing task. TaskId: {TaskId}, RepositoryId: {RepositoryId}, BranchId: {BranchId}, Priority: {Priority}",
            task.Id, task.RepositoryId, task.BranchId, task.Priority);

        var needsFullGeneration = false;
        try
        {
            var result = await updateService.ProcessIncrementalUpdateAsync(
                task.RepositoryId, task.BranchId, stoppingToken);

            if (result.Success)
            {
                task.TargetCommitId = result.CurrentCommitId;
                await UpdateTaskStatusAsync(
                    context, task, IncrementalUpdateStatus.Completed, null, stoppingToken);

                _logger.LogInformation(
                    "Task completed successfully. TaskId: {TaskId}, ChangedFiles: {ChangedFiles}, Duration: {Duration}ms",
                    task.Id, result.ChangedFilesCount, result.Duration.TotalMilliseconds);
            }
            else if (result.RequiresFullGeneration)
            {
                needsFullGeneration = true;
                await UpdateTaskStatusAsync(
                    context, task, IncrementalUpdateStatus.Cancelled, result.ErrorMessage, stoppingToken);

                _logger.LogInformation(
                    "Task replaced by a full generation because source files were deleted. TaskId: {TaskId}",
                    task.Id);
            }
            else
            {
                await UpdateTaskStatusAsync(
                    context, task, IncrementalUpdateStatus.Failed, result.ErrorMessage, stoppingToken);

                _logger.LogWarning(
                    "Task failed. TaskId: {TaskId}, Error: {Error}",
                    task.Id, result.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Task processing cancelled. TaskId: {TaskId}", task.Id);
            await UpdateTaskStatusAsync(
                context, task, IncrementalUpdateStatus.Pending, null, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await UpdateTaskStatusAsync(
                context, task, IncrementalUpdateStatus.Failed, ex.Message, stoppingToken);

            _logger.LogError(ex,
                "Task processing failed with exception. TaskId: {TaskId}",
                task.Id);
        }
        finally
        {
            if (heartbeat is not null)
            {
                await heartbeat.DisposeAsync();
            }

            if (lease is not null && jobCoordinator is not null)
            {
                await jobCoordinator.ReleaseAsync(context, lease, CancellationToken.None);
            }
        }

        if (needsFullGeneration)
        {
            // The full generation reserves the lock of the same branch, so it can start only after the lease above is released.
            await EnqueueFullGenerationAsync(scope.ServiceProvider, task, stoppingToken);
        }

        return false;
    }

    private async Task EnqueueFullGenerationAsync(
        IServiceProvider services,
        IncrementalUpdateTask task,
        CancellationToken stoppingToken)
    {
        var taskService = services.GetService<IBranchGenerationTaskService>();
        if (taskService is null)
        {
            _logger.LogWarning(
                "Full generation could not be queued because the task service is unavailable. TaskId: {TaskId}",
                task.Id);
            return;
        }

        var result = await taskService.EnqueueFullGenerationAsync(
            task.RepositoryId, task.BranchId, task.RequestedBy, cancellationToken: stoppingToken);
        if (!result.Success && result.ErrorCode != "BRANCH_GENERATION_ACTIVE")
        {
            _logger.LogWarning(
                "Full generation after deleted files was not queued. TaskId: {TaskId}, ErrorCode: {ErrorCode}",
                task.Id, result.ErrorCode);
        }
    }

    private static async Task<IncrementalUpdateTask?> TryClaimIncrementalTaskAsync(
        IContext context,
        string taskId,
        CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;

        if (EfContextCapabilities.SupportsExecuteUpdate(context))
        {
            var updatedRows = await context.IncrementalUpdateTasks
                .Where(item => item.Id == taskId &&
                               !item.IsDeleted &&
                               item.Status == IncrementalUpdateStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(item => item.Status, IncrementalUpdateStatus.Processing)
                        .SetProperty(item => item.StartedAt, now)
                        .SetProperty(item => item.CompletedAt, (DateTime?)null)
                        .SetProperty(item => item.ErrorMessage, (string?)null)
                        .SetProperty(item => item.UpdatedAt, now),
                    stoppingToken);

            if (updatedRows != 1)
            {
                return null;
            }

            return await context.IncrementalUpdateTasks.FirstAsync(item => item.Id == taskId, stoppingToken);
        }

        var task = await context.IncrementalUpdateTasks
            .FirstOrDefaultAsync(item => item.Id == taskId &&
                                         !item.IsDeleted &&
                                         item.Status == IncrementalUpdateStatus.Pending,
                stoppingToken);
        if (task is null)
        {
            return null;
        }

        task.Status = IncrementalUpdateStatus.Processing;
        task.StartedAt = now;
        task.CompletedAt = null;
        task.ErrorMessage = null;
        task.UpdatedAt = now;
        await context.SaveChangesAsync(stoppingToken);
        return task;
    }

    private async Task UpdateTaskStatusAsync(
        IContext context,
        IncrementalUpdateTask task,
        IncrementalUpdateStatus status,
        string? errorMessage,
        CancellationToken stoppingToken)
    {
        task.Status = status;
        task.ErrorMessage = errorMessage;
        task.UpdatedAt = DateTime.UtcNow;

        switch (status)
        {
            case IncrementalUpdateStatus.Processing:
                task.StartedAt = DateTime.UtcNow;
                break;
            case IncrementalUpdateStatus.Completed:
            case IncrementalUpdateStatus.Failed:
                task.CompletedAt = DateTime.UtcNow;
                if (status == IncrementalUpdateStatus.Failed)
                {
                    task.RetryCount++;
                }
                break;
        }

        await context.SaveChangesAsync(stoppingToken);
    }

    private async Task CheckScheduledUpdatesAsync(
        IContext context,
        IGitPlatformService gitPlatformService,
        IRepositoryAnalyzer repositoryAnalyzer,
        IRepositoryVisibilityProbe? visibilityProbe,
        CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("Scheduled incremental scans are disabled");
            return;
        }

        var now = DateTime.UtcNow;
        var defaultInterval = Math.Max(_options.DefaultUpdateIntervalMinutes, _options.MinUpdateIntervalMinutes);
        var maxRepositoriesPerPoll = Math.Max(1, _options.MaxRepositoriesPerPoll);

        var repositoriesToCheck = await context.Repositories
            .Where(r => !r.IsDeleted && r.Status == RepositoryStatus.Completed)
            .Where(r => r.LastUpdateCheckAt == null ||
                        r.LastUpdateCheckAt.Value.AddMinutes(
                            r.UpdateIntervalMinutes == null
                                ? defaultInterval
                                : (r.UpdateIntervalMinutes.Value < _options.MinUpdateIntervalMinutes
                                    ? _options.MinUpdateIntervalMinutes
                                    : r.UpdateIntervalMinutes.Value)) <= now)
            .OrderBy(r => r.LastUpdateCheckAt ?? DateTime.MinValue)
            .Take(maxRepositoriesPerPoll)
            .ToListAsync(stoppingToken);

        foreach (var repository in repositoriesToCheck)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await SyncRepositoryVisibilityAsync(context, gitPlatformService, visibilityProbe, repository, stoppingToken);
            await CreateScheduledUpdateTasksAsync(context, repositoryAnalyzer, repository, stoppingToken);
        }
    }

    private async Task SyncRepositoryVisibilityAsync(
        IContext context,
        IGitPlatformService gitPlatformService,
        IRepositoryVisibilityProbe? visibilityProbe,
        Repository repository,
        CancellationToken stoppingToken)
    {
        try
        {
            var shouldBePublic = await ResolveCurrentVisibilityAsync(
                gitPlatformService, visibilityProbe, repository, stoppingToken);
            if (shouldBePublic is null)
            {
                return;
            }

            if (repository.IsPublic != shouldBePublic)
            {
                _logger.LogInformation(
                    "Visibility mismatch detected for {Org}/{Repo}: DB={DbVisibility}, Actual={ActualVisibility}. Updating.",
                    repository.OrgName, repository.RepoName,
                    repository.IsPublic ? "Public" : "Private",
                    shouldBePublic.Value ? "Public" : "Private");

                repository.IsPublic = shouldBePublic.Value;
                repository.UpdatedAt = DateTime.UtcNow;
                await context.SaveChangesAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to sync visibility for {Org}/{Repo}",
                repository.OrgName, repository.RepoName);
        }
    }

    /// <summary>
    /// A repository with a provider identity is asked on its own provider. Only a repository without one that lives
    /// on github.com uses the GitHub checker; every other host has no known way to answer, so its visibility stays.
    /// </summary>
    private static async Task<bool?> ResolveCurrentVisibilityAsync(
        IGitPlatformService gitPlatformService,
        IRepositoryVisibilityProbe? visibilityProbe,
        Repository repository,
        CancellationToken stoppingToken)
    {
        if (repository.Provider is not null)
        {
            return visibilityProbe is null
                ? null
                : await visibilityProbe.GetIsPublicAsync(repository, stoppingToken);
        }

        if (!IsGitHubHost(repository.GitUrl) ||
            string.IsNullOrWhiteSpace(repository.OrgName) ||
            string.IsNullOrWhiteSpace(repository.RepoName))
        {
            return null;
        }

        var repoInfo = await gitPlatformService.CheckRepoExistsAsync(repository.OrgName, repository.RepoName);
        return repoInfo.Exists ? !repoInfo.IsPrivate : null;
    }

    private static bool IsGitHubHost(string gitUrl)
    {
        try
        {
            return new Uri(gitUrl).Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task CreateScheduledUpdateTasksAsync(
        IContext context,
        IRepositoryAnalyzer repositoryAnalyzer,
        Repository repository,
        CancellationToken stoppingToken)
    {
        try
        {
            var branches = await context.RepositoryBranches
                .Where(b => b.RepositoryId == repository.Id && !b.IsDeleted)
                .ToListAsync(stoppingToken);

            var sourceInfo = RepositorySource.Parse(repository.GitUrl);
            var saveChanges = false;

            foreach (var branch in branches)
            {
                var existingTask = await context.IncrementalUpdateTasks
                    .AnyAsync(t => !t.IsDeleted &&
                                   t.RepositoryId == repository.Id &&
                                   t.BranchId == branch.Id &&
                                   (t.Status == IncrementalUpdateStatus.Pending
                                       || t.Status == IncrementalUpdateStatus.Processing),
                        stoppingToken);

                if (existingTask)
                {
                    _logger.LogDebug(
                        "Skipping scheduled update, task already exists. Repository: {Org}/{Repo}, Branch: {Branch}",
                        repository.OrgName, repository.RepoName, branch.BranchName);
                    continue;
                }

                var activeBranchGenerationTask = await context.BranchGenerationTasks
                    .AnyAsync(t => !t.IsDeleted &&
                                   t.RepositoryId == repository.Id &&
                                   t.BranchId == branch.Id &&
                                   (t.Status == BranchGenerationTaskStatus.Pending ||
                                    t.Status == BranchGenerationTaskStatus.Processing),
                        stoppingToken);

                if (activeBranchGenerationTask)
                {
                    _logger.LogDebug(
                        "Skipping scheduled update, branch full generation is active. Repository: {Org}/{Repo}, Branch: {Branch}",
                        repository.OrgName, repository.RepoName, branch.BranchName);
                    continue;
                }

                string? remoteCommitId;
                try
                {
                    remoteCommitId = await repositoryAnalyzer.GetRemoteBranchHeadCommitAsync(
                        repository,
                        branch.BranchName,
                        stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to query remote HEAD. Repository: {Org}/{Repo}, Branch: {Branch}",
                        repository.OrgName,
                        repository.RepoName,
                        branch.BranchName);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(remoteCommitId))
                {
                    if (sourceInfo.SourceType == RepositorySourceType.Git)
                    {
                        _logger.LogWarning(
                            "Remote HEAD was not found. Repository: {Org}/{Repo}, Branch: {Branch}",
                            repository.OrgName,
                            repository.RepoName,
                            branch.BranchName);
                        continue;
                    }

                    CreateScheduledTask(context, repository, branch, branch.LastCommitId, null);
                    saveChanges = true;
                    continue;
                }

                if (string.Equals(remoteCommitId, branch.LastCommitId, StringComparison.Ordinal))
                {
                    _logger.LogDebug(
                        "Skipping scheduled update, remote HEAD unchanged. Repository: {Org}/{Repo}, Branch: {Branch}, Commit: {CommitId}",
                        repository.OrgName,
                        repository.RepoName,
                        branch.BranchName,
                        remoteCommitId);
                    continue;
                }

                if (ShouldNormalizeSnapshotBaseline(sourceInfo, branch.LastCommitId, remoteCommitId))
                {
                    NormalizeSnapshotBaseline(repository, branch, remoteCommitId);
                    saveChanges = true;
                    continue;
                }

                CreateScheduledTask(context, repository, branch, branch.LastCommitId, remoteCommitId);
                saveChanges = true;
            }

            repository.LastUpdateCheckAt = DateTime.UtcNow;
            repository.UpdatedAt = DateTime.UtcNow;
            saveChanges = true;

            if (saveChanges)
            {
                await context.SaveChangesAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to create scheduled update tasks. Repository: {Org}/{Repo}",
                repository.OrgName, repository.RepoName);
        }
    }

    private void NormalizeSnapshotBaseline(
        Repository repository,
        RepositoryBranch branch,
        string remoteCommitId)
    {
        var previousCommitId = branch.LastCommitId;
        branch.LastCommitId = remoteCommitId;
        branch.UpdatedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Normalized scheduled incremental baseline without creating a task. Repository: {Org}/{Repo}, Branch: {Branch}, PreviousCommit: {PreviousCommit}, CurrentCommit: {CurrentCommit}",
            repository.OrgName,
            repository.RepoName,
            branch.BranchName,
            previousCommitId,
            remoteCommitId);
    }

    private static bool ShouldNormalizeSnapshotBaseline(
        RepositorySourceInfo sourceInfo,
        string? previousCommitId,
        string remoteCommitId)
    {
        return sourceInfo.SourceType == RepositorySourceType.LocalDirectory &&
               IsGitCommitId(remoteCommitId) &&
               IsDirectorySnapshotId(previousCommitId);
    }

    private static bool IsGitCommitId(string? commitId)
    {
        return commitId is { Length: 40 } &&
               commitId.All(Uri.IsHexDigit);
    }

    private static bool IsDirectorySnapshotId(string? commitId)
    {
        return commitId is { Length: 64 } &&
               commitId.All(Uri.IsHexDigit);
    }

    private void CreateScheduledTask(
        IContext context,
        Repository repository,
        RepositoryBranch branch,
        string? previousCommitId,
        string? targetCommitId)
    {
        var task = new IncrementalUpdateTask
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repository.Id,
            BranchId = branch.Id,
            PreviousCommitId = previousCommitId,
            TargetCommitId = targetCommitId,
            Status = IncrementalUpdateStatus.Pending,
            Priority = 0,
            IsManualTrigger = false,
            CreatedAt = DateTime.UtcNow
        };

        context.IncrementalUpdateTasks.Add(task);

        _logger.LogInformation(
            "Created scheduled update task. TaskId: {TaskId}, Repository: {Org}/{Repo}, Branch: {Branch}, TargetCommit: {TargetCommit}",
            task.Id, repository.OrgName, repository.RepoName, branch.BranchName, targetCommitId ?? "unknown");
    }
}
