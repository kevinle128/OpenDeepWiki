using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Records who started an action on an indexed branch. Every authenticated user can run these actions,
/// so the actor must stay traceable. Repositories without a Git connection have no audit table to write to,
/// so their actions are logged with identifiers only.
/// </summary>
public interface IBranchActionAuditor
{
    /// <summary>
    /// Adds an audit event to the current context without saving. The caller's own save persists it,
    /// so the event commits or fails together with the action. Does nothing for a repository without a connection.
    /// </summary>
    void Stage(Repository repository, string? actorUserId, GitConnectionAuditEventType eventType);

    /// <summary>
    /// Writes the event on its own after an action succeeded. A write problem is logged and never hides the action result.
    /// </summary>
    Task RecordAsync(
        string repositoryId,
        string? branchId,
        string? actorUserId,
        GitConnectionAuditEventType eventType,
        CancellationToken cancellationToken);
}

public sealed class BranchActionAuditor(IContext context, ILogger<BranchActionAuditor> logger) : IBranchActionAuditor
{
    public void Stage(Repository repository, string? actorUserId, GitConnectionAuditEventType eventType)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (string.IsNullOrWhiteSpace(repository.GitConnectionId))
        {
            return;
        }

        context.GitConnectionAuditEvents.Add(NewEvent(repository.GitConnectionId, repository.Id, actorUserId, eventType));
    }

    public async Task RecordAsync(
        string repositoryId,
        string? branchId,
        string? actorUserId,
        GitConnectionAuditEventType eventType,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Branch action. Action: {Action}, ActorUserId: {ActorUserId}, RepositoryId: {RepositoryId}, BranchId: {BranchId}",
            eventType, actorUserId, repositoryId, branchId);

        GitConnectionAuditEvent? audit = null;
        try
        {
            var connectionId = await context.Repositories
                .AsNoTracking()
                .Where(repository => repository.Id == repositoryId)
                .Select(repository => repository.GitConnectionId)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return;
            }

            audit = NewEvent(connectionId, repositoryId, actorUserId, eventType);
            context.GitConnectionAuditEvents.Add(audit);
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            if (audit is not null)
            {
                context.GitConnectionAuditEvents.Remove(audit);
            }

            logger.LogWarning(ex, "Branch action audit event could not be written. RepositoryId: {RepositoryId}", repositoryId);
        }
    }

    private static GitConnectionAuditEvent NewEvent(
        string connectionId,
        string repositoryId,
        string? actorUserId,
        GitConnectionAuditEventType eventType)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            GitConnectionId = connectionId,
            RepositoryId = repositoryId,
            ActorUserId = actorUserId,
            EventType = eventType,
            Outcome = GitConnectionAuditOutcome.Success,
            CorrelationId = Activity.Current?.TraceId.ToString()
        };
}
