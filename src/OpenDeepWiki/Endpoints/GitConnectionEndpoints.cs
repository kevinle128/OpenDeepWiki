using Microsoft.AspNetCore.Mvc;
using OpenDeepWiki.Models.GitConnections;
using OpenDeepWiki.Services.GitConnections;

namespace OpenDeepWiki.Endpoints;

/// <summary>
/// Authenticated API for shared Git connections and their provider catalogs.
/// Success: <c>{ success: true, data }</c>. Failure: <c>{ success: false, errorCode, message }</c>.
/// Messages are fixed text per code and never carry provider text or credentials.
/// </summary>
public static class GitConnectionEndpoints
{
    private const int DefaultPageSize = GitProviderPaging.DefaultPageSize;

    public static IEndpointRouteBuilder MapGitConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/git-connections")
            .RequireAuthorization()
            .WithTags("Git Connections");

        group.MapGet("", ListAsync).WithName("ListGitConnections");
        group.MapPost("", CreateAsync).WithName("CreateGitConnection");
        group.MapGet("/{id}", GetAsync).WithName("GetGitConnection");
        group.MapPut("/{id}", UpdateAsync).WithName("UpdateGitConnection");
        group.MapDelete("/{id}", DeleteAsync).WithName("DeleteGitConnection");
        group.MapPost("/{id}/test", TestAsync).WithName("TestGitConnection");
        group.MapPost("/{id}/enable", EnableAsync).WithName("EnableGitConnection");
        group.MapPost("/{id}/disable", DisableAsync).WithName("DisableGitConnection");
        group.MapGet("/{id}/repositories", ListRepositoriesAsync).WithName("ListGitConnectionRepositories");
        group.MapGet("/{id}/repositories/{providerRepositoryId}/branches", ListBranchesAsync).WithName("ListGitConnectionBranches");
        group.MapGet("/{id}/audit-events", ListAuditEventsAsync).WithName("ListGitConnectionAuditEvents");

