using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

public class GitLabPatProviderClientTests
{
    private const string CanaryPat = "glpat-CANARY-provider-0123456789";
    private const string PublicIp = "93.184.216.34";
    private const string SaaS = "https://gitlab.com";
    private const string Corp = "https://gitlab.corp.example";

    // ---- identity ----

    [Fact]
    public async Task Validate_OnGitLabCom_CallsUserEndpointWithPrivateTokenHeaderOnly()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("""{"id":1234,"username":"tanuki"}""");

        var identity = await client.ValidateAsync(Target(SaaS, null), CancellationToken.None);

        Assert.Equal("1234", identity.ExternalAccountId);
        Assert.Equal("tanuki", identity.AccountName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{SaaS}/api/v4/user", request.Uri.AbsoluteUri);
        Assert.Equal(CanaryPat, request.PrivateToken);
        Assert.Null(request.Authorization);
        Assert.DoesNotContain(CanaryPat, request.Uri.ToString());
        Assert.Equal(IPAddress.Parse(PublicIp), Assert.Single(request.PinnedAddresses!));
    }

    [Fact]
    public async Task Validate_OnPublicSelfHostedHttpsHost_Works()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("""{"id":9,"username":"corp-user"}""");

        var identity = await client.ValidateAsync(Target(Corp, null), CancellationToken.None);

