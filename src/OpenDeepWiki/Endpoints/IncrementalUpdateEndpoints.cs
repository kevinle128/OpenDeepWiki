using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// 增量更新端点日志类（用于泛型日志记录器）
/// </summary>
public class IncrementalUpdateEndpointsLogger { }

/// <summary>
/// 增量更新 API 端点
/// 提供手动触发增量更新、查询任务状态和重试失败任务的功能
/// Every route needs a signed-in user. Any signed-in user may update, read, and retry the incremental
/// tasks of any repository branch: branch work is shared across the workspace.
/// </summary>
public static class IncrementalUpdateEndpoints
{
    /// <summary>
    /// 注册所有增量更新相关端点
    /// </summary>
    public static IEndpointRouteBuilder MapIncrementalUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        // 仓库增量更新触发端点
        var repoGroup = app.MapGroup("/api/v1/repositories")
            .RequireAuthorization()
            .WithTags("增量更新");

        repoGroup.MapPost("/{repositoryId}/branches/{branchId}/incremental-update", TriggerIncrementalUpdateAsync)
            .WithName("TriggerIncrementalUpdate")
            .WithSummary("手动触发增量更新")
            .WithDescription("为指定仓库和分支创建一个高优先级的增量更新任务");

        repoGroup.MapGet("/{repositoryId}/incremental-updates", ListTasksAsync)
            .WithName("ListIncrementalUpdateTasks")
            .WithSummary("列出仓库的增量更新任务");

        // 增量更新任务管理端点
        var taskGroup = app.MapGroup("/api/v1/incremental-updates")
            .RequireAuthorization()
            .WithTags("增量更新任务");

        taskGroup.MapGet("/{taskId}", GetTaskStatusAsync)
            .WithName("GetIncrementalUpdateTaskStatus")
            .WithSummary("获取任务状态")
            .WithDescription("获取指定增量更新任务的详细状态");

        taskGroup.MapPost("/{taskId}/retry", RetryFailedTaskAsync)
            .WithName("RetryFailedIncrementalUpdateTask")
            .WithSummary("重试失败任务")
            .WithDescription("重试一个失败的增量更新任务");

