using Microsoft.AspNetCore.Mvc;
using OpenDeepWiki.Models.ConnectedRepositories;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// Authenticated API for connected repositories and their indexed branches. Every authenticated user can connect a
/// remote through an enabled connection, add and remove branches, and read the branch list. Deleting a repository and
/// changing its visibility are other routes with their own rules.
/// Success: <c>{ success: true, data }</c>. Failure: <c>{ success: false, errorCode, message }</c> with fixed text per code.
/// </summary>
public static class ConnectedRepositoryEndpoints
{
    public static IEndpointRouteBuilder MapConnectedRepositoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGroup("/api/v1/connected-repositories")
            .RequireAuthorization()
            .WithTags("Connected Repositories")
            .MapPost("", ConnectAsync)
            .WithName("ConnectRepository");

        var branches = app.MapGroup("/api/v1/repositories/{repositoryId}/indexed-branches")
            .RequireAuthorization()
            .WithTags("Indexed Branches");

        branches.MapGet("", ListBranchesAsync).WithName("ListIndexedBranches");
        branches.MapPost("", AddBranchesAsync).WithName("AddIndexedBranches");
        branches.MapDelete("/{branchId}", RemoveBranchAsync).WithName("RemoveIndexedBranch");

        return app;
    }

    private static Task<IResult> ConnectAsync(
        HttpContext http,
        [FromBody] ConnectRepositoryRequest request,
        [FromServices] IConnectedRepositoryService service,
        CancellationToken ct)
        => RunAsync(http, async () =>
        {
            var result = await service.ConnectAsync(request, ct);
            return Json(result, result.RepositoryCreated || result.Branches.Any(item => item.Created));
        });

    private static Task<IResult> AddBranchesAsync(
        HttpContext http,
        string repositoryId,
        [FromBody] AddIndexedBranchesRequest request,
        [FromServices] IConnectedRepositoryService service,
        CancellationToken ct)
        => RunAsync(http, async () =>
        {
            var result = await service.AddIndexedBranchesAsync(repositoryId, request, ct);
            return Json(result, result.Branches.Any(item => item.Created));
        });

    private static Task<IResult> ListBranchesAsync(
        HttpContext http,
        string repositoryId,
        [FromServices] IConnectedRepositoryService service,
        CancellationToken ct)
        => RunAsync(http, async () =>
            Results.Json(new { success = true, data = await service.ListIndexedBranchesAsync(repositoryId, ct) }));

    private static Task<IResult> RemoveBranchAsync(
        HttpContext http,
        string repositoryId,
        string branchId,
        [FromServices] IIndexedBranchRemovalService service,
        CancellationToken ct)
        => RunAsync(http, async () =>
            Results.Json(new { success = true, data = await service.RemoveAsync(repositoryId, branchId, ct) }));

    /// <summary>
    /// 201 when something was created, 200 when the request only found what already existed.
    /// </summary>
    private static IResult Json<T>(T data, bool created)
        => Results.Json(new { success = true, data }, statusCode: created ? StatusCodes.Status201Created : StatusCodes.Status200OK);

    private static async Task<IResult> RunAsync(HttpContext http, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ConnectedRepositoryException ex)
        {
            return ToErrorResult(ex.ErrorCode, ex.Branches);
        }
        catch (IndexedBranchRemovalException ex)
        {
            return ToErrorResult(ex.ErrorCode, []);
        }
        catch (GitProviderException ex)
        {
            return GitConnectionEndpoints.ToErrorResult(ex, http);
        }
    }

    private static IResult ToErrorResult(string code, IReadOnlyList<string> branches)
    {
        var (status, message) = Describe(code);
        return Results.Json(
            new { success = false, errorCode = code, message, branches = branches.Count == 0 ? null : branches },
            statusCode: status);
    }

    internal static (int Status, string Message) Describe(string code) => code switch
    {
        ConnectedRepositoryErrorCodes.Unauthorized => (StatusCodes.Status401Unauthorized, "Sign in to continue."),
        ConnectedRepositoryErrorCodes.InvalidRequest => (StatusCodes.Status400BadRequest, "The request is not valid."),
        ConnectedRepositoryErrorCodes.NoBranchesSelected => (StatusCodes.Status400BadRequest, "Select at least one branch."),
        ConnectedRepositoryErrorCodes.TooManyBranches => (StatusCodes.Status400BadRequest, "Too many branches are selected."),
        ConnectedRepositoryErrorCodes.InvalidBranchName => (StatusCodes.Status400BadRequest, "A branch name is not valid."),
        ConnectedRepositoryErrorCodes.ConnectionNotFound => (StatusCodes.Status404NotFound, "The connection does not exist."),
        ConnectedRepositoryErrorCodes.ConnectionDisabled => (StatusCodes.Status409Conflict, "The connection is disabled."),
        ConnectedRepositoryErrorCodes.ConnectionSecretUnreadable => (StatusCodes.Status409Conflict, "The stored credential cannot be read. Replace the token."),
        ConnectedRepositoryErrorCodes.RepositoryNotFound => (StatusCodes.Status404NotFound, "The repository does not exist."),
        ConnectedRepositoryErrorCodes.RepositoryNotConnected => (StatusCodes.Status409Conflict, "The repository has no Git connection."),
        ConnectedRepositoryErrorCodes.RemoteBranchNotFound => (StatusCodes.Status422UnprocessableEntity, "A selected branch does not exist on the remote."),
        ConnectedRepositoryErrorCodes.RemoteBranchLookupLimit => (StatusCodes.Status422UnprocessableEntity, "The remote has too many branches to check the selection."),
        ConnectedRepositoryErrorCodes.RemoteInvalid => (StatusCodes.Status502BadGateway, "The provider returned an unusable repository."),
        ConnectedRepositoryErrorCodes.GenerationLockConflict => (StatusCodes.Status409Conflict, "The repository has a running generation. Try again later."),
        ConnectedRepositoryErrorCodes.RepositoryGenerationActive => (StatusCodes.Status409Conflict, "The repository is being generated. Try again later."),
        ConnectedRepositoryErrorCodes.Conflict => (StatusCodes.Status409Conflict, "The repository was changed at the same time. Try again."),
        ConnectedRepositoryErrorCodes.AlreadyConnected => (StatusCodes.Status409Conflict, "The repository already uses another Git connection."),
        IndexedBranchRemovalErrorCodes.BranchNotFound => (StatusCodes.Status404NotFound, "The branch does not exist."),
        IndexedBranchRemovalErrorCodes.BranchJobActive => (StatusCodes.Status409Conflict, "A job of this branch is running. Try again when it has finished."),
        _ => (StatusCodes.Status500InternalServerError, "The request failed.")
    };
}