        Assert.Equal("9", identity.ExternalAccountId);
        Assert.Equal($"{Corp}/api/v4/user", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Validate_WhenSelfHostedPortIsCustom_KeepsThePortInTheRequest()
    {
        var (client, handler, _) = Create(resolver: new FakeHostResolver().Add("gitlab.corp.example", PublicIp));
        handler.EnqueueJson("""{"id":9,"username":"corp-user"}""");

        await client.ValidateAsync(Target("https://gitlab.corp.example:8443", null), CancellationToken.None);

        Assert.Equal("https://gitlab.corp.example:8443/api/v4/user", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Validate_WhenHostResolvesPrivateWithoutAllowlist_IsBlockedBeforeAnyRequest()
    {
        var (client, handler, _) = Create(resolver: new FakeHostResolver().Add("gitlab.corp.example", "10.1.2.3"));
        handler.EnqueueJson("""{"id":9,"username":"x"}""");

        await AssertCode(GitProviderErrorCodes.ServerUrlBlocked,
            () => client.ValidateAsync(Target(Corp, null), CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Validate_WhenPrivateHostIsAllowlisted_Works()
    {
        var (client, handler, _) = Create(
            resolver: new FakeHostResolver().Add("gitlab.corp.example", "10.1.2.3"),
            allowedHosts: ["gitlab.corp.example"]);
        handler.EnqueueJson("""{"id":9,"username":"x"}""");

        await client.ValidateAsync(Target(Corp, null), CancellationToken.None);

        Assert.Equal(IPAddress.Parse("10.1.2.3"), Assert.Single(handler.Requests).PinnedAddresses!.Single());
    }

    [Fact]
    public async Task Validate_WhenAllowedOriginRedirectsToABlockedAddress_FailsAndNeverSendsTheTokenAgain()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://169.254.169.254/latest/meta-data");
            return response;
        });
        handler.EnqueueJson("""{"id":1,"username":"metadata"}""");

        await AssertCode(GitProviderErrorCodes.RedirectBlocked,
            () => client.ValidateAsync(Target(Corp, null), CancellationToken.None));

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"{Corp}/api/v4/user", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Validate_WhenGuardRejectsMidFlight_PropagatesBlockedCodeFromTheConnectCallback()
    {
        var (client, handler, _) = Create();
        handler.EnqueueThrow(new HttpRequestException("x", new GitProviderException(GitProviderErrorCodes.ServerUrlBlocked)));

        await AssertCode(GitProviderErrorCodes.ServerUrlBlocked,
            () => client.ValidateAsync(Target(Corp, null), CancellationToken.None));
    }

    [Fact]
    public async Task GetRepository_UsesNumericProjectId()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(ProjectJson(77, "group/sub/project"));

        var repository = await client.GetRepositoryAsync(Target(SaaS), "77", CancellationToken.None);

        Assert.Equal("77", repository.ProviderRepositoryId);
        Assert.Equal("group/sub/project", repository.FullName);
        Assert.Equal("project", repository.Name);
        Assert.Equal("group/sub", repository.Namespace);
        Assert.Equal("https://gitlab.com/group/sub/project.git", repository.CloneUrl);
        Assert.Equal("Private", repository.Visibility);
        Assert.Equal($"{SaaS}/api/v4/projects/77", Assert.Single(handler.Requests).Uri.AbsoluteUri);
    }

    // ---- paging ----

    [Theory]
    [InlineData("3")]
    [InlineData("3q")]
    [InlineData("🧠a")]
    public async Task Catalog_ShortSearch_FiltersOnePageWithoutUsingGitLabExactSearch(string search)
    {
        var (client, handler, _) = Create();
        var matching = ProjectJson(196, "vtc/3q-chess/3q-game-client")
            .Replace("\"name\":\"3q-game-client\"", "\"name\":\"🧠api\"");
        handler.EnqueueJson($"[{matching},{ProjectJson(197, "acme/other")}]",
            link: $"<{SaaS}/api/v4/projects?page=2>; rel=\"next\"");

        var result = await client.ListCatalogAsync(Target(SaaS), null, 50, search, "private", "updated", CancellationToken.None);

        Assert.Equal("196", Assert.Single(result.Items).ProviderRepositoryId);
        Assert.NotNull(result.NextCursor);
        Assert.Null(result.TotalCount);
        var query = QueryHelpers.ParseQuery(Assert.Single(handler.Requests).Uri.Query);
        Assert.False(query.ContainsKey("search"));
        Assert.False(query.ContainsKey("search_namespaces"));
        Assert.Equal("private", query["visibility"].ToString());
        Assert.Equal("last_activity_at", query["order_by"].ToString());
        Assert.Equal("desc", query["sort"].ToString());
    }

    [Fact]
    public async Task Catalog_ShortSearch_LoadMoreSkipsEmptyPagesAndKeepsMatchingRows()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson($"[{ProjectJson(1, "vtc/3q/first")}]", link: $"<{SaaS}/api/v4/projects?page=2>; rel=\"next\"");
        handler.EnqueueJson($"[{ProjectJson(2, "acme/other")}]", link: $"<{SaaS}/api/v4/projects?page=3>; rel=\"next\"");
        handler.EnqueueJson($"[{ProjectJson(3, "vtc/3q/last")}]", link: $"<{SaaS}/api/v4/projects?page=4>; rel=\"next\"");
        handler.EnqueueJson($"[{ProjectJson(4, "acme/none")}]");

        var first = await client.ListCatalogAsync(Target(SaaS), null, 50, "3q", "all", "name", CancellationToken.None);
        Assert.Equal("1", Assert.Single(first.Items).ProviderRepositoryId);
        Assert.Single(handler.Requests);

        var second = await client.ListCatalogAsync(Target(SaaS), first.NextCursor, 50, "3q", "all", "name", CancellationToken.None);
        Assert.Equal("3", Assert.Single(second.Items).ProviderRepositoryId);
        Assert.NotNull(second.NextCursor);
        Assert.Equal(3, handler.Requests.Count);

        var last = await client.ListCatalogAsync(Target(SaaS), second.NextCursor, 50, "3q", "all", "name", CancellationToken.None);
        Assert.Empty(last.Items);
        Assert.Null(last.NextCursor);
        Assert.Null(last.TotalCount);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(["", "2", "3", "4"], handler.Requests.Select(r =>
            QueryHelpers.ParseQuery(r.Uri.Query).TryGetValue("page", out var value) ? value.ToString() : ""));
        Assert.All(handler.Requests, r => Assert.Equal("name", QueryHelpers.ParseQuery(r.Uri.Query)["order_by"].ToString()));
    }

    [Fact]
    public async Task Catalog_ShortSearch_RejectsRepeatedEmptyPage()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(ProjectPage(1, 1), link: $"<{SaaS}/api/v4/projects?page=1>; rel=\"next\"");

        await AssertCode(GitProviderErrorCodes.InvalidResponse, () => client.ListCatalogAsync(
            Target(SaaS), null, 50, "3q", "all", "updated", CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("updated", "all", "last_activity_at", "desc")]
    [InlineData("updatedAsc", "public", "last_activity_at", "asc")]
    [InlineData("name", "private", "name", "asc")]
    public async Task Catalog_QueriesOnlyTheRequestedPage_AndKeepsFiltersOnLoadMore(
        string sort, string visibility, string orderBy, string direction)
    {
        var (client, handler, _) = Create();
        handler.Enqueue(_ =>
        {
            var response = FakeProviderHandler.Json(ProjectPage(1, 50),
                link: $"<{SaaS}/api/v4/projects?page=2&per_page=100&order_by=id&visibility=public>; rel=\"next\"");
            response.Headers.TryAddWithoutValidation("X-Total", "51");
            return response;
        });
        handler.EnqueueJson(ProjectPage(51, 1));

        var first = await client.ListCatalogAsync(Target(SaaS), null, 50, "group/api & service", visibility, sort, CancellationToken.None);

        Assert.Equal(50, first.Items.Count);
        Assert.Equal(51, first.TotalCount);
        Assert.NotNull(first.NextCursor);
        Assert.Single(handler.Requests);

        var second = await client.ListCatalogAsync(Target(SaaS), first.NextCursor, 50, "group/api & service", visibility, sort, CancellationToken.None);

        Assert.Equal("51", Assert.Single(second.Items).ProviderRepositoryId);
        Assert.Null(second.NextCursor);
        Assert.Equal(2, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            var query = QueryHelpers.ParseQuery(request.Uri.Query);
            Assert.Equal("true", query["membership"].ToString());
            Assert.Equal("true", query["simple"].ToString());
            Assert.Equal("50", query["per_page"].ToString());
            Assert.Equal("group/api & service", query["search"].ToString());
            Assert.Equal("true", query["search_namespaces"].ToString());
            Assert.Equal(orderBy, query["order_by"].ToString());
            Assert.Equal(direction, query["sort"].ToString());
            if (visibility == "all")
                Assert.False(query.ContainsKey("visibility"));
            else
                Assert.Equal(visibility, query["visibility"].ToString());
        }
        Assert.Equal("2", QueryHelpers.ParseQuery(handler.Requests[1].Uri.Query)["page"].ToString());
    }

    [Theory]
    [InlineData("other", "all", "updated", 50, "connection-1")]
    [InlineData("", "private", "updated", 50, "connection-1")]
    [InlineData("", "all", "name", 50, "connection-1")]
    [InlineData("", "all", "updated", 25, "connection-1")]
    [InlineData("", "all", "updated", 50, "connection-2")]
    public async Task Catalog_RejectsCursorForAnotherQueryOrConnection_BeforeSendingARequest(
        string search, string visibility, string sort, int pageSize, string connectionId)
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(ProjectPage(1, 50), link: $"<{SaaS}/api/v4/projects?page=2>; rel=\"next\"");
        var first = await client.ListCatalogAsync(Target(SaaS), null, 50, "", "all", "updated", CancellationToken.None);

        await AssertCode(GitProviderErrorCodes.InvalidCursor, () => client.ListCatalogAsync(
            Target(SaaS, connectionId), first.NextCursor, pageSize, search, visibility, sort, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListRepositories_WhenKeysetIsSupported_FollowsValidatedNextLinks()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(ProjectPage(1, 100),
            link: $"<{SaaS}/api/v4/projects?id_after=100&membership=true&order_by=id&pagination=keyset&per_page=100&sort=asc&evil=1>; rel=\"next\"");
        handler.EnqueueJson(ProjectPage(101, 100),
            link: $"<{SaaS}/api/v4/projects?id_after=200&membership=true&order_by=id&pagination=keyset&per_page=100&sort=asc>; rel=\"next\"");
        handler.EnqueueJson(ProjectPage(201, 20));

        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await client.ListRepositoriesAsync(Target(SaaS), cursor, 100, CancellationToken.None);
            ids.AddRange(page.Items.Select(item => item.ProviderRepositoryId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(220, ids.Distinct().Count());
        Assert.Equal(3, handler.Requests.Count);
        var first = handler.Requests[0].Uri.Query;
        Assert.Contains("pagination=keyset", first);
        Assert.Contains("membership=true", first);
        Assert.Contains("per_page=100", first);
        Assert.DoesNotContain("id_after", first);
        Assert.Contains("id_after=100", handler.Requests[1].Uri.Query);
        Assert.Contains("id_after=200", handler.Requests[2].Uri.Query);
        Assert.All(handler.Requests, request => Assert.DoesNotContain("evil", request.Uri.Query));
        Assert.All(handler.Requests, request => Assert.Equal("/api/v4/projects", request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task ListRepositories_WhenKeysetIsRejected_FallsBackToOffsetPagingWithoutLosingItems()
    {
        var (client, handler, _) = Create();
        handler.EnqueueStatus(HttpStatusCode.BadRequest);
        handler.EnqueueJson(ProjectPage(1, 100), link: $"<{SaaS}/api/v4/projects?membership=true&page=2&per_page=100>; rel=\"next\"");
        handler.EnqueueJson(ProjectPage(101, 30));

        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await client.ListRepositoriesAsync(Target(SaaS), cursor, 100, CancellationToken.None);
            ids.AddRange(page.Items.Select(item => item.ProviderRepositoryId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        Assert.Equal(130, ids.Distinct().Count());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("pagination=keyset", handler.Requests[0].Uri.Query);
        Assert.DoesNotContain("pagination", handler.Requests[1].Uri.Query);
        Assert.DoesNotContain("pagination", handler.Requests[2].Uri.Query);
        Assert.Contains("page=2", handler.Requests[2].Uri.Query);
    }

    [Fact]
    public async Task ListRepositories_WhenServerIgnoresKeysetAndSendsOffsetLink_ContinuesWithOffsetPaging()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(ProjectPage(1, 10), link: $"<{SaaS}/api/v4/projects?membership=true&order_by=id&page=2&per_page=10&sort=asc>; rel=\"next\"");
        handler.EnqueueJson(ProjectPage(11, 5));

        var first = await client.ListRepositoriesAsync(Target(SaaS), null, 10, CancellationToken.None);
        var second = await client.ListRepositoriesAsync(Target(SaaS), first.NextCursor, 10, CancellationToken.None);

        Assert.Equal(15, first.Items.Concat(second.Items).Select(item => item.ProviderRepositoryId).Distinct().Count());
        Assert.DoesNotContain("pagination", handler.Requests[1].Uri.Query);
        Assert.Contains("page=2", handler.Requests[1].Uri.Query);
    }

    [Fact]
    public async Task ListRepositories_WhenOnlyNextPageHeaderIsPresent_UsesItAndReportsTotal()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(_ =>
        {
            var response = FakeProviderHandler.Json(ProjectPage(1, 2));
            response.Headers.TryAddWithoutValidation("X-Next-Page", "2");
            response.Headers.TryAddWithoutValidation("X-Total", "3");
            return response;
        });
        handler.EnqueueJson(ProjectPage(3, 1));

        var first = await client.ListRepositoriesAsync(Target(SaaS), null, 2, CancellationToken.None);
        var second = await client.ListRepositoriesAsync(Target(SaaS), first.NextCursor, 2, CancellationToken.None);

        Assert.Equal(3, first.TotalCount);
        Assert.Null(second.TotalCount);
        Assert.Null(second.NextCursor);
        Assert.Contains("page=2", handler.Requests[1].Uri.Query);
    }

    [Fact]
    public async Task ListBranches_ForSubgroupProject_UsesNumericProjectIdInThePath()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("""
            [{"name":"main","default":true,"commit":{"id":"abc"}},{"name":"feature/x","default":false,"commit":{"id":"def"}}]
            """, link: $"<{SaaS}/api/v4/projects/77/repository/branches?page=2&per_page=100>; rel=\"next\"");
        handler.EnqueueJson("""[{"name":"hotfix","default":false,"commit":{"id":"123"}}]""");

        var first = await client.ListBranchesAsync(Target(SaaS), "77", null, 100, CancellationToken.None);
        var second = await client.ListBranchesAsync(Target(SaaS), "77", first.NextCursor, 100, CancellationToken.None);

        Assert.Equal(["main", "feature/x"], first.Items.Select(branch => branch.Name));
        Assert.True(first.Items[0].IsDefault);
        Assert.False(first.Items[1].IsDefault);
        Assert.Equal("abc", first.Items[0].CommitSha);
        Assert.Equal(["hotfix"], second.Items.Select(branch => branch.Name));
        Assert.All(handler.Requests, request => Assert.Equal("/api/v4/projects/77/repository/branches", request.Uri.AbsolutePath));
        Assert.Contains("page=2", handler.Requests[1].Uri.Query);
    }

    [Theory]
    [InlineData("group/sub/project")]
    [InlineData("group%2Fproject")]
    [InlineData("../user")]
    [InlineData("")]
    [InlineData("7x")]
    public async Task ListBranches_WhenProjectIdIsNotNumeric_RefusesWithoutRequest(string projectId)
    {
        var (client, handler, _) = Create();

        await AssertCode(GitProviderErrorCodes.InvalidRepositoryId,
            () => client.ListBranchesAsync(Target(SaaS), projectId, null, 100, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("<https://evil.example.com/api/v4/projects?id_after=1&pagination=keyset>; rel=\"next\"")]
    [InlineData("<https://gitlab.com/api/v4/users?page=2>; rel=\"next\"")]
    [InlineData("<http://gitlab.com/api/v4/projects?page=2>; rel=\"next\"")]
    [InlineData("<https://gitlab.com/api/v4/projects?id_after=1;DROP>; rel=\"next\"")]
    [InlineData("<https://gitlab.com/api/v4/projects?membership=true>; rel=\"next\"")]
    public async Task ListRepositories_WhenNextLinkIsNotValid_ReturnsInvalidResponse(string link)
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(ProjectPage(1, 1), link: link);

        await AssertCode(GitProviderErrorCodes.InvalidResponse,
            () => client.ListRepositoriesAsync(Target(SaaS), null, 100, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    // ---- cursor binding ----

    [Fact]
    public async Task ListRepositories_WhenCursorIsChanged_RejectsWithoutOutboundRequest()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(ProjectPage(1, 1), link: $"<{SaaS}/api/v4/projects?id_after=1&pagination=keyset>; rel=\"next\"");
        var first = await client.ListRepositoriesAsync(Target(SaaS), null, 100, CancellationToken.None);
        var tampered = first.NextCursor![..^2] + (first.NextCursor![^2] == 'A' ? "B" : "A") + first.NextCursor![^1];

        await AssertCode(GitProviderErrorCodes.InvalidCursor,
            () => client.ListRepositoriesAsync(Target(SaaS), tampered, 100, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListRepositories_WhenCursorBelongsToAnotherServerOrConnection_Rejects()
    {
        var resolver = new FakeHostResolver().Add("gitlab.com", PublicIp).Add("gitlab.corp.example", PublicIp);
        var (client, handler, _) = Create(resolver: resolver);
        handler.EnqueueJson(ProjectPage(1, 1), link: $"<{SaaS}/api/v4/projects?id_after=1&pagination=keyset>; rel=\"next\"");
        var first = await client.ListRepositoriesAsync(Target(SaaS, "connection-1"), null, 100, CancellationToken.None);

        await AssertCode(GitProviderErrorCodes.InvalidCursor,
            () => client.ListRepositoriesAsync(Target(Corp, "connection-1"), first.NextCursor, 100, CancellationToken.None));
        await AssertCode(GitProviderErrorCodes.InvalidCursor,
            () => client.ListRepositoriesAsync(Target(SaaS, "connection-2"), first.NextCursor, 100, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    // ---- failure mapping ----

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GitProviderErrorCodes.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, GitProviderErrorCodes.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, GitProviderErrorCodes.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, GitProviderErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, GitProviderErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, GitProviderErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, GitProviderErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.Redirect, GitProviderErrorCodes.RedirectBlocked)]
    [InlineData(HttpStatusCode.BadRequest, GitProviderErrorCodes.RequestRejected)]
    public async Task Validate_MapsStatusToStableCode_WithoutRawBodyOrToken(HttpStatusCode status, string expected)
    {
        var (client, handler, logger) = Create();
        handler.EnqueueStatus(status);

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => client.ValidateAsync(Target(SaaS, null), CancellationToken.None));

        Assert.Equal(expected, exception.Code);
        Assert.DoesNotContain("RAW_PROVIDER_BODY", exception.ToString());
        Assert.DoesNotContain(CanaryPat, exception.ToString());
        Assert.All(logger.Lines, line => Assert.DoesNotContain(CanaryPat, line));
        Assert.All(logger.Lines, line => Assert.DoesNotContain("RAW_PROVIDER_BODY", line));
    }

    [Fact]
    public async Task Validate_WhenRateLimited_ReportsRetryAfterWithoutRetrying()
    {
        var (client, handler, _) = Create();
        handler.EnqueueStatus(HttpStatusCode.TooManyRequests, ("Retry-After", "45"));

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => client.ValidateAsync(Target(SaaS, null), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(45), exception.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Validate_WhenHostDoesNotResolve_ReturnsDnsFailure()
    {
        var (client, handler, _) = Create(resolver: new FakeHostResolver());
        handler.EnqueueJson("""{"id":1,"username":"x"}""");

        await AssertCode(GitProviderErrorCodes.DnsFailure, () => client.ValidateAsync(Target(Corp, null), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Validate_WhenTlsFails_ReturnsTlsFailure()
    {
        var (client, handler, _) = Create();
        handler.EnqueueThrow(new HttpRequestException("x", new AuthenticationException("bad certificate")));

        await AssertCode(GitProviderErrorCodes.TlsFailure, () => client.ValidateAsync(Target(Corp, null), CancellationToken.None));
    }

    [Fact]
    public async Task Validate_WhenRequestTimesOut_ReturnsTimeout()
    {
        var (client, handler, _) = Create();
        handler.EnqueueThrow(new TaskCanceledException("timeout", new TimeoutException()));

        await AssertCode(GitProviderErrorCodes.Timeout, () => client.ValidateAsync(Target(Corp, null), CancellationToken.None));
    }

    [Fact]
    public async Task Validate_WhenConnectionIsRefused_ReturnsUnavailable()
    {
        var (client, handler, _) = Create();
        handler.EnqueueThrow(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)));

        await AssertCode(GitProviderErrorCodes.Unavailable, () => client.ValidateAsync(Target(Corp, null), CancellationToken.None));
    }

    // ---- helpers ----

    private static (GitLabPatProviderClient Client, FakeProviderHandler Handler, ListLogger<GitLabPatProviderClient> Logger) Create(
        FakeHostResolver? resolver = null,
        IEnumerable<string>? allowedHosts = null)
    {
        var handler = new FakeProviderHandler();
        var validator = GitProviderTestFactory.CreateValidator(
            resolver ?? new FakeHostResolver().Add("gitlab.com", PublicIp).Add("gitlab.corp.example", PublicIp),
            allowedHosts);
        var logger = new ListLogger<GitLabPatProviderClient>();
        var client = new GitLabPatProviderClient(
            new FakeHttpClientFactory(_ => GitProviderTestFactory.CreateGuardedClient(handler, validator)),
            new ProviderPaginationCursorCodec(new EphemeralDataProtectionProvider()),
            logger);
        return (client, handler, logger);
    }

    private static GitProviderTarget Target(string serverUrl, string? connectionId = "connection-1")
        => new(connectionId, GitProvider.GitLab, serverUrl, CanaryPat);

    private static async Task AssertCode(string expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<GitProviderException>(action);
        Assert.Equal(expected, exception.Code);
    }

    private static string ProjectJson(long id, string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var ns = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        return $$"""
            {"id":{{id}},"name":"{{name}}","path":"{{name}}","path_with_namespace":"{{path}}","description":null,
             "namespace":{"full_path":"{{ns}}"},"http_url_to_repo":"https://gitlab.com/{{path}}.git",
             "web_url":"https://gitlab.com/{{path}}","default_branch":"main","visibility":"private",
             "last_activity_at":"2026-02-03T04:05:06.000Z"}
            """;
    }

    private static string ProjectPage(int firstId, int count)
        => "[" + string.Join(",", Enumerable.Range(firstId, count).Select(id => ProjectJson(id, $"group/project-{id}"))) + "]";
}
