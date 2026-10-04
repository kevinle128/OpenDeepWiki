using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

public class GitHubPatProviderClientTests
{
    private const string CanaryPat = "ghp_CANARY_provider_0123456789abcdef";
    private const string Api = "https://api.github.com";

    // ---- identity ----

    [Fact]
    public async Task Validate_CallsUserEndpointWithTokenInHeaderOnly_AndTrustsNumericId()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("""{"id":583231,"login":"octocat"}""");

        var identity = await client.ValidateAsync(Target(null), CancellationToken.None);

        Assert.Equal("583231", identity.ExternalAccountId);
        Assert.Equal("octocat", identity.AccountName);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{Api}/user", request.Uri.AbsoluteUri);
        Assert.Equal($"Bearer {CanaryPat}", request.Authorization);
        Assert.DoesNotContain(CanaryPat, request.Uri.ToString());
        Assert.Equal("application/vnd.github+json", request.Headers["Accept"]);
        Assert.True(request.Headers.ContainsKey("X-GitHub-Api-Version"));
        Assert.True(request.Headers.ContainsKey("User-Agent"));
    }

    [Theory]
    [InlineData("""{"login":"octocat"}""")]
    [InlineData("""{"id":"not-a-number","login":"octocat"}""")]
    [InlineData("""{"id":1}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task Validate_WhenResponseLacksTrustedIdentity_ReturnsInvalidResponse(string body)
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(body);

        await AssertCode(GitProviderErrorCodes.InvalidResponse, () => client.ValidateAsync(Target(null), CancellationToken.None));
    }

    [Fact]
    public async Task GetRepository_RefreshesMutableMetadataByStableRemoteId()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(RepoJson(42, "acme/old-name"));
        handler.EnqueueJson(RepoJson(42, "acme/new-name"));

        var before = await client.GetRepositoryAsync(Target(), "42", CancellationToken.None);
        var after = await client.GetRepositoryAsync(Target(), "42", CancellationToken.None);

        Assert.Equal("42", before.ProviderRepositoryId);
        Assert.Equal(before.ProviderRepositoryId, after.ProviderRepositoryId);
        Assert.Equal("acme/old-name", before.FullName);
        Assert.Equal("acme/new-name", after.FullName);
        Assert.All(handler.Requests, request => Assert.Equal($"{Api}/repositories/42", request.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task GetRepository_MapsFieldsAndVisibility()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("""
            {"id":7,"name":"widgets","full_name":"acme/widgets","owner":{"login":"acme"},"description":"A widget",
             "clone_url":"https://github.com/acme/widgets.git","html_url":"https://github.com/acme/widgets",
             "default_branch":"main","private":true,"visibility":"private","updated_at":"2026-01-02T03:04:05Z"}
            """);

        var repository = await client.GetRepositoryAsync(Target(), "7", CancellationToken.None);

        Assert.Equal("widgets", repository.Name);
        Assert.Equal("acme", repository.Namespace);
        Assert.Equal("A widget", repository.Description);
        Assert.Equal("https://github.com/acme/widgets.git", repository.CloneUrl);
        Assert.Equal("main", repository.DefaultBranch);
        Assert.Equal("Private", repository.Visibility);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), repository.UpdatedAt);
    }

    // ---- paging ----

    [Fact]
    public async Task ListRepositories_WhenMoreThanOnePage_FollowsValidatedLinksWithoutDuplicates()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(RepoPage(1, 100), link: $"<{Api}/user/repos?visibility=all&per_page=100&page=2>; rel=\"next\", <{Api}/user/repos?page=3>; rel=\"last\"");
        handler.EnqueueJson(RepoPage(101, 100), link: $"<{Api}/user/repos?visibility=all&per_page=100&page=3>; rel=\"next\"");
        handler.EnqueueJson(RepoPage(201, 50));

        var ids = new List<string>();
        string? cursor = null;
        do
        {
            var page = await client.ListRepositoriesAsync(Target(), cursor, 100, CancellationToken.None);
            ids.AddRange(page.Items.Select(item => item.ProviderRepositoryId));
            cursor = page.NextCursor;
            Assert.Null(page.TotalCount);
        }
        while (cursor is not null);

        Assert.Equal(250, ids.Count);
        Assert.Equal(250, ids.Distinct().Count());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal($"{Api}/user/repos?visibility=all&affiliation=owner%2Ccollaborator%2Corganization_member&sort=full_name&per_page=100",
            handler.Requests[0].Uri.AbsoluteUri);
        Assert.Contains("page=2", handler.Requests[1].Uri.Query);
        Assert.Contains("page=3", handler.Requests[2].Uri.Query);
        Assert.All(handler.Requests, request => Assert.Contains("per_page=100", request.Uri.Query));
    }

    [Fact]
    public async Task ListRepositories_ReturnsOpaqueCursorWithoutTheLinkUrl()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(RepoPage(1, 1), link: $"<{Api}/user/repos?page=2>; rel=\"next\"");

        var page = await client.ListRepositoriesAsync(Target(), null, 100, CancellationToken.None);

        Assert.NotNull(page.NextCursor);
        Assert.DoesNotContain("github", page.NextCursor!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("page", page.NextCursor!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<https://evil.example.com/user/repos?page=2>; rel=\"next\"")]
    [InlineData("<http://api.github.com/user/repos?page=2>; rel=\"next\"")]
    [InlineData("<https://api.github.com/orgs/x/repos?page=2>; rel=\"next\"")]
    [InlineData("<https://api.github.com:8443/user/repos?page=2>; rel=\"next\"")]
    [InlineData("<https://api.github.com/user/repos?page=abc>; rel=\"next\"")]
    [InlineData("<https://api.github.com/user/repos?sort=full_name>; rel=\"next\"")]
    [InlineData("<not a url>; rel=\"next\"")]
    public async Task ListRepositories_WhenNextLinkIsNotValid_ReturnsInvalidResponseAndNeverFollowsIt(string link)
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(RepoPage(1, 1), link: link);

        await AssertCode(GitProviderErrorCodes.InvalidResponse,
            () => client.ListRepositoriesAsync(Target(), null, 100, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListRepositories_ClampsPageSize()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("[]").EnqueueJson("[]");

        await client.ListRepositoriesAsync(Target(), null, 500, CancellationToken.None);
        await client.ListRepositoriesAsync(Target(), null, 0, CancellationToken.None);

        Assert.Contains("per_page=100", handler.Requests[0].Uri.Query);
        Assert.Contains("per_page=50", handler.Requests[1].Uri.Query);
    }

    [Fact]
    public async Task ListBranches_UsesNumericRepositoryIdAndFollowsLinks()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("""[{"name":"main","commit":{"sha":"abc"}},{"name":"dev","commit":{"sha":"def"}}]""",
            link: $"<{Api}/repositories/42/branches?per_page=100&page=2>; rel=\"next\"");
        handler.EnqueueJson("""[{"name":"release/1.0","commit":{"sha":"123"}}]""");

        var first = await client.ListBranchesAsync(Target(), "42", null, 100, CancellationToken.None);
        var second = await client.ListBranchesAsync(Target(), "42", first.NextCursor, 100, CancellationToken.None);

        Assert.Equal(["main", "dev"], first.Items.Select(branch => branch.Name));
        Assert.Equal("abc", first.Items[0].CommitSha);
        Assert.Equal(["release/1.0"], second.Items.Select(branch => branch.Name));
        Assert.Null(second.NextCursor);
        Assert.Equal($"{Api}/repositories/42/branches?per_page=100", handler.Requests[0].Uri.AbsoluteUri);
        Assert.Equal($"{Api}/repositories/42/branches?per_page=100&page=2", handler.Requests[1].Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("../../user")]
    [InlineData("acme/widgets")]
    [InlineData("42?x=1")]
    [InlineData("")]
    [InlineData("abc")]
    public async Task ListBranches_WhenRepositoryIdIsNotNumeric_RefusesWithoutRequest(string repositoryId)
    {
        var (client, handler, _) = Create();

        await AssertCode(GitProviderErrorCodes.InvalidRepositoryId,
            () => client.ListBranchesAsync(Target(), repositoryId, null, 100, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    // ---- cursor binding ----

    [Fact]
    public async Task ListRepositories_WhenCursorIsChanged_RejectsWithoutOutboundRequest()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(RepoPage(1, 1), link: $"<{Api}/user/repos?page=2>; rel=\"next\"");
        var first = await client.ListRepositoriesAsync(Target(), null, 100, CancellationToken.None);
        var tampered = first.NextCursor![..^2] + (first.NextCursor![^2] == 'A' ? "B" : "A") + first.NextCursor![^1];

        await AssertCode(GitProviderErrorCodes.InvalidCursor,
            () => client.ListRepositoriesAsync(Target(), tampered, 100, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListRepositories_WhenCursorBelongsToAnotherConnection_RejectsWithoutOutboundRequest()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson(RepoPage(1, 1), link: $"<{Api}/user/repos?page=2>; rel=\"next\"");
        var first = await client.ListRepositoriesAsync(Target("connection-1"), null, 100, CancellationToken.None);

        await AssertCode(GitProviderErrorCodes.InvalidCursor,
            () => client.ListRepositoriesAsync(Target("connection-2"), first.NextCursor, 100, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListBranches_WhenCursorBelongsToAnotherRepository_RejectsWithoutOutboundRequest()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("[]", link: $"<{Api}/repositories/42/branches?page=2>; rel=\"next\"");
        var first = await client.ListBranchesAsync(Target(), "42", null, 100, CancellationToken.None);

        await AssertCode(GitProviderErrorCodes.InvalidCursor,
            () => client.ListBranchesAsync(Target(), "43", first.NextCursor, 100, CancellationToken.None));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ListRepositories_WhenCursorWasIssuedForBranches_Rejects()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("[]", link: $"<{Api}/repositories/42/branches?page=2>; rel=\"next\"");
        var branches = await client.ListBranchesAsync(Target(), "42", null, 100, CancellationToken.None);

        await AssertCode(GitProviderErrorCodes.InvalidCursor,
            () => client.ListRepositoriesAsync(Target(), branches.NextCursor, 100, CancellationToken.None));
    }

    // ---- failure mapping ----

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GitProviderErrorCodes.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, GitProviderErrorCodes.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, GitProviderErrorCodes.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, GitProviderErrorCodes.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, GitProviderErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, GitProviderErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GitProviderErrorCodes.Unavailable)]
    [InlineData(HttpStatusCode.Found, GitProviderErrorCodes.RedirectBlocked)]
    [InlineData(HttpStatusCode.MovedPermanently, GitProviderErrorCodes.RedirectBlocked)]
    [InlineData(HttpStatusCode.UnprocessableEntity, GitProviderErrorCodes.RequestRejected)]
    public async Task Validate_MapsStatusToStableCode_WithoutRawBodyOrToken(HttpStatusCode status, string expected)
    {
        var (client, handler, logger) = Create();
        handler.EnqueueStatus(status, ("Location", "http://169.254.169.254/latest"));

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => client.ValidateAsync(Target(null), CancellationToken.None));

        Assert.Equal(expected, exception.Code);
        Assert.DoesNotContain("RAW_PROVIDER_BODY", exception.ToString());
        Assert.DoesNotContain(CanaryPat, exception.ToString());
        Assert.All(logger.Lines, line => Assert.DoesNotContain(CanaryPat, line));
        Assert.All(logger.Lines, line => Assert.DoesNotContain("RAW_PROVIDER_BODY", line));
        // A redirect is never followed, so the redirect target never receives the token.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Validate_WhenForbiddenWithExhaustedRateLimit_ReturnsRateLimitedWithWait()
    {
        var (client, handler, _) = Create();
        var reset = DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeSeconds().ToString();
        handler.EnqueueStatus(HttpStatusCode.Forbidden, ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset));

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => client.ValidateAsync(Target(null), CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.RateLimited, exception.Code);
        Assert.InRange(exception.RetryAfter!.Value.TotalSeconds, 60, 91);
    }

    [Fact]
    public async Task Validate_WhenRateLimitedWithRetryAfter_ReportsWaitAndDoesNotRetry()
    {
        var (client, handler, _) = Create();
        handler.EnqueueStatus(HttpStatusCode.TooManyRequests, ("Retry-After", "30"));

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => client.ValidateAsync(Target(null), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Validate_WhenRetryAfterIsHuge_CapsTheReportedWait()
    {
        var (client, handler, _) = Create();
        handler.EnqueueStatus(HttpStatusCode.TooManyRequests, ("Retry-After", "999999999"));

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => client.ValidateAsync(Target(null), CancellationToken.None));

        Assert.True(exception.RetryAfter <= TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Validate_WhenHostDoesNotResolve_ReturnsDnsFailure()
    {
        var (client, handler, _) = Create();
        handler.EnqueueThrow(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound)));

        await AssertCode(GitProviderErrorCodes.DnsFailure, () => client.ValidateAsync(Target(null), CancellationToken.None));
    }

    [Fact]
    public async Task Validate_WhenTlsHandshakeFails_ReturnsTlsFailure()
    {
        var (client, handler, _) = Create();
        handler.EnqueueThrow(new HttpRequestException("x", new AuthenticationException("The remote certificate is invalid.")));

        await AssertCode(GitProviderErrorCodes.TlsFailure, () => client.ValidateAsync(Target(null), CancellationToken.None));
    }

    [Fact]
    public async Task Validate_WhenConnectionFails_ReturnsUnavailable()
    {
        var (client, handler, _) = Create();
        handler.EnqueueThrow(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused)));

        await AssertCode(GitProviderErrorCodes.Unavailable, () => client.ValidateAsync(Target(null), CancellationToken.None));
    }

    [Fact]
    public async Task Validate_WhenTheRequestTimesOut_ReturnsTimeout()
    {
        var (client, handler, _) = Create();
        handler.EnqueueThrow(new TaskCanceledException("timeout", new TimeoutException()));

        await AssertCode(GitProviderErrorCodes.Timeout, () => client.ValidateAsync(Target(null), CancellationToken.None));
    }

    [Fact]
    public async Task Validate_WhenCallerCancels_PropagatesCancellation()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("""{"id":1,"login":"x"}""");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ValidateAsync(Target(null), cts.Token));
    }

    [Fact]
    public async Task ListRepositories_WhenResponseIsMalformed_ReturnsInvalidResponse()
    {
        var (client, handler, _) = Create();
        handler.EnqueueJson("""[{"id":1,"name":"x"}]""");

        await AssertCode(GitProviderErrorCodes.InvalidResponse,
            () => client.ListRepositoriesAsync(Target(), null, 100, CancellationToken.None));
    }

    [Fact]
    public void Target_ToStringNeverContainsTheToken()
    {
        Assert.DoesNotContain(CanaryPat, Target().ToString());
    }

    // ---- helpers ----

    private static (GitHubPatProviderClient Client, FakeProviderHandler Handler, ListLogger<GitHubPatProviderClient> Logger) Create()
    {
        var handler = new FakeProviderHandler();
        var logger = new ListLogger<GitHubPatProviderClient>();
        var client = new GitHubPatProviderClient(
            new FakeHttpClientFactory(_ => new HttpClient(handler)),
            new ProviderPaginationCursorCodec(new EphemeralDataProtectionProvider()),
            logger);
        return (client, handler, logger);
    }

    private static GitProviderTarget Target(string? connectionId = "connection-1")
        => new(connectionId, GitProvider.GitHub, "https://github.com", CanaryPat);

    private static async Task AssertCode(string expected, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<GitProviderException>(action);
        Assert.Equal(expected, exception.Code);
    }

    private static string RepoJson(long id, string fullName)
    {
        var name = fullName[(fullName.IndexOf('/') + 1)..];
        return $$"""
            {"id":{{id}},"name":"{{name}}","full_name":"{{fullName}}","owner":{"login":"acme"},"description":null,
             "clone_url":"https://github.com/{{fullName}}.git","html_url":"https://github.com/{{fullName}}",
             "default_branch":"main","private":false,"visibility":"public","updated_at":"2026-01-02T03:04:05Z"}
            """;
    }

    private static string RepoPage(int firstId, int count)
        => "[" + string.Join(",", Enumerable.Range(firstId, count).Select(id => RepoJson(id, $"acme/repo-{id}"))) + "]";
}
