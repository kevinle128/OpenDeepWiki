using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// Full-generation endpoints of indexed branches. Every authenticated user can run them: branch work is shared
/// across the workspace, while deleting a repository or changing its visibility keeps its own stricter rules.
/// </summary>
public static class BranchGenerationEndpoints
{
    private const int DefaultTaskListLimit = 20;
    private const int MaxTaskListLimit = 100;

    public static IEndpointRouteBuilder MapBranchGenerationEndpoints(this IEndpointRouteBuilder app)
    {
        var repoGroup = app.MapGroup("/api/v1/repositories")
            .RequireAuthorization()
            .WithTags("Branch Generation");

        repoGroup.MapPost("/{repositoryId}/branches/{branchId}/generation-tasks/full", EnqueueFullGenerationAsync)
            .WithName("EnqueueBranchFullGeneration");
        repoGroup.MapGet("/{repositoryId}/branch-generation-tasks", ListTasksAsync)
            .WithName("ListBranchGenerationTasks");

        var taskGroup = app.MapGroup("/api/v1/branch-generation-tasks")
            .RequireAuthorization()
            .WithTags("Branch Generation Tasks");

        taskGroup.MapGet("/{taskId}", GetTaskAsync)
            .WithName("GetBranchGenerationTask");
        taskGroup.MapPost("/{taskId}/retry", RetryAsync)
            .WithName("RetryBranchGenerationTask");
        taskGroup.MapPost("/{taskId}/cancel", CancelAsync)
            .WithName("CancelBranchGenerationTask");

        return app;
    }

    private static async Task<IResult> EnqueueFullGenerationAsync(
        string repositoryId,
        string branchId,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        [FromServices] IBranchGenerationTaskService taskService,
        [FromServices] IBranchActionAuditor auditor,
        CancellationToken cancellationToken)
    {
        var authorizationResult = await AuthorizeBranchOperationAsync(context, userContext, repositoryId, cancellationToken);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        var result = await taskService.EnqueueFullGenerationAsync(
            repositoryId, branchId, userContext.UserId, cancellationToken: cancellationToken);
        if (result.Success)
        {
            await auditor.RecordAsync(
                repositoryId, branchId, userContext.UserId, GitConnectionAuditEventType.BranchRebuildRequested, cancellationToken);
        }

        return ToResult(result, StatusCodes.Status201Created);
    }

    private static async Task<IResult> RetryAsync(
        string taskId,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        [FromServices] IBranchGenerationTaskService taskService,
        [FromServices] IBranchActionAuditor auditor,
        CancellationToken cancellationToken)
    {
        var authorizationResult = await AuthorizeBranchTaskOperationAsync(context, userContext, taskId, cancellationToken);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        var result = await taskService.RetryAsync(taskId, cancellationToken);
        if (result.Success && result.Task is not null)
        {
            await auditor.RecordAsync(
                result.Task.RepositoryId, result.Task.BranchId, userContext.UserId,
                GitConnectionAuditEventType.BranchTaskRetried, cancellationToken);
        }

        return ToResult(result);
    }

    private static async Task<IResult> CancelAsync(
        string taskId,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        [FromServices] IBranchGenerationTaskService taskService,
        [FromServices] IBranchActionAuditor auditor,
        CancellationToken cancellationToken)
    {
        var authorizationResult = await AuthorizeBranchTaskOperationAsync(context, userContext, taskId, cancellationToken);
        if (authorizationResult is not null)
        {
            return authorizationResult;
        }

        var result = await taskService.CancelAsync(taskId, cancellationToken);
        if (result.Success && result.Task is not null)
        {
            await auditor.RecordAsync(
                result.Task.RepositoryId, result.Task.BranchId, userContext.UserId,
                GitConnectionAuditEventType.BranchTaskCancelled, cancellationToken);
        }

        return ToResult(result);
    }

    private static async Task<IResult> GetTaskAsync(
        string taskId,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        CancellationToken cancellationToken)
    {
        var unauthorized = await AuthorizeBranchTaskOperationAsync(context, userContext, taskId, cancellationToken);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var task = await context.BranchGenerationTasks
            .AsNoTracking()
            .Include(item => item.Repository)
            .Include(item => item.Branch)
            .FirstOrDefaultAsync(item => item.Id == taskId && !item.IsDeleted, cancellationToken);

        return task is null
            ? Results.NotFound(new BranchGenerationErrorResponse(false, "TASK_NOT_FOUND", "任务不存在"))
            : Results.Ok(BranchGenerationTaskResponse.FromTask(task));
    }

    private static async Task<IResult> ListTasksAsync(
        string repositoryId,
        [FromServices] IContext context,
        [FromServices] IUserContext userContext,
        CancellationToken cancellationToken,
        [FromQuery] string? branchId = null,
        [FromQuery] int limit = DefaultTaskListLimit)
    {
        var unauthorized = await AuthorizeBranchOperationAsync(context, userContext, repositoryId, cancellationToken);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var take = limit < 1 ? DefaultTaskListLimit : Math.Min(limit, MaxTaskListLimit);
        var tasks = await context.BranchGenerationTasks
            .AsNoTracking()
            .Include(item => item.Repository)
            .Include(item => item.Branch)
            .Where(item => item.RepositoryId == repositoryId && !item.IsDeleted)
            .Where(item => branchId == null || item.BranchId == branchId)
            .OrderByDescending(item => item.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);

        return Results.Ok(tasks.Select(BranchGenerationTaskResponse.FromTask).ToList());
    }

