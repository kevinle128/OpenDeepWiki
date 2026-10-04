using System.Security.Claims;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Tests.Chat.Sessions;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

public class GitConnectionAuthorizationServiceTests
{
    private const string CreatorId = "creator-user";
    private const string OtherUserId = "other-user";
    private const string AdminUserId = "admin-user";

    [Fact]
    public async Task CanUse_WhenAuthenticatedNonCreatorUsesEnabledConnection_ReturnsTrue()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context, OtherUserId);

        Assert.True(service.CanUse(CreateConnection()));
    }

    [Fact]
    public async Task CanUse_WhenCallerIsAnonymous_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        var service = new GitConnectionAuthorizationService(
            new FakeUserContext(null, isAuthenticated: false),
            context);

        Assert.False(service.CanUse(CreateConnection()));
    }

    [Fact]
    public async Task CanUse_WhenAuthenticatedCallerHasNoUserId_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        var service = new GitConnectionAuthorizationService(new FakeUserContext(null), context);

        Assert.False(service.CanUse(CreateConnection()));
    }

    [Fact]
    public async Task CanUse_WhenConnectionIsDisabled_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context, CreatorId);
        var connection = CreateConnection();
        connection.IsEnabled = false;

        Assert.False(service.CanUse(connection));
    }

    [Fact]
    public async Task CanUse_WhenConnectionIsSoftDeleted_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context, CreatorId);
        var connection = CreateConnection();
        connection.MarkAsDeleted();

        Assert.False(service.CanUse(connection));
    }

    [Fact]
    public async Task CanMaintainAsync_WhenCallerIsCreator_ReturnsTrue()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context, CreatorId);

        Assert.True(await service.CanMaintainAsync(CreateConnection()));
    }

    [Fact]
    public async Task CanMaintainAsync_WhenCallerIsNonCreatorWithoutAdminRole_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context, OtherUserId);

        Assert.False(await service.CanMaintainAsync(CreateConnection()));
    }

    [Fact]
    public async Task CanMaintainAsync_WhenCallerIsCurrentAdminInDatabase_ReturnsTrue()
    {
        await using var context = await CreateContextAsync();
        var service = CreateService(context, AdminUserId);

        Assert.True(await service.CanMaintainAsync(CreateConnection()));
    }

    [Fact]
    public async Task CanMaintainAsync_WhenOnlyTheJwtClaimSaysAdmin_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        var service = new GitConnectionAuthorizationService(
            new FakeUserContext(OtherUserId, isAdminClaim: true),
            context);

        Assert.False(await service.CanMaintainAsync(CreateConnection()));
    }

    [Fact]
    public async Task CanMaintainAsync_WhenAdminRoleWasRevokedAfterTokenIssue_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        var adminLink = context.UserRoles.Single(link => link.UserId == AdminUserId);
        adminLink.MarkAsDeleted();
        await context.SaveChangesAsync();
        var service = new GitConnectionAuthorizationService(
            new FakeUserContext(AdminUserId, isAdminClaim: true),
            context);

        Assert.False(await service.CanMaintainAsync(CreateConnection()));
    }

    [Fact]
    public async Task CanMaintainAsync_WhenAdminRoleIsInactiveOrDeleted_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        var adminRole = context.Roles.Single(role => role.Name == "Admin");
        adminRole.IsActive = false;
        await context.SaveChangesAsync();
        var service = CreateService(context, AdminUserId);

        Assert.False(await service.CanMaintainAsync(CreateConnection()));

        adminRole.IsActive = true;
        adminRole.MarkAsDeleted();
        await context.SaveChangesAsync();

        Assert.False(await service.CanMaintainAsync(CreateConnection()));
    }

    [Fact]
    public async Task CanMaintainAsync_WhenCreatorWasSoftDeleted_OnlyAdminMaintains()
    {
        await using var context = await CreateContextAsync();
        context.Users.Single(user => user.Id == CreatorId).MarkAsDeleted();
        await context.SaveChangesAsync();

        Assert.False(await CreateService(context, CreatorId).CanMaintainAsync(CreateConnection()));
        Assert.False(await CreateService(context, OtherUserId).CanMaintainAsync(CreateConnection()));
        Assert.True(await CreateService(context, AdminUserId).CanMaintainAsync(CreateConnection()));
    }

    [Fact]
    public async Task CanMaintainAsync_WhenCallerIsAnonymous_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        var service = new GitConnectionAuthorizationService(
            new FakeUserContext(null, isAuthenticated: false),
            context);

        Assert.False(await service.CanMaintainAsync(CreateConnection()));
    }

    [Fact]
    public async Task CanMaintainAsync_DoesNotUseRepositoryOwnership()
    {
        await using var context = await CreateContextAsync();
        context.Repositories.Add(new Repository
        {
            Id = "repo-1",
            OwnerUserId = OtherUserId,
            GitUrl = "https://github.com/acme/widgets",
            OrgName = "acme",
            RepoName = "widgets",
            GitConnectionId = "connection-1"
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, OtherUserId);

        Assert.False(await service.CanMaintainAsync(CreateConnection()));
    }

    private static GitConnectionAuthorizationService CreateService(TestDbContext context, string userId)
        => new(new FakeUserContext(userId), context);

    private static GitConnection CreateConnection() => new()
    {
        Id = "connection-1",
        Provider = GitProvider.GitHub,
        NormalizedServerUrl = "https://github.com",
        ExternalAccountId = "1001",
        AccountName = "octocat",
        ProtectedToken = "protected",
        CreatedByUserId = CreatorId,
        IsEnabled = true
    };

    private static async Task<TestDbContext> CreateContextAsync()
    {
        var context = TestDbContext.Create();
        var adminRole = new Role { Id = "role-admin", Name = "Admin", Description = "Admin", IsActive = true };
        context.Roles.Add(adminRole);
        context.Users.AddRange(
            new User { Id = CreatorId, Name = "creator", Email = "creator@example.com" },
            new User { Id = OtherUserId, Name = "other", Email = "other@example.com" },
            new User { Id = AdminUserId, Name = "admin", Email = "admin@example.com" });
        context.UserRoles.Add(new UserRole { Id = "link-admin", UserId = AdminUserId, RoleId = adminRole.Id });
        await context.SaveChangesAsync();
        return context;
    }

    private sealed class FakeUserContext(
        string? userId,
        bool isAuthenticated = true,
        bool isAdminClaim = false) : IUserContext
    {
        public string? UserId { get; } = userId;
        public string? UserName => UserId;
        public string? Email => null;
        public bool IsAuthenticated { get; } = isAuthenticated;

        public ClaimsPrincipal? User { get; } = BuildPrincipal(userId, isAuthenticated, isAdminClaim);

        private static ClaimsPrincipal BuildPrincipal(string? userId, bool isAuthenticated, bool isAdminClaim)
        {
            if (!isAuthenticated)
            {
                return new ClaimsPrincipal(new ClaimsIdentity());
            }

            var claims = new List<Claim>();
            if (!string.IsNullOrWhiteSpace(userId))
            {
                claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));
            }

            if (isAdminClaim)
            {
                claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }
    }
}
