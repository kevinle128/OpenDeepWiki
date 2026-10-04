using OpenDeepWiki.Models.ConnectedRepositories;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Stable codes of branch removal failures. They are safe to return to clients.
/// </summary>
public static class IndexedBranchRemovalErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string RepositoryNotFound = "REPOSITORY_NOT_FOUND";
    public const string BranchNotFound = "BRANCH_NOT_FOUND";

    /// <summary>
    /// A full or incremental job of the branch is running, so its data cannot be removed.
    /// </summary>
    public const string BranchJobActive = "BRANCH_JOB_ACTIVE";
}

public sealed class IndexedBranchRemovalException : Exception
{
    public IndexedBranchRemovalException(string errorCode)
        : base($"Indexed branch removal failed: {errorCode}.")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// Removes one indexed branch. Every authenticated user can do it. Only data of that branch goes away: its documents,
/// languages, tasks, logs, artifacts, locks, and local workspace. The repository, its connection, and the other
/// branches stay, even when the removed branch was the last one.
/// </summary>
public interface IIndexedBranchRemovalService
{
    /// <summary>
    /// Cancels pending work of the branch, then removes the branch and its data in one transaction.
    /// The workspace directory is removed after the commit, so a failed delete never undoes the removal.
    /// </summary>
    /// <exception cref="IndexedBranchRemovalException">
    /// The repository or branch does not exist, or a job of the branch is processing. In the last case nothing changes.
    /// </exception>
    Task<RemoveIndexedBranchResponse> RemoveAsync(string repositoryId, string branchId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes workspace directories of earlier removals that could not be deleted at that time.
    /// </summary>
    /// <remarks>
    /// This is system maintenance. It needs no signed-in user and only removes directories that an earlier removal named.
    /// </remarks>
    /// <returns>Number of directories that were removed.</returns>
    Task<int> CleanupRemovedWorkspacesAsync(string repositoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Retries pending workspace deletes of every repository that has some. A failure in one repository
    /// does not stop the others.
    /// </summary>
    /// <returns>Number of directories that were removed.</returns>
    Task<int> CleanupAllRemovedWorkspacesAsync(CancellationToken cancellationToken);
}