    /// <summary>
    /// Branch work needs a signed-in user and a repository that the user can see (public, own, or any as Admin), and
    /// nothing more. A repository that the user cannot see is reported as missing. The name says so on purpose:
    /// repository-level changes (delete, visibility) must not reuse this check.
    /// </summary>
    public static async Task<IResult?> AuthorizeBranchOperationAsync(
        IContext context,
        IUserContext userContext,
        string repositoryId,
        CancellationToken cancellationToken)
    {
        var unauthorized = RequireSignedIn(userContext);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var repositoryExists = await context.Repositories
            .AsNoTracking()
            .Where(RepositoryReadAccess.VisibleTo(userContext))
            .AnyAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken);

        return repositoryExists
            ? null
            : Results.NotFound(new BranchGenerationErrorResponse(false, "REPOSITORY_NOT_FOUND", "仓库不存在"));
    }

    public static async Task<IResult?> AuthorizeBranchTaskOperationAsync(
        IContext context,
        IUserContext userContext,
        string taskId,
        CancellationToken cancellationToken)
    {
        var unauthorized = RequireSignedIn(userContext);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        var task = await context.BranchGenerationTasks
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == taskId && !item.IsDeleted, cancellationToken);

        if (task is null)
        {
            return Results.NotFound(new BranchGenerationErrorResponse(false, "TASK_NOT_FOUND", "任务不存在"));
        }

        return await AuthorizeBranchOperationAsync(context, userContext, task.RepositoryId, cancellationToken);
    }

    private static IResult? RequireSignedIn(IUserContext userContext)
    {
        return userContext.IsAuthenticated && !string.IsNullOrWhiteSpace(userContext.UserId)
            ? null
            : Results.Json(
                new BranchGenerationErrorResponse(false, "UNAUTHORIZED", "请先登录"),
                statusCode: StatusCodes.Status401Unauthorized);
    }

    private static IResult ToResult(BranchGenerationTaskResult result, int successStatusCode = StatusCodes.Status200OK)
    {
        if (result.Success && result.Task is not null)
        {
            return Results.Json(BranchGenerationTaskResponse.FromTask(result.Task), statusCode: successStatusCode);
        }

        var response = new BranchGenerationErrorResponse(
            false,
            result.ErrorCode ?? "BRANCH_GENERATION_FAILED",
            result.ErrorMessage ?? "branch 生成任务操作失败",
            result.Task is null ? null : BranchGenerationTaskResponse.FromTask(result.Task),
            result.ActiveLock is null ? null : BranchGenerationLockResponse.FromLock(result.ActiveLock));

        return result.ErrorCode switch
        {
            "REPOSITORY_NOT_FOUND" or "BRANCH_NOT_FOUND" or "TASK_NOT_FOUND" => Results.NotFound(response),
            "BRANCH_GENERATION_ACTIVE" or "REPOSITORY_GENERATION_ACTIVE" or "GENERATION_LOCK_CONFLICT" => Results.Conflict(response),
            "INVALID_TASK_STATUS" => Results.BadRequest(response),
            _ => Results.Json(response, statusCode: StatusCodes.Status500InternalServerError)
        };
    }
}

public sealed record BranchGenerationTaskResponse(
    bool Success,
    string TaskId,
    string RepositoryId,
    string BranchId,
    string? RepositoryName,
    string? BranchName,
    string Status,
    string Mode,
    int Priority,
    bool IsManualTrigger,
    int RetryCount,
    string? ErrorMessage,
    string? RequestedBy,
    string? TargetCommitId,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt)
{
    public static BranchGenerationTaskResponse FromTask(BranchGenerationTask task)
    {
        return new BranchGenerationTaskResponse(
            true,
            task.Id,
            task.RepositoryId,
            task.BranchId,
            task.Repository is null ? null : $"{task.Repository.OrgName}/{task.Repository.RepoName}",
            task.Branch?.BranchName,
            task.Status.ToString(),
            task.Mode.ToString(),
            task.Priority,
            task.IsManualTrigger,
            task.RetryCount,
            task.ErrorMessage,
            task.RequestedBy,
            task.TargetCommitId,
            task.CreatedAt,
            task.StartedAt,
            task.CompletedAt);
    }
}

public sealed record BranchGenerationErrorResponse(
    bool Success,
    string ErrorCode,
    string Error,
    BranchGenerationTaskResponse? ActiveTask = null,
    BranchGenerationLockResponse? ActiveLock = null);

public sealed record BranchGenerationLockResponse(
    string RepositoryId,
    string OwnerType,
    string OwnerId,
    string Scope,
    DateTime AcquiredAt)
{
    public static BranchGenerationLockResponse FromLock(RepositoryGenerationLock generationLock)
    {
        return new BranchGenerationLockResponse(
            generationLock.RepositoryId,
            generationLock.OwnerType.ToString(),
            generationLock.OwnerId,
            generationLock.Scope.ToString(),
            generationLock.AcquiredAt);
    }
}
