using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Endpoints;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.GitHub;
using OpenDeepWiki.Services.Organizations;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Endpoints;

/// <summary>
/// The legacy submit route keeps its shape. New credential fields get a stable validation error through the
/// real HTTP pipeline, and the error names the replacement field.
/// </summary>
public sealed class LegacySubmitRouteTests : IAsyncLifetime
{
    private const string Pat = "ghp_CANARY_submit_route_0123456789";

    private readonly SqliteScratchDatabase _database;
    private TestEndpointHost _host = null!;

    public LegacySubmitRouteTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
    }

    public async Task InitializeAsync()
    {
        _host = await TestEndpointHost.StartAsync(
            services =>
            {
                services.AddScoped<IUserContext, UserContext>();
                services.AddScoped<IContext>(_ => _database.Open());
            },
            app =>
            {
                app.UseRepositoryConnectionRequestErrors();
                var context = _database.Open();
                var userContext = new HttpUserContext(app.Services);
                var protector = new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider());
                var service = new RepositoryService(
                    context,
                    Mock.Of<IGitPlatformService>(),
                    userContext,
                    Mock.Of<IGitHubAppService>(),
                    Mock.Of<IOrganizationService>(),
                    new RepositoryFullRegenerationCleaner(),
                    new RepositoryGenerationLockService(context),
                    Options.Create(new RepositoryAnalyzerOptions()),
                    new GitCredentialResolver(context, protector, NullLogger<GitCredentialResolver>.Instance),
                    new GitConnectionAuthorizationService(userContext, context),
                    new BranchActionAuditor(context, NullLogger<BranchActionAuditor>.Instance));
                app.MapPost("/api/v1/repositories/submit", (Func<RepositorySubmitRequest, Task<Repository>>)service.SubmitAsync)
                    .RequireAuthorization();
            });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task Submit_WithANewCredentialField_Returns400WithAStableCodeAndNeverEchoesTheSecret()
    {
        var response = await Submit(new { gitUrl = "https://github.com/acme/widgets.git", orgName = "acme", repoName = "widgets",
            branchName = "main", languageCode = "zh", isPublic = false, authAccount = "octocat", authPassword = Pat });

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.False(response.Json.GetProperty("success").GetBoolean());
        Assert.Equal(RepositoryConnectionErrorCodes.LegacyCredentialFieldsRejected, response.Json.GetProperty("errorCode").GetString());
        Assert.Contains("gitConnectionId", response.Json.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Pat, response.Body);
    }

    [Fact]
    public async Task Submit_WithACredentialInTheUrl_Returns400WithoutEchoingTheUrl()
    {
        var response = await Submit(new { gitUrl = $"https://octocat:{Pat}@github.com/acme/widgets.git", orgName = "acme",
            repoName = "widgets", branchName = "main", languageCode = "zh", isPublic = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(RepositoryConnectionErrorCodes.GitUrlContainsCredentials, response.Json.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(Pat, response.Body);
    }

    [Fact]
    public async Task Submit_WhenAnonymous_Returns401()
    {
        var response = await _host.SendAsync("POST", "/api/v1/repositories/submit", user: null, body: new { gitUrl = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
    }

    private Task<TestEndpointHost.TestResponse> Submit(object body)
        => _host.SendAsync("POST", "/api/v1/repositories/submit", "user-1", body);

    private sealed class HttpUserContext(IServiceProvider services) : IUserContext
    {
        private System.Security.Claims.ClaimsPrincipal? Principal
            => services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>().HttpContext?.User;

        public string? UserId => Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        public string? UserName => UserId;
        public string? Email => null;
        public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
        public System.Security.Claims.ClaimsPrincipal? User => Principal;
    }
}
