using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models.ConnectedRepositories;
using OpenDeepWiki.Services.Auth;

namespace OpenDeepWiki.Services.Repositories;

public sealed class IndexedBranchRemovalService(
    IContext context,
    IUserContext userContext,
    IBranchActionAuditor auditor,
    IOptions<RepositoryAnalyzerOptions> analyzerOptions,
    ILogger<IndexedBranchRemovalService> logger) : IIndexedBranchRemovalService
{
    /// <summary>
    /// Names the workspace directories whose delete failed, one per line, next to the repository's branch directories.
    /// </summary>
    private const string PendingRemovalsFileName = ".pending-workspace-removals";

    private static readonly SemaphoreSlim PendingRemovalsLock = new(1, 1);

    public async Task<RemoveIndexedBranchResponse> RemoveAsync(
        string repositoryId, string branchId, CancellationToken cancellationToken)
    {
        var actor = RequireUser();
        var repository = await context.Repositories
            .AsNoTracking()
            .Where(RepositoryReadAccess.VisibleTo(userContext))
            .FirstOrDefaultAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken)
            ?? throw new IndexedBranchRemovalException(IndexedBranchRemovalErrorCodes.RepositoryNotFound);
        var branch = await context.RepositoryBranches
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == branchId && item.RepositoryId == repositoryId && !item.IsDeleted, cancellationToken)
            ?? throw new IndexedBranchRemovalException(IndexedBranchRemovalErrorCodes.BranchNotFound);

        await using (var transaction = await EfContextTransaction.BeginIfSupportedAsync(context, cancellationToken))
        {
            try
            {
                await RemoveBranchDataAsync(repository, branch, actor, cancellationToken);
                if (transaction is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }
            }
            catch
            {
                // A refused or failed removal must leave every row as it was.
                if (transaction is not null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }

                EfContextTransaction.ClearPendingChanges(context);
                throw;
            }
        }

        logger.LogInformation(
            "Indexed branch removed. RepositoryId: {RepositoryId}, BranchId: {BranchId}, ActorUserId: {ActorUserId}",
            repositoryId, branchId, actor);

        var workspaceRemoved = await RemoveWorkspaceAsync(repository, branch, cancellationToken);
        return new RemoveIndexedBranchResponse(repository.Id, branch.Id, branch.BranchName, workspaceRemoved);
    }

    public async Task<int> CleanupAllRemovedWorkspacesAsync(CancellationToken cancellationToken)
    {
        var options = analyzerOptions.Value;
        var repositories = await context.Repositories
            .AsNoTracking()
            .Select(item => new Repository { Id = item.Id, OrgName = item.OrgName, RepoName = item.RepoName })
            .ToListAsync(cancellationToken);

        var removed = 0;
        foreach (var repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(RepositoryRootOf(options, repository), PendingRemovalsFileName)))
            {
                continue;
            }

            try
            {
                removed += await CleanupRemovedWorkspacesAsync(repository.Id, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Pending workspace cleanup failed. RepositoryId: {RepositoryId}", repository.Id);
            }
        }

        return removed;
    }

    public async Task<int> CleanupRemovedWorkspacesAsync(string repositoryId, CancellationToken cancellationToken)
    {
        var repository = await context.Repositories
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == repositoryId, cancellationToken)
            ?? throw new IndexedBranchRemovalException(IndexedBranchRemovalErrorCodes.RepositoryNotFound);

        var options = analyzerOptions.Value;
        var repositoryRoot = RepositoryRootOf(options, repository);
        var markerPath = Path.Combine(repositoryRoot, PendingRemovalsFileName);

        await PendingRemovalsLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(markerPath))
            {
                return 0;
            }

            var activeDirectories = await ActiveBranchDirectoryNamesAsync(repository, options, cancellationToken);
            var removed = 0;
            var remaining = new List<string>();
            foreach (var name in (await File.ReadAllLinesAsync(markerPath, cancellationToken)).Where(line => line.Length > 0))
            {
                // A branch that was added again owns the directory now, so the old removal is void.
                if (activeDirectories.Contains(name) || !IsPlainDirectoryName(name))
                {
                    continue;
                }

                var directory = Path.Combine(repositoryRoot, "branches", name);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                if (TryDeleteDirectory(options, directory))
                {
                    removed++;
                }
                else
                {
                    remaining.Add(name);
                }
            }

            WriteMarker(markerPath, remaining);
            return removed;
        }
        finally
        {
            PendingRemovalsLock.Release();
        }
    }

    // ---- database ----

    private async Task RemoveBranchDataAsync(
        Repository repository, RepositoryBranch branch, string actor, CancellationToken cancellationToken)
    {
        var branchId = branch.Id;
        var now = DateTime.UtcNow;

        // Pending work goes first. A worker that claims a task needs it to be Pending, so after this statement
        // no new claim can start, and a claim that came earlier shows up as Processing below.
        await UpdateAsync(
            context.BranchGenerationTasks.Where(item => item.BranchId == branchId && !item.IsDeleted && item.Status == BranchGenerationTaskStatus.Pending),
            item => item.Status = BranchGenerationTaskStatus.Cancelled,
            setters => setters
                .SetProperty(item => item.Status, BranchGenerationTaskStatus.Cancelled)
                .SetProperty(item => item.CompletedAt, now),
            cancellationToken);
        await UpdateAsync(
            context.IncrementalUpdateTasks.Where(item => item.BranchId == branchId && !item.IsDeleted && item.Status == IncrementalUpdateStatus.Pending),
            item => item.Status = IncrementalUpdateStatus.Cancelled,
            setters => setters
                .SetProperty(item => item.Status, IncrementalUpdateStatus.Cancelled)
                .SetProperty(item => item.CompletedAt, now),
            cancellationToken);

        var processing = await context.BranchGenerationTasks.AnyAsync(
                             item => item.BranchId == branchId && !item.IsDeleted && item.Status == BranchGenerationTaskStatus.Processing,
                             cancellationToken)
                         || await context.IncrementalUpdateTasks.AnyAsync(
                             item => item.BranchId == branchId && !item.IsDeleted && item.Status == IncrementalUpdateStatus.Processing,
                             cancellationToken);
        if (processing)
        {
            throw new IndexedBranchRemovalException(IndexedBranchRemovalErrorCodes.BranchJobActive);
        }

        var languageIds = await context.BranchLanguages
            .Where(item => item.RepositoryBranchId == branchId)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        // Dependents before the rows they point at. Nothing here touches repository-wide records.
        // A catalog points at its parent with a restricting foreign key that both databases check row by row,
        // so one delete of the whole tree fails. Cutting the parent links first lets the tree go in one statement.
        await UpdateAsync(
            context.DocCatalogs.Where(item => languageIds.Contains(item.BranchLanguageId) && item.ParentId != null),
            item => item.ParentId = null,
            setters => setters.SetProperty(item => item.ParentId, (string?)null),
            cancellationToken);
        await DeleteAsync(context.DocCatalogs.Where(item => languageIds.Contains(item.BranchLanguageId)), cancellationToken);
        await DeleteAsync(context.DocFiles.Where(item => languageIds.Contains(item.BranchLanguageId)), cancellationToken);
        await DeleteAsync(
            context.TranslationTasks.Where(item => item.RepositoryBranchId == branchId || languageIds.Contains(item.SourceBranchLanguageId)),
            cancellationToken);
        await DeleteAsync(context.RepositoryProcessingLogs.Where(item => item.BranchId == branchId), cancellationToken);
        await DeleteAsync(context.IncrementalUpdateTasks.Where(item => item.BranchId == branchId), cancellationToken);
        await DeleteAsync(context.BranchGenerationTasks.Where(item => item.BranchId == branchId), cancellationToken);
        await DeleteAsync(context.GraphifyArtifacts.Where(item => item.RepositoryBranchId == branchId), cancellationToken);
        await DeleteAsync(context.RepositoryGenerationLocks.Where(item => item.BranchId == branchId), cancellationToken);
        await DeleteAsync(context.BranchLanguages.Where(item => item.RepositoryBranchId == branchId), cancellationToken);
        await DeleteAsync(context.RepositoryBranches.Where(item => item.Id == branchId), cancellationToken);

        auditor.Stage(repository, actor, GitConnectionAuditEventType.BranchRemoved);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Bulk update on relational databases. The InMemory provider has no bulk statements, so it updates tracked rows.
    /// </summary>
    private async Task UpdateAsync<T>(
        IQueryable<T> query,
        Action<T> apply,
        Action<UpdateSettersBuilder<T>> setters,
        CancellationToken cancellationToken) where T : class
    {
        if (EfContextCapabilities.SupportsExecuteUpdate(context))
        {
            await query.ExecuteUpdateAsync(setters, cancellationToken);
            return;
        }

        foreach (var item in await query.ToListAsync(cancellationToken))
        {
            apply(item);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task DeleteAsync<T>(IQueryable<T> query, CancellationToken cancellationToken) where T : class
    {
        if (EfContextCapabilities.SupportsExecuteUpdate(context))
        {
            await query.ExecuteDeleteAsync(cancellationToken);
            return;
        }

        ((DbContext)context).RemoveRange(await query.ToListAsync(cancellationToken));
        await context.SaveChangesAsync(cancellationToken);
    }

    // ---- workspace ----

    private async Task<bool> RemoveWorkspaceAsync(Repository repository, RepositoryBranch branch, CancellationToken cancellationToken)
    {
        try
        {
            var options = analyzerOptions.Value;
            var directory = BranchDirectoryOf(options, repository, branch.BranchName);
            var name = Path.GetFileName(directory);

            // Branch names such as "feature/x" and "feature_x" share one directory. It stays while any of them is indexed.
            var active = await ActiveBranchDirectoryNamesAsync(repository, options, cancellationToken);
            if (active.Contains(name))
            {
                logger.LogInformation(
                    "Workspace kept because another branch uses the same directory. RepositoryId: {RepositoryId}", repository.Id);
                return false;
            }

            if (!Directory.Exists(directory))
            {
                return true;
            }

            var repositoryRoot = RepositoryRootOf(options, repository);
            var markerPath = Path.Combine(repositoryRoot, PendingRemovalsFileName);
            await RecordPendingRemovalAsync(markerPath, name, cancellationToken);

            if (!TryDeleteDirectory(options, directory))
            {
                return false;
            }

            await ClearPendingRemovalAsync(markerPath, name, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "Workspace of a removed branch could not be cleaned. RepositoryId: {RepositoryId}", repository.Id);
            return false;
        }
    }

    private bool TryDeleteDirectory(RepositoryAnalyzerOptions options, string directory)
    {
        var root = options.RepositoriesDirectory;
        try
        {
            if (!IsStrictlyInside(root, directory))
            {
                logger.LogWarning("Workspace path is outside the repository root and was not deleted.");
                return false;
            }

            // A link is not followed: removing what it points at would reach outside the workspace.
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                logger.LogWarning("Workspace path is a link and was not deleted.");
                return false;
            }

            ResetReadOnlyAttributes(directory);
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Workspace delete failed and stays pending for cleanup.");
            return false;
        }
    }

    private static void ResetReadOnlyAttributes(string directory)
    {
        // Git writes object files as read-only, and a read-only file blocks the delete on some platforms.
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is below <paramref name="root"/> after both are resolved to full paths.
    /// The root itself and a sibling that only shares a name prefix are not inside.
    /// </summary>
    internal static bool IsStrictlyInside(string root, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var fullRoot = Path.GetFullPath(root).TrimEnd(separators);
        var fullPath = Path.GetFullPath(path).TrimEnd(separators);
        return fullRoot.Length > 0 && fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static string BranchDirectoryOf(RepositoryAnalyzerOptions options, Repository repository, string branchName)
    {
        var tree = RepositoryWorkspacePath.ForBranch(options, repository.OrgName, repository.RepoName, branchName);
        return Directory.GetParent(tree)!.FullName;
    }

    private static string RepositoryRootOf(RepositoryAnalyzerOptions options, Repository repository)
        => Directory.GetParent(Directory.GetParent(BranchDirectoryOf(options, repository, "x"))!.FullName)!.FullName;

    private async Task<HashSet<string>> ActiveBranchDirectoryNamesAsync(
        Repository repository, RepositoryAnalyzerOptions options, CancellationToken cancellationToken)
    {
        var names = await context.RepositoryBranches
            .AsNoTracking()
            .Where(item => item.RepositoryId == repository.Id && !item.IsDeleted)
            .Select(item => item.BranchName)
            .ToListAsync(cancellationToken);
        return names
            .Select(name => Path.GetFileName(BranchDirectoryOf(options, repository, name)))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private static bool IsPlainDirectoryName(string name)
        => name.Length > 0 && name is not "." and not ".."
           && name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;

    private static async Task RecordPendingRemovalAsync(string markerPath, string name, CancellationToken cancellationToken)
    {
        await PendingRemovalsLock.WaitAsync(cancellationToken);
        try
        {
            var lines = File.Exists(markerPath) ? (await File.ReadAllLinesAsync(markerPath, cancellationToken)).ToList() : [];
            if (!lines.Contains(name))
            {
                lines.Add(name);
                WriteMarker(markerPath, lines);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the marker a failed delete cannot be retried by name; the delete itself still runs.
        }
        finally
        {
            PendingRemovalsLock.Release();
        }
    }

    private static async Task ClearPendingRemovalAsync(string markerPath, string name, CancellationToken cancellationToken)
    {
        await PendingRemovalsLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(markerPath))
            {
                var lines = (await File.ReadAllLinesAsync(markerPath, cancellationToken)).Where(line => line != name).ToList();
                WriteMarker(markerPath, lines);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale line is harmless: cleanup skips directories that no longer exist.
        }
        finally
        {
            PendingRemovalsLock.Release();
        }
    }

    private static void WriteMarker(string markerPath, List<string> lines)
    {
        if (lines.Count == 0)
        {
            File.Delete(markerPath);
            return;
        }

        File.WriteAllLines(markerPath, lines);
    }

    private string RequireUser()
    {
        var userId = userContext.UserId;
        return userContext.IsAuthenticated && !string.IsNullOrWhiteSpace(userId)
            ? userId
            : throw new IndexedBranchRemovalException(IndexedBranchRemovalErrorCodes.Unauthorized);
    }
}
