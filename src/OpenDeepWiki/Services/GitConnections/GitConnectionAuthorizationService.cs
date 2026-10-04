using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;

namespace OpenDeepWiki.Services.GitConnections;

public sealed class GitConnectionAuthorizationService : IGitConnectionAuthorizationService
{
    private const string AdminRoleName = "Admin";

    private readonly IUserContext _userContext;
    private readonly IContext _context;

    public GitConnectionAuthorizationService(IUserContext userContext, IContext context)
    {
        _userContext = userContext;
        _context = context;
    }

    public bool CanUse(GitConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return _userContext.IsAuthenticated
               && !string.IsNullOrWhiteSpace(_userContext.UserId)
               && connection.IsEnabled
               && !connection.IsDeleted;
    }

    public async Task<bool> CanMaintainAsync(GitConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var userId = _userContext.UserId;
        if (!await CallerExistsAsync(userId, cancellationToken))
        {
            return false;
        }

        return string.Equals(connection.CreatedByUserId, userId, StringComparison.Ordinal)
               || await HasAdminRoleAsync(userId!, cancellationToken);
    }

    public async Task<bool> IsAdminAsync(CancellationToken cancellationToken = default)
    {
        var userId = _userContext.UserId;
        return await CallerExistsAsync(userId, cancellationToken)
               && await HasAdminRoleAsync(userId!, cancellationToken);
    }

    /// <summary>
    /// A token can outlive a deleted account, so the caller must still exist.
    /// </summary>
    private async Task<bool> CallerExistsAsync(string? userId, CancellationToken cancellationToken)
    {
        if (!_userContext.IsAuthenticated || string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        return await _context.Users
            .AnyAsync(user => user.Id == userId && !user.IsDeleted, cancellationToken);
    }

    private Task<bool> HasAdminRoleAsync(string userId, CancellationToken cancellationToken)
        => _context.UserRoles
            .Where(link => link.UserId == userId && !link.IsDeleted)
            .Join(
                _context.Roles.Where(role => role.Name == AdminRoleName && role.IsActive && !role.IsDeleted),
                link => link.RoleId,
                role => role.Id,
                (_, _) => 1)
            .AnyAsync(cancellationToken);
}
