using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Endpoints;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Tests.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Endpoints;

public sealed class GitConnectionEndpointsTests : IAsyncLifetime
{
    private const string CanaryPat = "ghp_CANARY_endpoint_0123456789abcdef";
    private const string SecondPat = "ghp_SECOND_endpoint_0123456789abcdef";
    private const string Creator = "creator";
    private const string Other = "other";
    private const string Admin = "admin";

    private readonly string _dbPath = Path.Combine(TestPaths.ScratchRoot, $"git-connection-endpoints-{Guid.NewGuid():N}.db");
    private readonly FakeProviderClient _github = new(GitProvider.GitHub);
    private readonly FakeProviderClient _gitlab = new(GitProvider.GitLab);
    private readonly List<string> _bodies = [];
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _github.IdentityByToken[CanaryPat] = new GitProviderIdentity("1001", "octocat");
        _github.IdentityByToken[SecondPat] = new GitProviderIdentity("1001", "octocat");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var services = builder.Services;
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, UserContext>();
        services.AddScoped<IContext>(_ => OpenContext());
        services.AddSingleton<IGitConnectionSecretProtector>(new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider()));
        services.AddScoped<IGitConnectionAuthorizationService, GitConnectionAuthorizationService>();
        services.AddSingleton(new GitLabServerUrlValidator(Options.Create(new GitProviderOptions()), new FakeHostResolver()));
        services.AddSingleton<IGitProviderClientResolver>(new GitProviderClientResolver([_github, _gitlab]));
        services.AddScoped<IGitConnectionService, GitConnectionService>();
        services.AddSingleton(new ProviderPaginationCursorCodec(new EphemeralDataProtectionProvider()));
        services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
        services.AddAuthorization();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapGitConnectionEndpoints();

        await using (var seed = OpenContext())
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Users.AddRange(
                new User { Id = Creator, Name = Creator, Email = "creator@example.com" },
                new User { Id = Other, Name = Other, Email = "other@example.com" },
                new User { Id = Admin, Name = Admin, Email = "admin@example.com" });
            var role = new Role { Id = "role-admin", Name = "Admin", Description = "Admin", IsActive = true };
            seed.Roles.Add(role);
            seed.UserRoles.Add(new UserRole { Id = "link-admin", UserId = Admin, RoleId = role.Id });
            await seed.SaveChangesAsync();
        }

        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqlitePools.Release(_dbPath);
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // ---- authentication ----

    [Theory]
    [InlineData("GET", "/api/v1/git-connections")]
    [InlineData("POST", "/api/v1/git-connections")]
    [InlineData("GET", "/api/v1/git-connections/c1")]
    [InlineData("PUT", "/api/v1/git-connections/c1")]
    [InlineData("DELETE", "/api/v1/git-connections/c1")]
    [InlineData("POST", "/api/v1/git-connections/c1/test")]
    [InlineData("POST", "/api/v1/git-connections/c1/enable")]
    [InlineData("POST", "/api/v1/git-connections/c1/disable")]
    [InlineData("GET", "/api/v1/git-connections/c1/repositories")]
    [InlineData("GET", "/api/v1/git-connections/c1/repositories/42/branches")]
    [InlineData("GET", "/api/v1/git-connections/c1/audit-events")]
    public async Task EveryRoute_WhenAnonymous_Returns401WithoutCallingTheProvider(string method, string path)
    {
        var response = await SendAsync(method, path, user: null, body: method is "POST" or "PUT" ? new { provider = "GitHub", token = CanaryPat } : null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Empty(_github.ValidateCalls);
        Assert.Empty(_github.CatalogCalls);
    }

    [Fact]
    public void LegacyBranchDiscoveryRoute_RequiresAuthentication()
    {
        var method = typeof(RepositoryService).GetMethod(nameof(RepositoryService.GetBranchesAsync))!;

        Assert.NotEmpty(method.GetCustomAttributes<AuthorizeAttribute>());
    }

    // ---- create and shared use ----

    [Fact]
    public async Task Create_ReturnsCreatedWithSafeEnvelope_AndSecondCallerGetsTheExistingConnection()
    {
        var first = await CreateAsync(Creator);
        var second = await SendAsync("POST", "/api/v1/git-connections", Other, new { provider = "GitHub", token = SecondPat });

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.True(first.Json.GetProperty("success").GetBoolean());
        var connection = first.Json.GetProperty("data");
        Assert.Equal("GitHub", connection.GetProperty("provider").GetString());
        Assert.Equal("octocat", connection.GetProperty("accountLogin").GetString());
        Assert.True(connection.GetProperty("hasSecret").GetBoolean());
        Assert.True(connection.GetProperty("canMaintain").GetBoolean());
        AssertNoSecretProperties(first.Body);

        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.True(second.Json.GetProperty("existing").GetBoolean());
        Assert.Equal(connection.GetProperty("id").GetString(), second.Json.GetProperty("data").GetProperty("id").GetString());
        Assert.False(second.Json.GetProperty("data").GetProperty("canMaintain").GetBoolean());
    }

    [Fact]
    public async Task Create_WhenProviderRejectsTheToken_Returns422WithStableCodeAndNoProviderText()
    {
        var response = await SendAsync("POST", "/api/v1/git-connections", Creator, new { provider = "GitHub", token = "ghp_wrong_token_value" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.Status);
        Assert.False(response.Json.GetProperty("success").GetBoolean());
        Assert.Equal("PROVIDER_UNAUTHORIZED", response.Json.GetProperty("errorCode").GetString());
        Assert.DoesNotContain("ghp_wrong_token_value", response.Body);
        Assert.False(string.IsNullOrWhiteSpace(response.Json.GetProperty("message").GetString()));
    }

    [Theory]
    [InlineData("""{"provider":"Gitee","token":"ghp_x"}""", "INVALID_PROVIDER")]
    [InlineData("""{"provider":"GitHub","token":""}""", "INVALID_TOKEN")]
    [InlineData("""{"provider":"GitLab","serverUrl":"http://gitlab.example.com","token":"glpat_x"}""", "SERVER_URL_INVALID")]
    public async Task Create_WhenRequestIsInvalid_Returns400WithStableCode(string json, string code)
    {
        var response = await SendRawAsync("POST", "/api/v1/git-connections", Creator, json);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(code, response.Json.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task List_ShowsSharedConnectionsWithCapabilitiesPerCaller()
    {
        await CreateAsync(Creator);

        var asOther = await SendAsync("GET", "/api/v1/git-connections", Other);
        var asAdmin = await SendAsync("GET", "/api/v1/git-connections", Admin);

        var otherItem = Assert.Single(asOther.Json.GetProperty("data").EnumerateArray());
        Assert.False(otherItem.GetProperty("canMaintain").GetBoolean());
        Assert.True(Assert.Single(asAdmin.Json.GetProperty("data").EnumerateArray()).GetProperty("canMaintain").GetBoolean());
        AssertNoSecretProperties(asOther.Body);
    }

    // ---- maintenance ----

    [Fact]
    public async Task Rename_IsAllowedForCreatorAndAdminAndForbiddenForOthers()
    {
        var id = await CreateIdAsync();

        var forbidden = await SendAsync("PUT", $"/api/v1/git-connections/{id}", Other, new { displayName = "Hijacked" });
        var byCreator = await SendAsync("PUT", $"/api/v1/git-connections/{id}", Creator, new { displayName = "Team GitHub" });
        var byAdmin = await SendAsync("PUT", $"/api/v1/git-connections/{id}", Admin, new { displayName = "Admin name" });

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.Equal("CONNECTION_MAINTENANCE_FORBIDDEN", forbidden.Json.GetProperty("errorCode").GetString());
        Assert.Equal("Team GitHub", byCreator.Json.GetProperty("data").GetProperty("displayName").GetString());
        Assert.Equal("Admin name", byAdmin.Json.GetProperty("data").GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Rotate_WithInvalidReplacement_Returns422AndKeepsTheActiveConnectionUsable()
    {
        var id = await CreateIdAsync();

        var rotate = await SendAsync("PUT", $"/api/v1/git-connections/{id}", Creator, new { token = "ghp_revoked_token" });
        var catalog = await SendAsync("GET", $"/api/v1/git-connections/{id}/repositories", Other);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, rotate.Status);
        Assert.Equal(HttpStatusCode.OK, catalog.Status);
        Assert.Equal(CanaryPat, _github.CatalogCalls.Last().Token);
    }

    [Fact]
    public async Task Test_ReturnsHealthForMaintainersOnly()
    {
        var id = await CreateIdAsync();

        var forbidden = await SendAsync("POST", $"/api/v1/git-connections/{id}/test", Other);
        var ok = await SendAsync("POST", $"/api/v1/git-connections/{id}/test", Creator);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        var health = ok.Json.GetProperty("data");
        Assert.True(health.GetProperty("ok").GetBoolean());
        Assert.Equal("Healthy", health.GetProperty("state").GetString());
        Assert.True(health.TryGetProperty("latencyMs", out _));
        Assert.True(health.TryGetProperty("checkedAt", out _));
    }

    [Fact]
    public async Task DisableAndEnable_AreMaintainerOnly_AndDisableBlocksDiscoveryWith409()
    {
        var id = await CreateIdAsync();

        var forbidden = await SendAsync("POST", $"/api/v1/git-connections/{id}/disable", Other);
        var disabled = await SendAsync("POST", $"/api/v1/git-connections/{id}/disable", Creator);
        var repositories = await SendAsync("GET", $"/api/v1/git-connections/{id}/repositories", Other);
        var branches = await SendAsync("GET", $"/api/v1/git-connections/{id}/repositories/42/branches", Other);
        var enabled = await SendAsync("POST", $"/api/v1/git-connections/{id}/enable", Admin);
        var afterEnable = await SendAsync("GET", $"/api/v1/git-connections/{id}/repositories", Other);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.Equal("Disabled", disabled.Json.GetProperty("data").GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, repositories.Status);
        Assert.Equal("CONNECTION_DISABLED", repositories.Json.GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.Conflict, branches.Status);
        Assert.Equal(HttpStatusCode.OK, enabled.Status);
        Assert.Equal(HttpStatusCode.OK, afterEnable.Status);
    }

    [Fact]
    public async Task Disable_KeepsExistingRepositoriesAndDocumentation()
    {
        var id = await CreateIdAsync();
        await using (var context = OpenContext())
        {
            context.Repositories.Add(new Repository
            {
                Id = "r1", OwnerUserId = Creator, GitUrl = "https://github.com/acme/r1", OrgName = "acme", RepoName = "r1", GitConnectionId = id
            });
            await context.SaveChangesAsync();
        }

        await SendAsync("POST", $"/api/v1/git-connections/{id}/disable", Creator);

        await using var verification = OpenContext();
        var repository = await verification.Repositories.SingleAsync();
        Assert.False(repository.IsDeleted);
        Assert.Equal(id, repository.GitConnectionId);
    }

    [Fact]
    public async Task Delete_WhenRepositoryDependsOnTheConnection_Returns409AndKeepsIt()
    {
        var id = await CreateIdAsync();
        await using (var context = OpenContext())
        {
            context.Repositories.Add(new Repository
            {
                Id = "r1", OwnerUserId = Creator, GitUrl = "https://github.com/acme/r1", OrgName = "acme", RepoName = "r1", GitConnectionId = id
            });
            await context.SaveChangesAsync();
        }

        var response = await SendAsync("DELETE", $"/api/v1/git-connections/{id}", Creator);

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("CONNECTION_IN_USE", response.Json.GetProperty("errorCode").GetString());
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("GET", $"/api/v1/git-connections/{id}", Other)).Status);
    }

    [Fact]
    public async Task Delete_ByMaintainerRemovesFromListAndOthersAreForbidden()
    {
        var id = await CreateIdAsync();

        var forbidden = await SendAsync("DELETE", $"/api/v1/git-connections/{id}", Other);
        var deleted = await SendAsync("DELETE", $"/api/v1/git-connections/{id}", Creator);
        var get = await SendAsync("GET", $"/api/v1/git-connections/{id}", Creator);
        var list = await SendAsync("GET", "/api/v1/git-connections", Creator);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.Equal(HttpStatusCode.OK, deleted.Status);
        Assert.Equal(HttpStatusCode.NotFound, get.Status);
        Assert.Equal("CONNECTION_NOT_FOUND", get.Json.GetProperty("errorCode").GetString());
        Assert.Empty(list.Json.GetProperty("data").EnumerateArray());
    }

    // ---- catalog ----

    [Fact]
    public async Task Repositories_AnyAuthenticatedUserPagesThroughTheSharedConnection()
    {
        var id = await CreateIdAsync();

        var response = await SendAsync("GET", $"/api/v1/git-connections/{id}/repositories?cursor=abc&pageSize=25", Other);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var data = response.Json.GetProperty("data");
        Assert.Equal("42", Assert.Single(data.GetProperty("items").EnumerateArray()).GetProperty("providerRepositoryId").GetString());
        Assert.Equal("next-cursor", data.GetProperty("nextCursor").GetString());
        var call = _github.CatalogCalls.Single();
        Assert.Equal("abc", call.Cursor);
        Assert.Equal(25, call.PageSize);
        AssertNoSecretProperties(response.Body);
    }

    [Fact]
    public async Task Repositories_WhenPageSizeIsMissing_UsesTheDefault()
    {
        var id = await CreateIdAsync();

        await SendAsync("GET", $"/api/v1/git-connections/{id}/repositories", Other);

        Assert.Equal(50, _github.CatalogCalls.Single().PageSize);
    }

    [Fact]
    public async Task Branches_UseTheStableProviderRepositoryId()
    {
        var id = await CreateIdAsync();

        var response = await SendAsync("GET", $"/api/v1/git-connections/{id}/repositories/42/branches?pageSize=10", Other);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("main", Assert.Single(response.Json.GetProperty("data").GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal("42", _github.CatalogCalls.Single().RepositoryId);
    }

    [Fact]
    public async Task Catalog_WhenProviderRateLimits_Returns429WithRetryAfterAndStableCode()
    {
        var id = await CreateIdAsync();
        _github.CatalogFailure = new GitProviderException(GitProviderErrorCodes.RateLimited, TimeSpan.FromSeconds(30));

        var response = await SendAsync("GET", $"/api/v1/git-connections/{id}/repositories", Other);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.Status);
        Assert.Equal("PROVIDER_RATE_LIMITED", response.Json.GetProperty("errorCode").GetString());
        Assert.Equal("30", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [Theory]
    [InlineData(GitProviderErrorCodes.InvalidCursor, HttpStatusCode.BadRequest)]
    [InlineData(GitProviderErrorCodes.InvalidRepositoryId, HttpStatusCode.BadRequest)]
    [InlineData(GitProviderErrorCodes.ServerUrlBlocked, HttpStatusCode.BadRequest)]
    [InlineData(GitProviderErrorCodes.Unauthorized, HttpStatusCode.UnprocessableEntity)]
    [InlineData(GitProviderErrorCodes.Forbidden, HttpStatusCode.UnprocessableEntity)]
    [InlineData(GitProviderErrorCodes.NotFound, HttpStatusCode.NotFound)]
    [InlineData(GitProviderErrorCodes.Timeout, HttpStatusCode.GatewayTimeout)]
    [InlineData(GitProviderErrorCodes.Unavailable, HttpStatusCode.BadGateway)]
    [InlineData(GitProviderErrorCodes.DnsFailure, HttpStatusCode.BadGateway)]
    [InlineData(GitProviderErrorCodes.TlsFailure, HttpStatusCode.BadGateway)]
    [InlineData(GitProviderErrorCodes.RedirectBlocked, HttpStatusCode.BadGateway)]
    [InlineData(GitProviderErrorCodes.InvalidResponse, HttpStatusCode.BadGateway)]
    public async Task Catalog_MapsProviderFailuresToStableStatusAndCode(string code, HttpStatusCode status)
    {
        var id = await CreateIdAsync();
        _github.CatalogFailure = new GitProviderException(code);

        var response = await SendAsync("GET", $"/api/v1/git-connections/{id}/repositories", Other);

        Assert.Equal(status, response.Status);
        Assert.Equal(code, response.Json.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(CanaryPat, response.Body);
    }

    [Theory]
    [InlineData(OpenDeepWiki.Models.GitConnections.GitConnectionErrorCodes.Conflict, HttpStatusCode.Conflict)]
    [InlineData(OpenDeepWiki.Models.GitConnections.GitConnectionErrorCodes.AccountMismatch, HttpStatusCode.Conflict)]
    [InlineData(OpenDeepWiki.Models.GitConnections.GitConnectionErrorCodes.SecretUnreadable, HttpStatusCode.Conflict)]
    public async Task ErrorMapping_CoversConnectionStateErrors(string code, HttpStatusCode status)
    {
        var result = GitConnectionEndpoints.ToErrorResult(new GitConnectionServiceException(code), new DefaultHttpContext());
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();

        await result.ExecuteAsync(context);

        Assert.Equal((int)status, context.Response.StatusCode);
    }

    [Fact]
    public async Task Catalog_WhenConnectionIsMissing_Returns404()
    {
        var response = await SendAsync("GET", "/api/v1/git-connections/missing/repositories", Other);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal("CONNECTION_NOT_FOUND", response.Json.GetProperty("errorCode").GetString());
    }

    // ---- audit ----

    [Fact]
    public async Task AuditEvents_AreReadableByMaintainersOnlyAndHoldNoSecrets()
    {
        var id = await CreateIdAsync();
        await SendAsync("POST", $"/api/v1/git-connections/{id}/disable", Creator);

        var forbidden = await SendAsync("GET", $"/api/v1/git-connections/{id}/audit-events", Other);
        var allowed = await SendAsync("GET", $"/api/v1/git-connections/{id}/audit-events?limit=5", Admin);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        var events = allowed.Json.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("eventType").GetString()).ToList();
        Assert.Equal(["Disabled", "Created"], events);
        AssertNoSecretProperties(allowed.Body);
    }

    // ---- no secret anywhere ----

    [Fact]
    public async Task NoResponseBodyEverContainsThePat()
    {
        var id = await CreateIdAsync();
        await SendAsync("GET", "/api/v1/git-connections", Other);
        await SendAsync("GET", $"/api/v1/git-connections/{id}", Other);
        await SendAsync("PUT", $"/api/v1/git-connections/{id}", Creator, new { token = SecondPat, displayName = "Rotated" });
        await SendAsync("POST", $"/api/v1/git-connections/{id}/test", Creator);
        await SendAsync("GET", $"/api/v1/git-connections/{id}/audit-events", Creator);

        Assert.All(_bodies, body => Assert.DoesNotContain(CanaryPat, body));
        Assert.All(_bodies, body => Assert.DoesNotContain(SecondPat, body));
        Assert.All(_bodies, body => Assert.DoesNotContain("protectedToken", body, StringComparison.OrdinalIgnoreCase));
    }

    // ---- helpers ----

    private async Task<Response> CreateAsync(string user)
        => await SendAsync("POST", "/api/v1/git-connections", user, new { provider = "GitHub", token = CanaryPat });

    private async Task<string> CreateIdAsync()
    {
        var response = await CreateAsync(Creator);
        Assert.Equal(HttpStatusCode.Created, response.Status);
        return response.Json.GetProperty("data").GetProperty("id").GetString()!;
    }

    private Task<Response> SendAsync(string method, string path, string? user, object? body = null)
        => SendRawAsync(method, path, user, body is null ? null : JsonSerializer.Serialize(body));

    private async Task<Response> SendRawAsync(string method, string path, string? user, string? json)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        _bodies.Add(text);
        JsonElement parsed = default;
        if (!string.IsNullOrWhiteSpace(text) && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            parsed = JsonDocument.Parse(text).RootElement.Clone();
        }

        return new Response(response.StatusCode, text, parsed, response.Headers);
    }

    private static void AssertNoSecretProperties(string body)
    {
        Assert.DoesNotContain(CanaryPat, body);
        Assert.DoesNotContain(SecondPat, body);
        using var document = JsonDocument.Parse(body);
        Assert.DoesNotContain(PropertyNames(document.RootElement), name =>
            name.Equals("token", StringComparison.OrdinalIgnoreCase)
            || name.Equals("protectedToken", StringComparison.OrdinalIgnoreCase)
            || name.Equals("secret", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var child in PropertyNames(property.Value))
                    {
                        yield return child;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var child in PropertyNames(item))
                    {
                        yield return child;
                    }
                }

                break;
        }
    }

    private HostContext OpenContext()
        => new(new DbContextOptionsBuilder<HostContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private sealed record Response(HttpStatusCode Status, string Body, JsonElement Json, System.Net.Http.Headers.HttpResponseHeaders Headers);

    private sealed class HostContext(DbContextOptions<HostContext> options) : MasterDbContext(options);

    /// <summary>
    /// Test authentication: the X-Test-User header carries the user ID. No header means anonymous.
    /// </summary>
    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user) || string.IsNullOrEmpty(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.ToString())], "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