        return app;
    }

    /// <summary>
    /// 手动触发增量更新
    /// POST /api/v1/repositories/{repositoryId}/branches/{branchId}/incremental-update
    /// </summary>
    private static async Task<IResult> TriggerIncrementalUpdateAsync(
        string repositoryId,
        string branchId,
        [FromServices] IIncrementalUpdateService updateService,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        [FromServices] IBranchActionAuditor auditor,
        [FromServices] ILogger<IncrementalUpdateEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        var unauthorized = await AuthorizeRepositoryAsync(context, userContext, repositoryId, cancellationToken);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        logger.LogInformation(
            "Manual incremental update requested. RepositoryId: {RepositoryId}, BranchId: {BranchId}, UserId: {UserId}",
            repositoryId, branchId, userContext.UserId);

        try
        {
            // The service checks that the branch exists and belongs to the repository.
            var taskId = await updateService.TriggerManualUpdateAsync(
                repositoryId, branchId, cancellationToken, userContext.UserId);

            var task = await context.IncrementalUpdateTasks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            logger.LogInformation(
                "Incremental update task created/found. TaskId: {TaskId}, Status: {Status}",
                taskId, task?.Status);

            await auditor.RecordAsync(
                repositoryId, branchId, userContext.UserId, GitConnectionAuditEventType.BranchSyncRequested, cancellationToken);

            return Results.Ok(new TriggerIncrementalUpdateResponse
            {
                Success = true,
                TaskId = taskId,
                Status = task?.Status.ToString() ?? "Unknown",
                Message = task?.Status == IncrementalUpdateStatus.Processing
                    ? "任务正在处理中"
                    : "增量更新任务已创建"
            });
        }
        catch (IncrementalUpdateRejectedException ex)
        {
            logger.LogWarning(
                "Incremental update rejected. RepositoryId: {RepositoryId}, BranchId: {BranchId}, ErrorCode: {ErrorCode}",
                repositoryId, branchId, ex.ErrorCode);

            return ToRejectedResult(ex.ErrorCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to trigger incremental update. RepositoryId: {RepositoryId}, BranchId: {BranchId}",
                repositoryId, branchId);

            return Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "触发增量更新失败",
                    ErrorCode = "TRIGGER_FAILED"
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult ToRejectedResult(string errorCode)
    {
        var (status, message) = errorCode switch
        {
            IncrementalUpdateErrorCodes.RepositoryNotFound => (StatusCodes.Status404NotFound, "仓库不存在"),
            IncrementalUpdateErrorCodes.BranchNotFound => (StatusCodes.Status404NotFound, "分支不存在"),
            IncrementalUpdateErrorCodes.BranchGenerationActive => (StatusCodes.Status409Conflict, "该分支已有 full generation 任务正在排队或处理中"),
            _ => (StatusCodes.Status400BadRequest, "增量更新请求被拒绝")
        };

        return Results.Json(
            new IncrementalUpdateErrorResponse { Success = false, Error = message, ErrorCode = errorCode },
            statusCode: status);
    }

    /// <summary>
    /// A signed-in user who can see the repository (public, own, or any as Admin). A repository that the user cannot
    /// see is reported as missing.
    /// </summary>
    private static async Task<IResult?> AuthorizeRepositoryAsync(
        IContext context, IUserContext userContext, string repositoryId, CancellationToken cancellationToken)
    {
        var unauthorized = RequireSignedIn(userContext);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var visible = await context.Repositories
            .AsNoTracking()
            .Where(RepositoryReadAccess.VisibleTo(userContext))
            .AnyAsync(r => r.Id == repositoryId && !r.IsDeleted, cancellationToken);
        return visible ? null : ToRejectedResult(IncrementalUpdateErrorCodes.RepositoryNotFound);
    }

    /// <summary>
    /// A signed-in user who can see the repository of the task. A task of a hidden repository is reported as missing.
    /// </summary>
    private static async Task<IResult?> AuthorizeTaskAsync(
        IContext context, IUserContext userContext, string taskId, CancellationToken cancellationToken)
    {
        var unauthorized = RequireSignedIn(userContext);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var repositoryId = await context.IncrementalUpdateTasks
            .AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.RepositoryId)
            .FirstOrDefaultAsync(cancellationToken);
        var hidden = repositoryId is null
                     || await AuthorizeRepositoryAsync(context, userContext, repositoryId, cancellationToken) is not null;
        return hidden
            ? Results.NotFound(new IncrementalUpdateErrorResponse { Success = false, Error = "任务不存在", ErrorCode = "TASK_NOT_FOUND" })
            : null;
    }

    private static IResult? RequireSignedIn(IUserContext userContext)
    {
        return userContext.IsAuthenticated && !string.IsNullOrWhiteSpace(userContext.UserId)
            ? null
            : Results.Json(
                new IncrementalUpdateErrorResponse { Success = false, Error = "请先登录", ErrorCode = "UNAUTHORIZED" },
                statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// 列出仓库的增量更新任务（最新的在前）
    /// GET /api/v1/repositories/{repositoryId}/incremental-updates
    /// </summary>
    private static async Task<IResult> ListTasksAsync(
        string repositoryId,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        CancellationToken cancellationToken,
        [FromQuery] string? branchId = null,
        [FromQuery] int limit = 20)
    {
        var unauthorized = await AuthorizeRepositoryAsync(context, userContext, repositoryId, cancellationToken);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var take = limit < 1 ? 20 : Math.Min(limit, 100);
        var tasks = await context.IncrementalUpdateTasks
            .AsNoTracking()
            .Include(t => t.Repository)
            .Include(t => t.Branch)
            .Where(t => t.RepositoryId == repositoryId && !t.IsDeleted)
            .Where(t => branchId == null || t.BranchId == branchId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Results.Ok(tasks.Select(ToTaskResponse).ToList());
    }

    private static IncrementalUpdateTaskResponse ToTaskResponse(IncrementalUpdateTask task) => new()
    {
        Success = true,
        TaskId = task.Id,
        RepositoryId = task.RepositoryId,
        RepositoryName = task.Repository != null
            ? $"{task.Repository.OrgName}/{task.Repository.RepoName}"
            : null,
        BranchId = task.BranchId,
        BranchName = task.Branch?.BranchName,
        Status = task.Status.ToString(),
        Priority = task.Priority,
        IsManualTrigger = task.IsManualTrigger,
        PreviousCommitId = task.PreviousCommitId,
        TargetCommitId = task.TargetCommitId,
        RetryCount = task.RetryCount,
        ErrorMessage = task.ErrorMessage,
        CreatedAt = task.CreatedAt,
        StartedAt = task.StartedAt,
        CompletedAt = task.CompletedAt
    };


    /// <summary>
    /// 获取任务状态
    /// GET /api/v1/incremental-updates/{taskId}
    /// </summary>
    private static async Task<IResult> GetTaskStatusAsync(
        string taskId,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        [FromServices] ILogger<IncrementalUpdateEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        var unauthorized = await AuthorizeTaskAsync(context, userContext, taskId, cancellationToken);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        logger.LogDebug("Getting task status. TaskId: {TaskId}", taskId);

        try
        {
            var task = await context.IncrementalUpdateTasks
                .Include(t => t.Repository)
                .Include(t => t.Branch)
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                logger.LogWarning("Task not found. TaskId: {TaskId}", taskId);
                return Results.NotFound(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "任务不存在",
                    ErrorCode = "TASK_NOT_FOUND"
                });
            }

            return Results.Ok(ToTaskResponse(task));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get task status. TaskId: {TaskId}", taskId);

            return Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "获取任务状态失败",
                    ErrorCode = "GET_STATUS_FAILED"
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// 重试失败任务
    /// POST /api/v1/incremental-updates/{taskId}/retry
    /// </summary>
    private static async Task<IResult> RetryFailedTaskAsync(
        string taskId,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        [FromServices] IBranchActionAuditor auditor,
        [FromServices] ILogger<IncrementalUpdateEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        var unauthorized = await AuthorizeTaskAsync(context, userContext, taskId, cancellationToken);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        logger.LogInformation("Retry requested for task. TaskId: {TaskId}, UserId: {UserId}", taskId, userContext.UserId);

        try
        {
            var task = await context.IncrementalUpdateTasks
                .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

            if (task == null)
            {
                logger.LogWarning("Task not found. TaskId: {TaskId}", taskId);
                return Results.NotFound(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "任务不存在",
                    ErrorCode = "TASK_NOT_FOUND"
                });
            }

            // 只能重试失败的任务
            if (task.Status != IncrementalUpdateStatus.Failed)
            {
                logger.LogWarning(
                    "Cannot retry task with status {Status}. TaskId: {TaskId}",
                    task.Status, taskId);

                return Results.BadRequest(new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = $"只能重试失败的任务，当前状态: {task.Status}",
                    ErrorCode = "INVALID_TASK_STATUS"
                });
            }

            // 重置任务状态
            task.Status = IncrementalUpdateStatus.Pending;
            task.RetryCount++;
            task.ErrorMessage = null;
            task.StartedAt = null;
            task.CompletedAt = null;
            task.UpdatedAt = DateTime.UtcNow;
            task.RequestedBy = userContext.UserId;

            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Task reset for retry. TaskId: {TaskId}, RetryCount: {RetryCount}",
                taskId, task.RetryCount);

            await auditor.RecordAsync(
                task.RepositoryId, task.BranchId, userContext.UserId,
                GitConnectionAuditEventType.BranchTaskRetried, cancellationToken);

            return Results.Ok(new RetryTaskResponse
            {
                Success = true,
                TaskId = task.Id,
                Status = task.Status.ToString(),
                RetryCount = task.RetryCount,
                Message = "任务已重置，将在下次轮询时重新处理"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retry task. TaskId: {TaskId}", taskId);

            return Results.Json(
                new IncrementalUpdateErrorResponse
                {
                    Success = false,
                    Error = "重试任务失败",
                    ErrorCode = "RETRY_FAILED"
                },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}


#region 响应模型

/// <summary>
/// 触发增量更新响应
/// </summary>
public class TriggerIncrementalUpdateResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 任务ID
    /// </summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// 任务状态
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 消息
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 增量更新任务详情响应
/// </summary>
public class IncrementalUpdateTaskResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 任务ID
    /// </summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// 仓库ID
    /// </summary>
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>
    /// 仓库名称 (org/repo)
    /// </summary>
    public string? RepositoryName { get; set; }

    /// <summary>
    /// 分支ID
    /// </summary>
    public string BranchId { get; set; } = string.Empty;

    /// <summary>
    /// 分支名称
    /// </summary>
    public string? BranchName { get; set; }

    /// <summary>
    /// 任务状态
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 任务优先级
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// 是否为手动触发
    /// </summary>
    public bool IsManualTrigger { get; set; }

    /// <summary>
    /// 上次处理的 Commit ID
    /// </summary>
    public string? PreviousCommitId { get; set; }

    /// <summary>
    /// 目标 Commit ID
    /// </summary>
    public string? TargetCommitId { get; set; }

    /// <summary>
    /// 重试次数
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 开始处理时间
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// 完成时间
    /// </summary>
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// 重试任务响应
/// </summary>
public class RetryTaskResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 任务ID
    /// </summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// 任务状态
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 重试次数
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// 消息
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 增量更新错误响应
/// </summary>
public class IncrementalUpdateErrorResponse
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// 错误代码
    /// </summary>
    public string ErrorCode { get; set; } = string.Empty;

    /// <summary>
    /// 详细信息
    /// </summary>
    public string? Details { get; set; }
}

#endregion
