using OpenDeepWiki.Models.ConnectedRepositories;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Stable codes of connected repository failures that are not provider calls. They are safe to return to clients.
/// </summary>
public static class ConnectedRepositoryErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string NoBranchesSelected = "NO_BRANCHES_SELECTED";
    public const string TooManyBranches = "TOO_MANY_BRANCHES";
    public const string InvalidBranchName = "INVALID_BRANCH_NAME";
    public const string ConnectionNotFound = "CONNECTION_NOT_FOUND";
    public const string ConnectionDisabled = "CONNECTION_DISABLED";
    public const string ConnectionSecretUnreadable = "CONNECTION_SECRET_UNREADABLE";
    public const string RepositoryNotFound = "REPOSITORY_NOT_FOUND";
    public const string RepositoryNotConnected = "REPOSITORY_NOT_CONNECTED";
    public const string RemoteBranchNotFound = "REMOTE_BRANCH_NOT_FOUND";
    public const string RemoteBranchLookupLimit = "REMOTE_BRANCH_LOOKUP_LIMIT";
    public const string RemoteInvalid = "REMOTE_INVALID";
    public const string GenerationLockConflict = "GENERATION_LOCK_CONFLICT";
    public const string RepositoryGenerationActive = "REPOSITORY_GENERATION_ACTIVE";
    public const string Conflict = "CONNECTED_REPOSITORY_CONFLICT";
    public const string AlreadyConnected = "REPOSITORY_ALREADY_CONNECTED";
}

/// <summary>
/// A connect or add-branches request failed before any change was committed.
/// </summary>
public sealed class ConnectedRepositoryException : Exception
{
    public ConnectedRepositoryException(string errorCode, IReadOnlyList<string>? branches = null)
        : base($"Connected repository request failed: {errorCode}.")
    {
        ErrorCode = errorCode;
        Branches = branches ?? [];
    }

    public string ErrorCode { get; }

    /// <summary>
    /// Branch names that the caller sent and that caused the failure. The caller supplied them, so they are safe to echo.
    /// </summary>
    public IReadOnlyList<string> Branches { get; }
}

/// <summary>
/// Registers remote repositories and their indexed branches. Every authenticated user can use any enabled connection
/// and add branches to any connected repository. One remote has one repository, and a request is atomic:
/// either every selected branch is stored with its full generation task, or nothing is.
/// </summary>
public interface IConnectedRepositoryService
{
    /// <summary>
    /// Gets or creates the repository of a remote and adds the selected branches.
    /// A branch that is already indexed is reported without a new task.
    /// </summary>
    Task<ConnectedRepositoryResponse> ConnectAsync(ConnectRepositoryRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Adds branches to a connected repository with the connection that the repository uses.
    /// </summary>
    Task<ConnectedRepositoryResponse> AddIndexedBranchesAsync(
        string repositoryId, AddIndexedBranchesRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexedBranchSummary>> ListIndexedBranchesAsync(string repositoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Links a repository that was registered by URL to a connection, without branches, tasks, or locks.
    /// This is the one place that assigns a legacy repository to a connection: the legacy credential backfill calls it.
    /// The repository must live on the origin of the connection and must not have another connection.
    /// It keeps the legacy credential fields and the remote identity empty: a later connect of the same remote
    /// adopts the repository and sets the identity. A repeat for the same connection changes nothing.
    /// </summary>
    Task AdoptLegacyRepositoryAsync(string repositoryId, string connectionId, CancellationToken cancellationToken);
}
