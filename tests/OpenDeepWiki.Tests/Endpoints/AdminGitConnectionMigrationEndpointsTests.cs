using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Endpoints.Admin;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Tests.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Endpoints;

/// <summary>
/// Admin-only migration routes through the real HTTP pipeline. The route group needs the Admin role in the token,
/// and the service checks the database again, so a revoked role cannot run a backfill with an old token.
/// </summary>
public sealed class AdminGitConnectionMigrationEndpointsTests : IAsyncLifetime
{
    private const string Pat = "ghp_CANARY_admin_endpoint_0123456789";
    private const string UrlSecret = "ghp_CANARY_admin_url_0123456789";
    private const string Base = "/api/admin/git-connection-migration";

    private readonly SqliteScratchDatabase _database;
    private readonly LegacyFakeProvider _github = new(GitProvider.GitHub);
    private readonly IGitConnectionSecretProtector _protector =
        new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider());
    private TestEndpointHost _host = null!;

    public AdminGitConnectionMigrationEndpointsTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
        _github.Identities[Pat] = new GitProviderIdentity("1001", "octocat");
    }

    public async Task InitializeAsync()
    {
        await using (var context = _database.Open())
        {
            context.Users.Add(new User { Id = "admin-1", Name = "admin-1", Email = "admin@example.com" });
            context.Users.Add(new User { Id = "stale-admin", Name = "stale-admin", Email = "stale@example.com" });
            var role = new Role { Id = "role-admin", Name = "Admin", Description = "Admin", IsActive = true };
            context.Roles.Add(role);
            context.UserRoles.Add(new UserRole { Id = "link-admin", UserId = "admin-1", RoleId = role.Id });
            context.Repositories.Add(Legacy("r1", "https://github.com/acme/one.git", Pat));
            context.Repositories.Add(Legacy("r2", $"https://u:{UrlSecret}@github.com/acme/two.git", null));
            await context.SaveChangesAsync();
        }

        _host = await TestEndpointHost.StartAsync(
            services =>
            {
                services.AddAuthorization(options => options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin")));
                services.AddScoped<IUserContext, UserContext>();
                services.AddScoped<IContext>(_ => _database.Open());
                services.AddSingleton(_protector);
                services.AddScoped<IGitConnectionAuthorizationService, GitConnectionAuthorizationService>();
                services.AddSingleton<IGitProviderClientResolver>(new GitProviderClientResolver([_github, new LegacyFakeProvider(GitProvider.GitLab)]));
                services.AddScoped<IGitCredentialResolver, GitCredentialResolver>();
                services.AddSingleton<ILogger<GitCredentialResolver>>(NullLogger<GitCredentialResolver>.Instance);
                services.AddSingleton<ILogger<BranchActionAuditor>>(NullLogger<BranchActionAuditor>.Instance);
                services.AddSingleton<ILogger<ConnectedRepositoryService>>(NullLogger<ConnectedRepositoryService>.Instance);
                services.AddSingleton<ILogger<LegacyGitCredentialMigrationService>>(NullLogger<LegacyGitCredentialMigrationService>.Instance);
                services.AddScoped<IRepositoryGenerationLockService, RepositoryGenerationLockService>();
                services.AddScoped<IBranchActionAuditor, BranchActionAuditor>();
                services.AddScoped<IConnectedRepositoryService, ConnectedRepositoryService>();
                services.AddScoped<ILegacyGitCredentialMigrationService, LegacyGitCredentialMigrationService>();
            },
            app => app.MapGroup("/api/admin").RequireAuthorization("AdminOnly").MapAdminGitConnectionMigrationEndpoints());
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _database.Dispose();
    }

    [Theory]
    [InlineData("GET", "/dry-run")]
    [InlineData("GET", "/status")]
    [InlineData("POST", "/migrate")]
    [InlineData("POST", "/retry")]
    public async Task EveryRoute_WhenAnonymous_Returns401(string method, string route)
    {
        var response = await _host.SendAsync(method, Base + route, user: null, body: new { repositoryIds = new[] { "r1" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Empty(_github.Validated);
    }

    [Theory]
    [InlineData("GET", "/dry-run")]
    [InlineData("GET", "/status")]
    [InlineData("POST", "/migrate")]
    [InlineData("POST", "/retry")]
    public async Task EveryRoute_ForANonAdminToken_Returns403AndNeverCallsTheProvider(string method, string route)
    {
        var response = await _host.SendAsync(method, Base + route, user: "user-1", body: new { repositoryIds = new[] { "r1" } }, role: "User");

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Empty(_github.Validated);
    }

    [Theory]
    [InlineData("GET", "/dry-run")]
    [InlineData("GET", "/status")]
    [InlineData("POST", "/migrate")]
    [InlineData("POST", "/retry")]
    public async Task EveryRoute_ForAnAdminTokenWhoseDatabaseRoleIsGone_Returns403WithAStableCode(string method, string route)
    {
        var response = await _host.SendAsync(method, Base + route, user: "stale-admin", body: new { repositoryIds = new[] { "r1" } }, role: "Admin");

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.False(response.Json.GetProperty("success").GetBoolean());
        Assert.Equal(LegacyCredentialErrorCodes.AdminRequired, response.Json.GetProperty("errorCode").GetString());
        Assert.Empty(_github.Validated);
        await using var context = _database.Open();
        Assert.Empty(await context.GitConnections.ToListAsync());
    }

    [Fact]
    public async Task DryRun_ForAnAdmin_ReportsCountsAndCodesWithoutSecretsOrWrites()
    {
        var response = await _host.SendAsync("GET", Base + "/dry-run", "admin-1", role: "Admin");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.True(response.Json.GetProperty("success").GetBoolean());
        var groups = response.Json.GetProperty("data").GetProperty("groups");
        Assert.Contains(groups.EnumerateArray(), group =>
            group.GetProperty("provider").GetString() == "GitHub" && group.GetProperty("state").GetString() == "Pending");
        Assert.DoesNotContain(Pat, response.Body);
        Assert.DoesNotContain(UrlSecret, response.Body);
        Assert.Empty(_github.Validated);
        await using var context = _database.Open();
        Assert.Empty(await context.GitCredentialMigrationRecords.ToListAsync());
    }

    [Fact]
    public async Task Migrate_ThenStatus_ShowsProgressAndTheRepairQueueWithoutSecrets()
    {
        var migrate = await _host.SendAsync("POST", Base + "/migrate", "admin-1", new { batchSize = 10 }, role: "Admin");
        var status = await _host.SendAsync("GET", Base + "/status", "admin-1", role: "Admin");

        Assert.Equal(HttpStatusCode.OK, migrate.Status);
        var result = migrate.Json.GetProperty("data");
        Assert.Equal(2, result.GetProperty("processed").GetInt32());
        Assert.Equal(1, result.GetProperty("migrated").GetInt32());
        Assert.Equal(1, result.GetProperty("blocked").GetInt32());
        Assert.False(result.GetProperty("hasMore").GetBoolean());

        var data = status.Json.GetProperty("data");
        Assert.False(data.GetProperty("contractGateClear").GetBoolean());
        var queue = data.GetProperty("repairQueue");
        var item = Assert.Single(queue.EnumerateArray());
        Assert.Equal("r2", item.GetProperty("repositoryId").GetString());
        Assert.Equal(LegacyCredentialErrorCodes.UrlContainsUserInfo, item.GetProperty("errorCode").GetString());
        foreach (var body in new[] { migrate.Body, status.Body })
        {
            Assert.DoesNotContain(Pat, body);
            Assert.DoesNotContain(UrlSecret, body);
            Assert.DoesNotContain("protectedToken", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Migrate_WithoutABody_UsesTheDefaultBatchSize()
    {
        var response = await _host.SendAsync("POST", Base + "/migrate", "admin-1", role: "Admin");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(2, response.Json.GetProperty("data").GetProperty("processed").GetInt32());
    }

    [Fact]
    public async Task Migrate_WithAnInvalidBatchSize_Returns400WithAStableCode()
    {
        var response = await _host.SendAsync("POST", Base + "/migrate", "admin-1", new { batchSize = 0 }, role: "Admin");

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(LegacyCredentialErrorCodes.InvalidRequest, response.Json.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Retry_ForABlockedRepository_RunsItAgainAndReturnsTheResult()
    {
        _github.Failures[Pat] = new GitProviderException(GitProviderErrorCodes.RateLimited);
        await _host.SendAsync("POST", Base + "/migrate", "admin-1", role: "Admin");
        _github.Failures.Clear();

        var response = await _host.SendAsync("POST", Base + "/retry", "admin-1", new { repositoryIds = new[] { "r1" } }, role: "Admin");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(1, response.Json.GetProperty("data").GetProperty("migrated").GetInt32());
    }

    [Fact]
    public async Task Retry_WithoutRepositoryIds_Returns400()
    {
        var response = await _host.SendAsync("POST", Base + "/retry", "admin-1", new { repositoryIds = Array.Empty<string>() }, role: "Admin");

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(LegacyCredentialErrorCodes.InvalidRequest, response.Json.GetProperty("errorCode").GetString());
    }

    private static Repository Legacy(string id, string url, string? password) => new()
    {
        Id = id,
        OwnerUserId = "user-1",
        GitUrl = url,
        OrgName = "org-" + id,
        RepoName = "repo-" + id,
        AuthPassword = password,
        IsPublic = password is null,
        Status = RepositoryStatus.Completed,
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(id == "r1" ? 0 : 1)
    };
}
