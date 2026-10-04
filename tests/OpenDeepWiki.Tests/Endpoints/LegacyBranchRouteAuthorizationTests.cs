using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.Models;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Endpoints;

/// <summary>
/// The legacy branch discovery route calls a provider with a server-side token, so an anonymous caller must never reach it.
/// The route is mapped from the real service method, so the test covers the attribute through the real pipeline.
/// </summary>
public sealed class LegacyBranchRouteAuthorizationTests
{
    private const string Route = "/api/v1/repositories/branches?gitUrl=https%3A%2F%2Fgithub.com%2Facme%2Fwidgets.git";

    [Fact]
    public async Task GetBranches_WhenAnonymous_Returns401AndNeverCallsThePlatform()
    {
        var platform = new Mock<IGitPlatformService>(MockBehavior.Strict);
        await using var host = await StartAsync(platform.Object);

        var response = await host.SendAsync("GET", Route, user: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        platform.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetBranches_WhenSignedIn_ReachesThePlatform()
    {
        var platform = new Mock<IGitPlatformService>();
        platform
            .Setup(service => service.GetBranchesAsync(It.IsAny<string>()))
            .ReturnsAsync(new GitBranchesResult([new GitBranchInfo("main", true)], "main", true));
        await using var host = await StartAsync(platform.Object);

        var response = await host.SendAsync("GET", Route, user: "user-1");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        platform.Verify(service => service.GetBranchesAsync(It.IsAny<string>()), Times.Once);
    }

    private static Task<TestEndpointHost> StartAsync(IGitPlatformService platform)
        => TestEndpointHost.StartAsync(
            _ => { },
            app =>
            {
                var service = CreateService(platform);
                var method = typeof(RepositoryService).GetMethod(nameof(RepositoryService.GetBranchesAsync))!;
                var handler = (Func<string, Task<GitBranchesResponse>>)Delegate.CreateDelegate(
                    typeof(Func<string, Task<GitBranchesResponse>>), service, method);
                app.MapGet("/api/v1/repositories/branches", handler);
            });

    private static RepositoryService CreateService(IGitPlatformService platform)
        => new(
            context: null!,
            gitPlatformService: platform,
            userContext: null!,
            gitHubAppService: null!,
            organizationService: null!,
            fullRegenerationCleaner: null!,
            generationLockService: null!,
            repositoryOptions: Options.Create(new RepositoryAnalyzerOptions()),
            credentialResolver: null!,
            connectionAuthorization: null!,
            branchActionAuditor: null!);
}
