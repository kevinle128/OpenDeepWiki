using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Endpoints;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Tests.Endpoints;
using OpenDeepWiki.Tests.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Integration;

/// <summary>
/// One user journey through the real HTTP pipeline on a real SQLite file: create a shared connection, page the
/// catalog, connect a repository with several branches, add and remove branches, then disable the connection.
/// Only the provider is a fake. Unit-level rules stay in the endpoint and service test classes.
/// </summary>
public sealed class GitConnectionWorkflowTests : IAsyncLifetime
{
    private const string Pat = "ghp_CANARY_workflow_0123456789abcdef";
    private const string Creator = "user-1";
    private const string Member = "user-2";
    private const string AdminUser = "admin-1";

    private readonly SqliteScratchDatabase _database;
    private readonly WorkflowProvider _provider = new();
    private readonly List<string> _bodies = [];
    private readonly string _root = Path.Combine(TestPaths.ScratchRoot, $"workflow-{Guid.NewGuid():N}");
    private TestEndpointHost _host = null!;

    public GitConnectionWorkflowTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
        _provider.IdentityByToken[Pat] = new GitProviderIdentity("1001", "octocat");
        _provider.AddRepository("41", "acme/alpha", "Public", "main", "main");
        _provider.AddRepository("42", "acme/widgets", "Public", "main", "main", "dev", "release");
        _provider.AddRepository("43", "acme/gamma", "Public", "main", "main");
    }

    public async Task InitializeAsync()
    {
        await using (var context = _database.Open())
        {
            context.Users.Add(new User { Id = AdminUser, Name = AdminUser, Email = "admin-1@example.com" });
            var role = new Role { Id = "role-admin", Name = "Admin", Description = "Admin", IsActive = true };
            context.Roles.Add(role);
            context.UserRoles.Add(new UserRole { Id = "link-admin", UserId = AdminUser, RoleId = role.Id });
            await context.SaveChangesAsync();
        }

        _host = await TestEndpointHost.StartAsync(
            services =>
            {
                services.AddScoped<IUserContext, UserContext>();
                services.AddScoped<IContext>(_ => _database.Open());
                services.AddSingleton<IGitConnectionSecretProtector>(
                    new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider()));
                services.AddScoped<IGitConnectionAuthorizationService, GitConnectionAuthorizationService>();
                services.AddSingleton(new GitLabServerUrlValidator(Options.Create(new GitProviderOptions()), new FakeHostResolver()));
                services.AddSingleton<IGitProviderClientResolver>(new GitProviderClientResolver([_provider]));
                services.AddScoped<IGitConnectionService, GitConnectionService>();
                services.AddSingleton(new ProviderPaginationCursorCodec(new EphemeralDataProtectionProvider()));
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
                app.MapGitConnectionEndpoints();
                app.MapConnectedRepositoryEndpoints();
                app.MapBranchGenerationEndpoints();
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

    [Fact]
    public async Task SharedConnection_ToCatalogToIndependentBranchJobs_ToDisabledState()
    {
        // 1. One user creates the connection; a second user submitting the same account gets the same row.
        var created = await SendAsync("POST", "/api/v1/git-connections", Creator, new { provider = "GitHub", token = Pat });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        var connectionId = created.Json.GetProperty("data").GetProperty("id").GetString()!;
        Assert.True(created.Json.GetProperty("data").GetProperty("canMaintain").GetBoolean());
        var shared = await SendAsync("POST", "/api/v1/git-connections", Member, new { provider = "GitHub", token = Pat });
        Assert.Equal(HttpStatusCode.OK, shared.Status);
        Assert.True(shared.Json.GetProperty("existing").GetBoolean());
        Assert.False(shared.Json.GetProperty("data").GetProperty("canMaintain").GetBoolean());

        // 2. The member pages the catalog with the opaque cursor and lists branches by the stable provider ID.
        var page1 = await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/repositories?pageSize=2", Member);
        var cursor = page1.Json.GetProperty("data").GetProperty("nextCursor").GetString();
        Assert.Equal(2, page1.Json.GetProperty("data").GetProperty("items").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(cursor));
        var page2 = await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/repositories?pageSize=2&cursor={Uri.EscapeDataString(cursor!)}", Member);
        Assert.Equal(1, page2.Json.GetProperty("data").GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKindNull, page2.Json.GetProperty("data").GetProperty("nextCursor").ValueKind);
        var branches = await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/repositories/42/branches?pageSize=10", Member);
        Assert.Equal(["main", "dev", "release"],
            branches.Json.GetProperty("data").GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()!));

        // 3. The member connects the repository with two branches: one independent job per branch.
        var connected = await SendAsync("POST", "/api/v1/connected-repositories", Member,
            new { connectionId, providerRepositoryId = "42", branches = new[] { "main", "dev" } });
        Assert.Equal(HttpStatusCode.Created, connected.Status);
        var data = connected.Json.GetProperty("data");
        var repositoryId = data.GetProperty("repositoryId").GetString()!;
        var firstTwo = data.GetProperty("branches").EnumerateArray().ToDictionary(
            item => item.GetProperty("branchName").GetString()!,
            item => (BranchId: item.GetProperty("branchId").GetString()!, TaskId: item.GetProperty("taskId").GetString()!));
        Assert.Equal(2, firstTwo.Count);
        Assert.NotEqual(firstTwo["main"].TaskId, firstTwo["dev"].TaskId);

        // 4. The creator, who does not own the repository, adds a third branch. The first two jobs stay untouched.
        var added = await SendAsync("POST", $"/api/v1/repositories/{repositoryId}/indexed-branches", Creator,
            new { branches = new[] { "release" } });
        Assert.Equal(HttpStatusCode.Created, added.Status);
        var release = added.Json.GetProperty("data").GetProperty("branches")[0];
        Assert.True(release.GetProperty("created").GetBoolean());
        await using (var context = _database.Open())
        {
            var tasks = await context.BranchGenerationTasks.AsNoTracking().ToListAsync();
            Assert.Equal(3, tasks.Count);
            Assert.Equal(3, tasks.Select(item => item.BranchId).Distinct().Count());
            Assert.All(tasks, item => Assert.Equal(BranchGenerationTaskStatus.Pending, item.Status));
        }

        // 5. A processing job blocks removal of its own branch with 409; the other branches are not affected.
        await using (var context = _database.Open())
        {
            var mainTask = await context.BranchGenerationTasks.SingleAsync(item => item.Id == firstTwo["main"].TaskId);
            mainTask.Status = BranchGenerationTaskStatus.Processing;
            await context.SaveChangesAsync();
        }

        var blocked = await SendAsync("DELETE", $"/api/v1/repositories/{repositoryId}/indexed-branches/{firstTwo["main"].BranchId}", Member);
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Equal("BRANCH_JOB_ACTIVE", blocked.Json.GetProperty("errorCode").GetString());
        var cancelled = await SendAsync("POST", $"/api/v1/branch-generation-tasks/{release.GetProperty("taskId").GetString()}/cancel", Member);
        Assert.Equal(HttpStatusCode.OK, cancelled.Status);
        var removed = await SendAsync("DELETE", $"/api/v1/repositories/{repositoryId}/indexed-branches/{release.GetProperty("branchId").GetString()}", Member);
        Assert.Equal(HttpStatusCode.OK, removed.Status);
        await using (var context = _database.Open())
        {
            Assert.Equal(["dev", "main"], (await context.RepositoryBranches.Select(item => item.BranchName).ToListAsync()).Order());
            var statuses = await context.BranchGenerationTasks.AsNoTracking().Where(item => item.Id == firstTwo["main"].TaskId || item.Id == firstTwo["dev"].TaskId)
                .ToDictionaryAsync(item => item.Id, item => item.Status);
            Assert.Equal(BranchGenerationTaskStatus.Processing, statuses[firstTwo["main"].TaskId]);
            Assert.Equal(BranchGenerationTaskStatus.Pending, statuses[firstTwo["dev"].TaskId]);
        }

        // 6. Connection maintenance stays with the creator and an Admin.
        var forbidden = await SendAsync("POST", $"/api/v1/git-connections/{connectionId}/disable", Member);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.Equal("CONNECTION_MAINTENANCE_FORBIDDEN", forbidden.Json.GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/audit-events", Member)).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/audit-events", Creator)).Status);

        // 7. Disabled: discovery and new work return a clear state; the indexed branches stay readable.
        var disabled = await SendAsync("POST", $"/api/v1/git-connections/{connectionId}/disable", Creator);
        Assert.Equal("Disabled", disabled.Json.GetProperty("data").GetProperty("state").GetString());
        var discovery = await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/repositories", Member);
        var newRepository = await SendAsync("POST", "/api/v1/connected-repositories", Member,
            new { connectionId, providerRepositoryId = "41", branches = new[] { "main" } });
        var newBranch = await SendAsync("POST", $"/api/v1/repositories/{repositoryId}/indexed-branches", Member, new { branches = new[] { "release" } });
        Assert.All([discovery, newRepository, newBranch], response =>
        {
            Assert.Equal(HttpStatusCode.Conflict, response.Status);
            Assert.Equal("CONNECTION_DISABLED", response.Json.GetProperty("errorCode").GetString());
        });
        var stillReadable = await SendAsync("GET", $"/api/v1/repositories/{repositoryId}/indexed-branches", Member);
        Assert.Equal(HttpStatusCode.OK, stillReadable.Status);
        Assert.Equal(2, stillReadable.Json.GetProperty("data").GetArrayLength());
        await using (var context = _database.Open())
        {
            Assert.False((await context.Repositories.SingleAsync()).IsDeleted);
        }

        // 8. An Admin who did not create the connection enables it again.
        var enabled = await SendAsync("POST", $"/api/v1/git-connections/{connectionId}/enable", AdminUser, role: "Admin");
        Assert.Equal(HttpStatusCode.OK, enabled.Status);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/repositories", Member)).Status);

        // 9. The raw token never appears in any response and the stored value is not the raw token.
        Assert.All(_bodies, body => Assert.DoesNotContain(Pat, body));
        await using var verification = _database.Open();
        Assert.NotEqual(Pat, (await verification.GitConnections.SingleAsync()).ProtectedToken);
        Assert.DoesNotContain(Pat, string.Join('|', await verification.GitConnectionAuditEvents.Select(item => item.EventType.ToString()).ToListAsync()));
    }

    [Fact]
    public async Task Anonymous_CannotUseAnyRouteOfTheWorkflow()
    {
        var created = await SendAsync("POST", "/api/v1/git-connections", Creator, new { provider = "GitHub", token = Pat });
        var connectionId = created.Json.GetProperty("data").GetProperty("id").GetString()!;
        var callsBefore = _provider.Calls;

        var responses = new[]
        {
            await SendAsync("GET", "/api/v1/git-connections", user: null),
            await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/repositories", user: null),
            await SendAsync("POST", "/api/v1/connected-repositories", user: null, new { connectionId, providerRepositoryId = "42", branches = new[] { "main" } }),
            await SendAsync("GET", "/api/v1/repositories/any/indexed-branches", user: null)
        };

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.Status));
        Assert.Equal(callsBefore, _provider.Calls);
    }

    [Fact]
    public async Task ProviderFailure_ReturnsAStableErrorAndAnEarlierSuccessStaysUsable()
    {
        var created = await SendAsync("POST", "/api/v1/git-connections", Creator, new { provider = "GitHub", token = Pat });
        var connectionId = created.Json.GetProperty("data").GetProperty("id").GetString()!;

        _provider.Failure = new GitProviderException(GitProviderErrorCodes.RateLimited, TimeSpan.FromSeconds(30));
        var limited = await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/repositories", Member);
        _provider.Failure = null;
        var recovered = await SendAsync("GET", $"/api/v1/git-connections/{connectionId}/repositories", Member);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("PROVIDER_RATE_LIMITED", limited.Json.GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.OK, recovered.Status);
        Assert.All(_bodies, body => Assert.DoesNotContain(Pat, body));
    }

    // ---- helpers ----

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    private async Task<TestEndpointHost.TestResponse> SendAsync(string method, string path, string? user, object? body = null, string? role = null)
    {
        var response = await _host.SendAsync(method, path, user, body, role);
        _bodies.Add(response.Body);
        return response;
    }

    /// <summary>
    /// Provider double that holds a small catalog. Pages hold at most the requested number of items and the cursor is
    /// the offset of the next page.
    /// </summary>
    private sealed class WorkflowProvider : IGitProviderClient
    {
        private readonly List<(RemoteRepository Remote, List<string> Branches)> _repositories = [];

        public GitProvider Provider => GitProvider.GitHub;

        public Dictionary<string, GitProviderIdentity> IdentityByToken { get; } = new();

        public Exception? Failure { get; set; }

        public int Calls { get; private set; }

        public void AddRepository(string id, string fullName, string visibility, string defaultBranch, params string[] branches)
        {
            var name = fullName[(fullName.LastIndexOf('/') + 1)..];
            _repositories.Add((
                new RemoteRepository(id, name, fullName, fullName[..fullName.LastIndexOf('/')], "A repository",
                    $"https://example.invalid/{fullName}.git", $"https://example.invalid/{fullName}", defaultBranch, visibility, null),
                branches.ToList()));
        }

        public Task<GitProviderIdentity> ValidateAsync(GitProviderTarget target, CancellationToken cancellationToken)
        {
            Calls++;
            ThrowIfFailing();
            return IdentityByToken.TryGetValue(target.Token, out var identity)
                ? Task.FromResult(identity)
                : throw new GitProviderException(GitProviderErrorCodes.Unauthorized);
        }

        public Task<RemoteRepository> GetRepositoryAsync(GitProviderTarget target, string providerRepositoryId, CancellationToken cancellationToken)
        {
            Calls++;
            ThrowIfFailing();
            return Task.FromResult(Find(providerRepositoryId).Remote);
        }

        public Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
            GitProviderTarget target, string? cursor, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            ThrowIfFailing();
            var start = cursor is null ? 0 : int.Parse(cursor);
            var items = _repositories.Skip(start).Take(pageSize).Select(item => item.Remote).ToList();
            var next = start + pageSize < _repositories.Count ? (start + pageSize).ToString() : null;
            return Task.FromResult(new ProviderPage<RemoteRepository>(items, next));
        }

        public Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
            GitProviderTarget target, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            ThrowIfFailing();
            var entry = Find(providerRepositoryId);
            var start = cursor is null ? 0 : int.Parse(cursor);
            var items = entry.Branches.Skip(start).Take(pageSize)
                .Select(name => new RemoteBranch(name, name == entry.Remote.DefaultBranch, null)).ToList();
            var next = start + pageSize < entry.Branches.Count ? (start + pageSize).ToString() : null;
            return Task.FromResult(new ProviderPage<RemoteBranch>(items, next));
        }

        private (RemoteRepository Remote, List<string> Branches) Find(string id)
            => _repositories.FirstOrDefault(item => item.Remote.ProviderRepositoryId == id) is { Remote: not null } entry
                ? entry
                : throw new GitProviderException(GitProviderErrorCodes.NotFound);

        private void ThrowIfFailing()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }
}