        return app;
    }

    private static Task<IResult> ListAsync(
        HttpContext http, [FromServices] IGitConnectionService service, CancellationToken ct)
        => RunAsync(http, async () => Ok(await service.ListAsync(ct)));

    private static Task<IResult> CreateAsync(
        HttpContext http,
        [FromBody] CreateGitConnectionRequest request,
        [FromServices] IGitConnectionService service,
        CancellationToken ct)
        => RunAsync(http, async () =>
        {
            var result = await service.CreateAsync(request, ct);
            var existing = result.Outcome == GitConnectionCreateOutcomes.Existing;
            return Results.Json(
                new { success = true, data = result.Connection, existing },
                statusCode: existing ? StatusCodes.Status200OK : StatusCodes.Status201Created);
        });

    private static Task<IResult> GetAsync(
        HttpContext http, string id, [FromServices] IGitConnectionService service, CancellationToken ct)
        => RunAsync(http, async () => Ok(await service.GetAsync(id, ct)));

    private static Task<IResult> UpdateAsync(
        HttpContext http,
        string id,
        [FromBody] UpdateGitConnectionRequest request,
        [FromServices] IGitConnectionService service,
        CancellationToken ct)
        => RunAsync(http, async () => Ok(await service.UpdateAsync(id, request, ct)));

    private static Task<IResult> DeleteAsync(
        HttpContext http, string id, [FromServices] IGitConnectionService service, CancellationToken ct)
        => RunAsync(http, async () =>
        {
            await service.DeleteAsync(id, ct);
            return Results.Json(new { success = true });
        });

    private static Task<IResult> TestAsync(
        HttpContext http, string id, [FromServices] IGitConnectionService service, CancellationToken ct)
        => RunAsync(http, async () => Ok(await service.TestAsync(id, ct)));

    private static Task<IResult> EnableAsync(
        HttpContext http, string id, [FromServices] IGitConnectionService service, CancellationToken ct)
        => RunAsync(http, async () => Ok(await service.SetEnabledAsync(id, true, ct)));

    private static Task<IResult> DisableAsync(
        HttpContext http, string id, [FromServices] IGitConnectionService service, CancellationToken ct)
        => RunAsync(http, async () => Ok(await service.SetEnabledAsync(id, false, ct)));

    private static Task<IResult> ListRepositoriesAsync(
        HttpContext http,
        string id,
        [FromQuery] string? cursor,
        [FromServices] IGitConnectionService service,
        CancellationToken ct,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] string? q = null,
        [FromQuery] string? visibility = null,
        [FromQuery] string? sort = null)
        => RunAsync(http, async () => Ok(q is not null || visibility is not null || sort is not null
            ? await service.ListCatalogAsync(id, cursor, pageSize, q, visibility, sort, ct)
            : await service.ListRepositoriesAsync(id, cursor, pageSize, ct)));

    private static Task<IResult> ListBranchesAsync(
        HttpContext http,
        string id,
        string providerRepositoryId,
        [FromQuery] string? cursor,
        [FromServices] IGitConnectionService service,
        CancellationToken ct,
        [FromQuery] int pageSize = DefaultPageSize)
        => RunAsync(http, async () => Ok(await service.ListBranchesAsync(id, providerRepositoryId, cursor, pageSize, ct)));

    private static Task<IResult> ListAuditEventsAsync(
        HttpContext http,
        string id,
        [FromServices] IGitConnectionService service,
        CancellationToken ct,
        [FromQuery] int limit = 30)
        => RunAsync(http, async () => Ok(await service.ListAuditEventsAsync(id, limit, ct)));

    private static IResult Ok<T>(T data) => Results.Json(new { success = true, data });

    private static async Task<IResult> RunAsync(HttpContext http, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is GitConnectionServiceException or GitProviderException)
        {
            return ToErrorResult(ex, http);
        }
    }

    /// <summary>
    /// Maps a known failure to its HTTP status and stable code. A rejected provider token is 422, not 401,
    /// because 401 would make the web client treat the user's own session as expired.
    /// </summary>
    internal static IResult ToErrorResult(Exception exception, HttpContext http)
    {
        var (code, retryAfter) = exception switch
        {
            GitConnectionServiceException service => (service.ErrorCode, (TimeSpan?)null),
            GitProviderException provider => (provider.Code, provider.RetryAfter),
            _ => throw new ArgumentException("Only connection and provider failures have a stable mapping.", nameof(exception))
        };

        if (retryAfter is { } wait)
        {
            http.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString("0");
        }

        var (status, message) = Describe(code);
        return Results.Json(new { success = false, errorCode = code, message }, statusCode: status);
    }

    internal static (int Status, string Message) Describe(string code) => code switch
    {
        GitConnectionErrorCodes.Unauthorized => (StatusCodes.Status401Unauthorized, "Sign in to continue."),
        GitConnectionErrorCodes.NotFound => (StatusCodes.Status404NotFound, "The connection does not exist."),
        GitConnectionErrorCodes.MaintenanceForbidden => (StatusCodes.Status403Forbidden, "Only the creator or an administrator can change this connection."),
        GitConnectionErrorCodes.Disabled => (StatusCodes.Status409Conflict, "The connection is disabled."),
        GitConnectionErrorCodes.InUse => (StatusCodes.Status409Conflict, "Repositories still use this connection."),
        GitConnectionErrorCodes.Conflict => (StatusCodes.Status409Conflict, "The connection was changed by someone else. Reload and try again."),
        GitConnectionErrorCodes.AccountMismatch => (StatusCodes.Status409Conflict, "The token belongs to a different provider account."),
        GitConnectionErrorCodes.SecretUnreadable => (StatusCodes.Status409Conflict, "The stored credential cannot be read. Replace the token."),
        GitConnectionErrorCodes.InvalidProvider => (StatusCodes.Status400BadRequest, "The provider is not supported."),
        GitConnectionErrorCodes.InvalidToken => (StatusCodes.Status400BadRequest, "The token is missing or malformed."),
        GitConnectionErrorCodes.InvalidDisplayName => (StatusCodes.Status400BadRequest, "The display name is not valid."),
        GitProviderErrorCodes.ServerUrlInvalid => (StatusCodes.Status400BadRequest, "The server URL must be an HTTPS origin."),
        GitProviderErrorCodes.ServerUrlBlocked => (StatusCodes.Status400BadRequest, "The server address is not allowed."),
        GitProviderErrorCodes.InvalidCursor => (StatusCodes.Status400BadRequest, "The cursor is not valid."),
        GitProviderErrorCodes.InvalidRepositoryId => (StatusCodes.Status400BadRequest, "The repository ID is not valid."),
        GitProviderErrorCodes.UnsupportedProvider => (StatusCodes.Status400BadRequest, "The provider is not supported."),
        GitProviderErrorCodes.Unauthorized => (StatusCodes.Status422UnprocessableEntity, "The provider rejected the token."),
        GitProviderErrorCodes.Forbidden => (StatusCodes.Status422UnprocessableEntity, "The token has no permission for this request."),
        GitProviderErrorCodes.NotFound => (StatusCodes.Status404NotFound, "The provider did not find the resource."),
        GitProviderErrorCodes.RateLimited => (StatusCodes.Status429TooManyRequests, "The provider rate limit is reached. Try again later."),
        GitProviderErrorCodes.Timeout => (StatusCodes.Status504GatewayTimeout, "The provider did not answer in time."),
        GitProviderErrorCodes.Unavailable
            or GitProviderErrorCodes.DnsFailure
            or GitProviderErrorCodes.TlsFailure
            or GitProviderErrorCodes.RedirectBlocked
            or GitProviderErrorCodes.InvalidResponse
            or GitProviderErrorCodes.RequestRejected => (StatusCodes.Status502BadGateway, "The provider request failed."),
        _ => (StatusCodes.Status500InternalServerError, "The request failed.")
    };
}
