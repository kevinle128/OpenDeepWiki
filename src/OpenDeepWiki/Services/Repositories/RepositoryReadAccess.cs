using System.Linq.Expressions;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Branch access in the shared workspace. Authenticated users can use connected repositories.
/// Legacy private repositories remain limited to their owner or an Admin.
/// Repository deletion and visibility changes use separate authorization rules.
/// </summary>
public static class RepositoryReadAccess
{
    public static Expression<Func<Repository, bool>> VisibleTo(IUserContext userContext)
    {
        if (userContext.User?.IsInRole("Admin") == true)
        {
            return repository => true;
        }

        var userId = userContext.IsAuthenticated ? userContext.UserId : null;
        return repository => repository.IsPublic || (userId != null &&
            (repository.OwnerUserId == userId || repository.GitConnectionId != null));
    }
}
