using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Endpoints;
using OpenDeepWiki.Endpoints.Admin;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models;
using OpenDeepWiki.Models.GitConnections;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Organizations;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Tests.Services.GitConnections;
using OpenDeepWiki.Tests.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Endpoints;

/// <summary>
/// Connect, add-branches, list, and remove routes through the real HTTP pipeline. Every authenticated user can use
/// them. Repository deletion and visibility keep their own stricter rules.
/// </summary>
public sealed class ConnectedRepositoryEndpointsTests : IAsyncLifetime
{
    private const string Pat = "ghp_CANARY_connected_endpoint_0123456789";

    private readonly SqliteScratchDatabase _database;
    private readonly FakeRemoteCatalog _github = new(GitProvider.GitHub);
    private readonly IGitConnectionSecretProtector _protector =
        new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider());
    private readonly string _root = Path.Combine(TestPaths.ScratchRoot, $"connected-endpoints-{Guid.NewGuid():N}");
    private TestEndpointHost _host = null!;

    public ConnectedRepositoryEndpointsTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
        _github.AddRepository("42", "acme/widgets", "Public", "main", "main", "dev", "release");
        _github.AddRepository("43", "acme/vault", "Private", "main", "main", "dev");
    }

    public async Task InitializeAsync()
    {
        await using (var context = _database.Open())
        {
            context.GitConnections.Add(new GitConnection
            {
                Id = "c1", Provider = GitProvider.GitHub, NormalizedServerUrl = "https://github.com", ExternalAccountId = "1001",
                DisplayName = "octocat", AccountName = "octocat", ProtectedToken = _protector.Protect(Pat), CreatedByUserId = "user-1"
            });
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
                services.AddSingleton<IGitProviderClientResolver>(new GitProviderClientResolver([_github, new FakeRemoteCatalog(GitProvider.GitLab)]));
                services.AddScoped<IRepositoryGenerationLockService, RepositoryGenerationLockService>();
                services.AddScoped<IBranchFullGenerationCleaner, BranchFullGenerationCleaner>();
                services.AddScoped<IBranchGenerationTaskService, BranchGenerationTaskService>();
                services.AddScoped<IBranchActionAuditor, BranchActionAuditor>();
                services.AddScoped<IConnectedRepositoryService, ConnectedRepositoryService>();
                services.AddScoped<IIndexedBranchRemovalService, IndexedBranchRemovalService>();
                services.AddSingleton(Options.Create(new RepositoryAnalyzerOptions { RepositoriesDirectory = _root }));
            },
            app =>
            {
                app.MapConnectedRepositoryEndpoints();
                app.MapBranchGenerationEndpoints();
                app.MapGroup("/api/admin").RequireAuthorization("AdminOnly").MapAdminRepositoryEndpoints();
                MapVisibilityRoute(app);
            });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _database.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- authentication ----

    [Theory]
    [InlineData("POST", "/api/v1/connected-repositories")]
    [InlineData("GET", "/api/v1/repositories/r1/indexed-branches")]
    [InlineData("POST", "/api/v1/repositories/r1/indexed-branches")]
    [InlineData("DELETE", "/api/v1/repositories/r1/indexed-branches/b1")]
    public async Task EveryRoute_WhenAnonymous_Returns401AndNeverCallsTheProvider(string method, string path)
    {
        var response = await _host.SendAsync(method, path, user: null, body: new { connectionId = "c1" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal(0, _github.RepositoryCalls);
    }

    // ---- connect and add ----

    [Fact]
    public async Task Connect_ByAnyAuthenticatedUser_CreatesTheRepositoryAndQueuesOneTaskPerBranch()
    {
        var response = await ConnectAsync("user-2", "main", "dev");

        Assert.Equal(HttpStatusCode.Created, response.Status);
        Assert.True(response.Json.GetProperty("success").GetBoolean());
        var data = response.Json.GetProperty("data");
        Assert.True(data.GetProperty("repositoryCreated").GetBoolean());
        Assert.Equal("acme", data.GetProperty("orgName").GetString());
        Assert.Equal(2, data.GetProperty("branches").GetArrayLength());
        Assert.All(data.GetProperty("branches").EnumerateArray(), branch =>
        {
            Assert.True(branch.GetProperty("created").GetBoolean());
            Assert.False(string.IsNullOrEmpty(branch.GetProperty("taskId").GetString()));
        });
        Assert.DoesNotContain(Pat, response.Body);
        await using var context = _database.Open();
        Assert.Equal("user-2", (await context.Repositories.SingleAsync()).OwnerUserId);
        Assert.Equal(2, await context.BranchGenerationTasks.CountAsync());
    }

    [Fact]
    public async Task Connect_Repeated_Returns200WithoutNewTasks()
    {
        await ConnectAsync("user-1", "main");

        var again = await ConnectAsync("user-2", "main");

        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.False(again.Json.GetProperty("data").GetProperty("repositoryCreated").GetBoolean());
        Assert.False(again.Json.GetProperty("data").GetProperty("branches")[0].GetProperty("created").GetBoolean());
        await using var context = _database.Open();
        Assert.Equal(1, await context.BranchGenerationTasks.CountAsync());
    }

    [Fact]
    public async Task AddBranches_ByANonOwner_QueuesTasksAndListShowsThem()
    {
        var created = await ConnectAsync("user-1", "main");
        var repositoryId = created.Json.GetProperty("data").GetProperty("repositoryId").GetString()!;

        var add = await _host.SendAsync("POST", $"/api/v1/repositories/{repositoryId}/indexed-branches", "user-2", new { branches = new[] { "dev", "release" } });
        var list = await _host.SendAsync("GET", $"/api/v1/repositories/{repositoryId}/indexed-branches", "user-2");

        Assert.Equal(HttpStatusCode.Created, add.Status);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        var branches = list.Json.GetProperty("data");
        Assert.Equal(["dev", "main", "release"], branches.EnumerateArray().Select(item => item.GetProperty("branchName").GetString()!).Order());
        Assert.All(branches.EnumerateArray(), item => Assert.Equal("Full", item.GetProperty("activeTaskKind").GetString()));
    }

    [Theory]
    [InlineData("{\"connectionId\":\"c1\",\"providerRepositoryId\":\"42\",\"branches\":[]}", HttpStatusCode.BadRequest, "NO_BRANCHES_SELECTED")]
    [InlineData("{\"connectionId\":\"c1\",\"providerRepositoryId\":\"42\",\"branches\":[\"a..b\"]}", HttpStatusCode.BadRequest, "INVALID_BRANCH_NAME")]
    [InlineData("{\"connectionId\":\"\",\"providerRepositoryId\":\"42\",\"branches\":[\"main\"]}", HttpStatusCode.BadRequest, "INVALID_REQUEST")]
    [InlineData("{\"connectionId\":\"missing\",\"providerRepositoryId\":\"42\",\"branches\":[\"main\"]}", HttpStatusCode.NotFound, "CONNECTION_NOT_FOUND")]
    [InlineData("{\"connectionId\":\"c1\",\"providerRepositoryId\":\"42\",\"branches\":[\"main\",\"ghost\"]}", HttpStatusCode.UnprocessableEntity, "REMOTE_BRANCH_NOT_FOUND")]
    [InlineData("{\"connectionId\":\"c1\",\"providerRepositoryId\":\"999\",\"branches\":[\"main\"]}", HttpStatusCode.NotFound, "PROVIDER_NOT_FOUND")]
    public async Task Connect_MapsFailuresToStableCodesAndStatuses(string json, HttpStatusCode status, string code)
    {
        var body = System.Text.Json.JsonSerializer.Deserialize<object>(json);

        var response = await _host.SendAsync("POST", "/api/v1/connected-repositories", "user-1", body);

        Assert.Equal(status, response.Status);
        Assert.False(response.Json.GetProperty("success").GetBoolean());
        Assert.Equal(code, response.Json.GetProperty("errorCode").GetString());
        await using var context = _database.Open();
        Assert.Empty(await context.Repositories.ToListAsync());
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
    }

    [Fact]
    public async Task Connect_WhenABranchIsMissingRemotely_ReportsTheNames()
    {
        var response = await ConnectAsync("user-1", "main", "ghost");

        Assert.Equal(["ghost"], response.Json.GetProperty("branches").EnumerateArray().Select(item => item.GetString()!));
    }

    [Fact]
    public async Task Connect_WhenTheConnectionIsDisabled_Returns409()
    {
        await using (var context = _database.Open())
        {
            (await context.GitConnections.SingleAsync()).IsEnabled = false;
            await context.SaveChangesAsync();
        }

        var response = await ConnectAsync("user-1", "main");

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("CONNECTION_DISABLED", response.Json.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Connect_WhenTheProviderRateLimits_Returns429WithRetryAfterAndNoProviderText()
    {
        _github.Failure = new GitProviderException(GitProviderErrorCodes.RateLimited, TimeSpan.FromSeconds(42));

        var response = await ConnectAsync("user-1", "main");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.Status);
        Assert.Equal("PROVIDER_RATE_LIMITED", response.Json.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(Pat, response.Body);
    }

    [Fact]
    public async Task Connect_WithSkillGenerationOff_StoresItAndTheDefaultStaysOn()
    {
        var off = await _host.SendAsync("POST", "/api/v1/connected-repositories", "user-1",
            new { connectionId = "c1", providerRepositoryId = "42", branches = new[] { "main" }, generateSkill = false });
        var on = await _host.SendAsync("POST", "/api/v1/connected-repositories", "user-1",
            new { connectionId = "c1", providerRepositoryId = "43", branches = new[] { "main" } });

        Assert.Equal(HttpStatusCode.Created, off.Status);
        Assert.Equal(HttpStatusCode.Created, on.Status);
        await using var context = _database.Open();
        var skills = await context.Repositories.ToDictionaryAsync(item => item.RepoName, item => item.GenerateSkill);
        Assert.False(skills["widgets"]);
        Assert.True(skills["vault"]);
    }

    [Fact]
    public void EveryStableErrorCode_HasAnExplicitStatusThatIsNot500()
    {
        var codes = typeof(ConnectedRepositoryErrorCodes).GetFields().Concat(typeof(IndexedBranchRemovalErrorCodes).GetFields())
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .Distinct()
            .ToList();

        Assert.Contains("REPOSITORY_ALREADY_CONNECTED", codes);
        Assert.All(codes, code => Assert.NotEqual(StatusCodes.Status500InternalServerError, ConnectedRepositoryEndpoints.Describe(code).Status));
        Assert.Equal(StatusCodes.Status409Conflict, ConnectedRepositoryEndpoints.Describe(ConnectedRepositoryErrorCodes.AlreadyConnected).Status);
    }

    [Fact]
    public void EveryConnectionErrorCode_HasAnExplicitStatusThatIsNot500()
    {
        var codes = typeof(GitConnectionErrorCodes).GetFields().Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);

        Assert.All(codes, code => Assert.NotEqual(StatusCodes.Status500InternalServerError, GitConnectionEndpoints.Describe(code).Status));
    }

    [Fact]
    public async Task AddBranches_ForAnUnknownRepository_Returns404()
    {
        var response = await _host.SendAsync("POST", "/api/v1/repositories/missing/indexed-branches", "user-2", new { branches = new[] { "main" } });

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal("REPOSITORY_NOT_FOUND", response.Json.GetProperty("errorCode").GetString());
    }

    // ---- removal ----

    [Fact]
    public async Task Remove_ByANonOwner_RemovesTheBranchAndKeepsTheRepository()
    {
        var created = await ConnectAsync("user-1", "main", "dev");
        var repositoryId = created.Json.GetProperty("data").GetProperty("repositoryId").GetString()!;
        var devId = created.Json.GetProperty("data").GetProperty("branches").EnumerateArray()
            .Single(item => item.GetProperty("branchName").GetString() == "dev").GetProperty("branchId").GetString()!;

        var response = await _host.SendAsync("DELETE", $"/api/v1/repositories/{repositoryId}/indexed-branches/{devId}", "user-2");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("dev", response.Json.GetProperty("data").GetProperty("branchName").GetString());
        await using var context = _database.Open();
        Assert.Equal(["main"], await context.RepositoryBranches.Select(item => item.BranchName).ToListAsync());
        Assert.Single(await context.Repositories.ToListAsync());
        Assert.Contains(
            await context.GitConnectionAuditEvents.ToListAsync(),
            item => item.EventType == GitConnectionAuditEventType.BranchRemoved && item.ActorUserId == "user-2");
    }

    [Fact]
    public async Task Remove_WhileAJobIsProcessing_Returns409WithAStableCodeAndChangesNothing()
    {
        var created = await ConnectAsync("user-1", "main");
        var repositoryId = created.Json.GetProperty("data").GetProperty("repositoryId").GetString()!;
        var branch = created.Json.GetProperty("data").GetProperty("branches")[0];
        await using (var context = _database.Open())
        {
            (await context.BranchGenerationTasks.SingleAsync()).Status = BranchGenerationTaskStatus.Processing;
            await context.SaveChangesAsync();
        }

        var response = await _host.SendAsync(
            "DELETE", $"/api/v1/repositories/{repositoryId}/indexed-branches/{branch.GetProperty("branchId").GetString()}", "user-2");

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("BRANCH_JOB_ACTIVE", response.Json.GetProperty("errorCode").GetString());
        await using var verification = _database.Open();
        Assert.Single(await verification.RepositoryBranches.ToListAsync());
    }

    [Fact]
    public async Task Remove_ForAnUnknownBranch_Returns404()
    {
        var created = await ConnectAsync("user-1", "main");
        var repositoryId = created.Json.GetProperty("data").GetProperty("repositoryId").GetString()!;

        var response = await _host.SendAsync("DELETE", $"/api/v1/repositories/{repositoryId}/indexed-branches/missing", "user-2");

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal("BRANCH_NOT_FOUND", response.Json.GetProperty("errorCode").GetString());
    }

    // ---- rights stay where they were ----

    [Fact]
    public async Task RepositoryLevelRights_StayRestricted_ForTheSameUserWhoManagesBranches()
    {
        var created = await ConnectAsync("user-1", "main");
        var repositoryId = created.Json.GetProperty("data").GetProperty("repositoryId").GetString()!;
        var branchId = created.Json.GetProperty("data").GetProperty("branches")[0].GetProperty("branchId").GetString()!;

        var rebuild = await _host.SendAsync("POST", $"/api/v1/repositories/{repositoryId}/branches/{branchId}/generation-tasks/full", "user-2");
        var add = await _host.SendAsync("POST", $"/api/v1/repositories/{repositoryId}/indexed-branches", "user-2", new { branches = new[] { "dev" } });
        var delete = await _host.SendAsync("DELETE", $"/api/admin/repositories/{repositoryId}", "user-2");
        var visibility = await _host.SendAsync("POST", "/api/v1/repositories/visibility", "user-2", new { repositoryId, isPublic = true });
        var ownerVisibility = await _host.SendAsync("POST", "/api/v1/repositories/visibility", "user-1", new { repositoryId, isPublic = true });

        // The shared branch capability: the rebuild conflicts with the queued task, the add succeeds.
        Assert.Equal(HttpStatusCode.Conflict, rebuild.Status);
        Assert.Equal(HttpStatusCode.Created, add.Status);
        Assert.Equal(HttpStatusCode.Forbidden, delete.Status);
        Assert.Equal(HttpStatusCode.Forbidden, visibility.Status);
        Assert.NotEqual(HttpStatusCode.Forbidden, ownerVisibility.Status);
        await using var context = _database.Open();
        Assert.False((await context.Repositories.SingleAsync()).IsDeleted);
    }

    // ---- private repositories ----

    [Fact]
    public async Task PrivateConnectedRepository_ForAnotherUser_AllowsListAddAndRemove()
    {
        var created = await _host.SendAsync(
            "POST", "/api/v1/connected-repositories", "user-1",
            new { connectionId = "c1", providerRepositoryId = "43", branches = new[] { "main" } });
        var data = created.Json.GetProperty("data");
        var repositoryId = data.GetProperty("repositoryId").GetString()!;
        var branchId = data.GetProperty("branches")[0].GetProperty("branchId").GetString()!;

        await using (var setup = _database.Open())
        {
            foreach (var task in await setup.BranchGenerationTasks.ToListAsync())
                task.Status = BranchGenerationTaskStatus.Completed;
            setup.RepositoryGenerationLocks.RemoveRange(await setup.RepositoryGenerationLocks.ToListAsync());
            await setup.SaveChangesAsync();
        }

        var list = await _host.SendAsync("GET", $"/api/v1/repositories/{repositoryId}/indexed-branches", "user-2");
        var add = await _host.SendAsync("POST", $"/api/v1/repositories/{repositoryId}/indexed-branches", "user-2", new { branches = new[] { "dev" } });
        var remove = await _host.SendAsync("DELETE", $"/api/v1/repositories/{repositoryId}/indexed-branches/{branchId}", "user-2");

        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal(HttpStatusCode.Created, add.Status);
        Assert.Equal(HttpStatusCode.OK, remove.Status);
        await using var context = _database.Open();
        Assert.Equal(["dev"], await context.RepositoryBranches.Where(item => !item.IsDeleted).Select(item => item.BranchName).ToListAsync());
    }

    [Fact]
    public async Task PrivateLegacyRepository_ForAnotherUser_RemainsHidden()
    {
        var created = await _host.SendAsync("POST", "/api/v1/connected-repositories", "user-1",
            new { connectionId = "c1", providerRepositoryId = "43", branches = new[] { "main" } });
        var repositoryId = created.Json.GetProperty("data").GetProperty("repositoryId").GetString()!;
        await using (var context = _database.Open())
        {
            (await context.Repositories.SingleAsync()).GitConnectionId = null;
            await context.SaveChangesAsync();
        }
        var list = await _host.SendAsync("GET", $"/api/v1/repositories/{repositoryId}/indexed-branches", "user-2");
        Assert.Equal(HttpStatusCode.NotFound, list.Status);
    }

    [Theory]
    [InlineData("user-1", null)]
    [InlineData("admin-1", "Admin")]
    public async Task PrivateRepository_ForTheOwnerAndAnAdmin_CanListAndAdd(string user, string? role)
    {
        var created = await _host.SendAsync(
            "POST", "/api/v1/connected-repositories", "user-1",
            new { connectionId = "c1", providerRepositoryId = "43", branches = new[] { "main" } });
        var repositoryId = created.Json.GetProperty("data").GetProperty("repositoryId").GetString()!;

        var list = await _host.SendAsync("GET", $"/api/v1/repositories/{repositoryId}/indexed-branches", user, role: role);

        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Single(list.Json.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task NoResponseBodyContainsTheCredential()
    {
        var bodies = new List<string>();
        var created = await ConnectAsync("user-1", "main");
        bodies.Add(created.Body);
        var repositoryId = created.Json.GetProperty("data").GetProperty("repositoryId").GetString()!;
        bodies.Add((await _host.SendAsync("GET", $"/api/v1/repositories/{repositoryId}/indexed-branches", "user-2")).Body);
        bodies.Add((await _host.SendAsync("POST", $"/api/v1/repositories/{repositoryId}/indexed-branches", "user-2", new { branches = new[] { "ghost" } })).Body);

        Assert.All(bodies, body => Assert.DoesNotContain(Pat, body));
        Assert.All(bodies, body => Assert.DoesNotContain("protectedToken", body, StringComparison.OrdinalIgnoreCase));
    }

    // ---- helpers ----

    private Task<TestEndpointHost.TestResponse> ConnectAsync(string user, params string[] branches)
        => _host.SendAsync("POST", "/api/v1/connected-repositories", user, new { connectionId = "c1", providerRepositoryId = "42", branches });

    /// <summary>
    /// Maps the real visibility method, so the owner rule that the service applies stays covered through HTTP.
    /// </summary>
    private void MapVisibilityRoute(WebApplication app)
    {
        var visibilityContext = _database.Open();
        var service = new RepositoryService(
            context: visibilityContext,
            gitPlatformService: Mock.Of<IGitPlatformService>(),
            userContext: new HttpUserContext(app.Services),
            gitHubAppService: null!,
            organizationService: Mock.Of<IOrganizationService>(),
            fullRegenerationCleaner: null!,
            generationLockService: null!,
            repositoryOptions: Options.Create(new RepositoryAnalyzerOptions()),
            credentialResolver: new GitCredentialResolver(
                visibilityContext, _protector, Microsoft.Extensions.Logging.Abstractions.NullLogger<GitCredentialResolver>.Instance),
            connectionAuthorization: null!,
            branchActionAuditor: null!);
        var method = typeof(RepositoryService).GetMethod(nameof(RepositoryService.UpdateVisibilityAsync))!;
        var handler = (Func<UpdateVisibilityRequest, Task<IResult>>)Delegate.CreateDelegate(
            typeof(Func<UpdateVisibilityRequest, Task<IResult>>), service, method);
        app.MapPost("/api/v1/repositories/visibility", handler).RequireAuthorization();
    }

    private sealed class HttpUserContext(IServiceProvider services) : IUserContext
    {
        private System.Security.Claims.ClaimsPrincipal? Principal
            => services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;

        public string? UserId => Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        public string? UserName => UserId;
        public string? Email => null;
        public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;
        public System.Security.Claims.ClaimsPrincipal? User => Principal;
    }
}
