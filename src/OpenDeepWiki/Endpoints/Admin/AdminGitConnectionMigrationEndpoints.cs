using Microsoft.AspNetCore.Mvc;
using OpenDeepWiki.Services.GitConnections;

namespace OpenDeepWiki.Endpoints.Admin;

/// <summary>
/// Admin operations for the legacy credential backfill. They live under the Admin route group, and the service
/// checks the database for a current Admin again. Answers carry counts, repository IDs, and stable codes only.
/// </summary>
public static class AdminGitConnectionMigrationEndpoints
{
    public static RouteGroupBuilder MapAdminGitConnectionMigrationEndpoints(this RouteGroupBuilder group)
    {
        var migration = group.MapGroup("/git-connection-migration")
            .WithTags("Admin - Git connection migration");

        migration.MapGet("/dry-run", (
            [FromServices] ILegacyGitCredentialMigrationService service,
            CancellationToken cancellationToken) =>
            RunAsync(() => service.DryRunAsync(cancellationToken)));

        migration.MapGet("/status", (
            [FromServices] ILegacyGitCredentialMigrationService service,
            CancellationToken cancellationToken) =>
            RunAsync(() => service.GetStatusAsync(cancellationToken)));

        migration.MapPost("/migrate", (
            [FromBody] LegacyCredentialMigrationRequest? request,
            [FromServices] ILegacyGitCredentialMigrationService service,
            CancellationToken cancellationToken) =>
            RunAsync(() => service.MigrateAsync(request ?? new LegacyCredentialMigrationRequest(), cancellationToken)));

        migration.MapPost("/retry", (
            [FromBody] RetryLegacyCredentialRequest? request,
            [FromServices] ILegacyGitCredentialMigrationService service,
            CancellationToken cancellationToken) =>
            RunAsync(() => service.RetryAsync(request?.RepositoryIds ?? [], cancellationToken)));

        return group;
    }

    private static async Task<IResult> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return Results.Ok(new { success = true, data = await operation() });
        }
        catch (LegacyCredentialMigrationException ex)
        {
            var status = ex.ErrorCode == LegacyCredentialErrorCodes.AdminRequired
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status400BadRequest;
            return Results.Json(new { success = false, errorCode = ex.ErrorCode, message = ex.Message }, statusCode: status);
        }
    }
}

public sealed record RetryLegacyCredentialRequest(IReadOnlyList<string>? RepositoryIds);
