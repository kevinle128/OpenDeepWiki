namespace OpenDeepWiki.Models.ConnectedRepositories;

/// <summary>
/// Registers one remote repository of a Git connection and queues a full generation for every selected branch.
/// The remote is addressed by its stable provider ID, never by name or URL.
/// <paramref name="GenerateSkill"/> applies when the repository is created. A repository that already exists keeps its setting.
/// </summary>
public sealed record ConnectRepositoryRequest(
    string? ConnectionId,
    string? ProviderRepositoryId,
    IReadOnlyList<string>? Branches,
    string? LanguageCode,
    bool GenerateSkill = true);

/// <summary>
/// Adds branches to a repository that is already registered.
/// </summary>
public sealed record AddIndexedBranchesRequest(
    IReadOnlyList<string>? Branches,
    string? LanguageCode);

/// <summary>
/// Result for one requested branch. <see cref="Created"/> is false when the branch was already indexed;
/// then no new task exists and <see cref="TaskId"/> is the task that is still active, if any.
/// </summary>
public sealed record IndexedBranchResult(
    string BranchId,
    string BranchName,
    bool Created,
    string? TaskId,
    string? TaskStatus);

public sealed record ConnectedRepositoryResponse(
    string RepositoryId,
    string OrgName,
    string RepoName,
    string GitConnectionId,
    bool RepositoryCreated,
    IReadOnlyList<IndexedBranchResult> Branches);

/// <summary>
/// One indexed branch with its state, for the branch management list.
/// </summary>
public sealed record IndexedBranchSummary(
    string BranchId,
    string BranchName,
    string? LastCommitId,
    string? GenerationStatus,
    string? LastGenerationTaskId,
    string? LastGenerationError,
    DateTime? LastProcessedAt,
    IReadOnlyList<string> Languages,
    string? ActiveTaskId,
    string? ActiveTaskKind);

public sealed record RemoveIndexedBranchResponse(
    string RepositoryId,
    string BranchId,
    string BranchName,
    bool WorkspaceRemoved);
