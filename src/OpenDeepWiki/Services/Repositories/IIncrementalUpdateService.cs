namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// 增量更新服务接口
/// 封装增量更新的核心业务逻辑
/// </summary>
public interface IIncrementalUpdateService
{
    /// <summary>
    /// 处理单个仓库的增量更新
    /// </summary>
    /// <param name="repositoryId">仓库ID</param>
    /// <param name="branchId">分支ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>更新结果</returns>
    Task<IncrementalUpdateResult> ProcessIncrementalUpdateAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 检查仓库是否需要增量更新
    /// </summary>
    /// <param name="repositoryId">仓库ID</param>
    /// <param name="branchId">分支ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>是否需要更新及变更信息</returns>
    Task<UpdateCheckResult> CheckForUpdatesAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 手动触发增量更新
    /// </summary>
    /// <param name="repositoryId">仓库ID</param>
    /// <param name="branchId">分支ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="requestedBy">Authenticated user who asked for the update.</param>
    /// <returns>创建的任务ID</returns>
    /// <exception cref="IncrementalUpdateRejectedException">
    /// The repository or branch does not exist, the branch does not belong to the repository,
    /// or a full generation of the branch is active.
    /// </exception>
    Task<string> TriggerManualUpdateAsync(
        string repositoryId,
        string branchId,
        CancellationToken cancellationToken = default,
        string? requestedBy = null);
}

/// <summary>
/// Stable codes of rejected incremental update requests.
/// </summary>
public static class IncrementalUpdateErrorCodes
{
    public const string RepositoryNotFound = "REPOSITORY_NOT_FOUND";
    public const string BranchNotFound = "BRANCH_NOT_FOUND";
    public const string BranchGenerationActive = "BRANCH_GENERATION_ACTIVE";
}

/// <summary>
/// An incremental update request was refused before any task was created.
/// </summary>
public sealed class IncrementalUpdateRejectedException : Exception
{
    public IncrementalUpdateRejectedException(string errorCode)
        : base($"Incremental update request rejected: {errorCode}.")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// 增量更新结果
/// </summary>
public class IncrementalUpdateResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 上次处理的 Commit ID
    /// </summary>
    public string? PreviousCommitId { get; set; }

    /// <summary>
    /// 当前 Commit ID
    /// </summary>
    public string? CurrentCommitId { get; set; }

    /// <summary>
    /// 变更文件数量
    /// </summary>
    public int ChangedFilesCount { get; set; }

    /// <summary>
    /// 更新的文档数量
    /// </summary>
    public int UpdatedDocumentsCount { get; set; }

    /// <summary>
    /// 处理耗时
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// True when the diff removed or renamed source files. The wiki updater only receives paths that still exist,
    /// so it cannot drop stale documents. The branch baseline stays unchanged and the branch needs a full generation.
    /// </summary>
    public bool RequiresFullGeneration { get; set; }
}

/// <summary>
/// 更新检查结果
/// </summary>
public class UpdateCheckResult
{
    /// <summary>
    /// 是否需要更新
    /// </summary>
    public bool NeedsUpdate { get; set; }

    /// <summary>
    /// 上次处理的 Commit ID
    /// </summary>
    public string? PreviousCommitId { get; set; }

    /// <summary>
    /// 当前 Commit ID
    /// </summary>
    public string? CurrentCommitId { get; set; }

    /// <summary>
    /// 变更文件列表
    /// </summary>
    public string[]? ChangedFiles { get; set; }
}
