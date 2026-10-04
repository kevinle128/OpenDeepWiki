using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models;
using OpenDeepWiki.Models.Admin;
using OpenDeepWiki.Services.Admin;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.GitHub;
using OpenDeepWiki.Services.Organizations;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

/// <summary>
/// Submit, visibility, listing, and admin update after the switch: connections replace plaintext credentials.
/// Legacy fields are still read through the credential resolver, but no path writes them any more.
/// </summary>
public sealed class RepositoryConnectionSwitchTests : IDisposable
{
    private const string Pat = "ghp_CANARY_switch_0123456789";

    private readonly SqliteScratchDatabase _database;
    private readonly IGitConnectionSecretProtector _protector =
        new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider());

    public RepositoryConnectionSwitchTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _database.Dispose();

    // ---- submit ----

    [Theory]
    [InlineData("octocat", null)]
    [InlineData(null, Pat)]
    [InlineData("octocat", Pat)]
    public async Task SubmitAsync_WithLegacyCredentialFields_FailsWithAStableCodeThatNamesGitConnectionId(string? account, string? password)
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<RepositoryConnectionRequestException>(
            () => service.SubmitAsync(NewRequest(isPublic: false, account: account, password: password)));

        Assert.Equal(RepositoryConnectionErrorCodes.LegacyCredentialFieldsRejected, exception.ErrorCode);
        Assert.Contains("GitConnectionId", exception.Message);
        Assert.DoesNotContain(Pat, exception.ToString());
        await AssertNoRepositoryAsync();
    }

    [Fact]
    public async Task SubmitAsync_ForAnExistingRepositoryWithLegacyCredentialFields_StillFails()
    {
        await SeedRepositoryAsync("existing", "acme", "widgets", "https://github.com/acme/widgets.git");

        await Assert.ThrowsAsync<RepositoryConnectionRequestException>(
            () => CreateService().SubmitAsync(NewRequest(branch: "dev", password: Pat)));
    }

    [Theory]
    [InlineData("https://octocat:ghp_CANARY_switch_0123456789@github.com/acme/widgets.git")]
    [InlineData("https://ghp_CANARY_switch_0123456789@github.com/acme/widgets.git")]
    public async Task SubmitAsync_ForAUrlWithUserInfo_FailsWithoutEchoingTheUrl(string url)
    {
        var exception = await Assert.ThrowsAsync<RepositoryConnectionRequestException>(
            () => CreateService().SubmitAsync(NewRequest(url: url)));

        Assert.Equal(RepositoryConnectionErrorCodes.GitUrlContainsCredentials, exception.ErrorCode);
        Assert.DoesNotContain(Pat, exception.ToString());
        await AssertNoRepositoryAsync();
    }

    [Fact]
    public async Task SubmitAsync_ForAPrivateRepositoryWithoutAConnection_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().SubmitAsync(NewRequest(isPublic: false)));

        await AssertNoRepositoryAsync();
    }

    [Fact]
    public async Task SubmitAsync_WithAUsableConnection_StoresTheConnectionAndNeverTheLegacyFields()
    {
        await SeedConnectionAsync("c1");

        var repository = await CreateService().SubmitAsync(NewRequest(isPublic: false, connectionId: "c1"));

        await using var context = _database.Open();
        var stored = await context.Repositories.SingleAsync(item => item.Id == repository.Id);
        Assert.Equal("c1", stored.GitConnectionId);
        Assert.Equal(GitProvider.GitHub, stored.Provider);
        Assert.Equal("https://github.com", stored.ProviderBaseUrl);
        Assert.Null(stored.AuthAccount);
        Assert.Null(stored.AuthPassword);
        Assert.False(stored.IsPublic);
        var audit = Assert.Single(await context.GitConnectionAuditEvents.ToListAsync());
        Assert.Equal(GitConnectionAuditEventType.RepositoryAssigned, audit.EventType);
        Assert.Equal("user-1", audit.ActorUserId);
        Assert.Equal(stored.Id, audit.RepositoryId);
    }

    [Theory]
    [InlineData("missing", RepositoryConnectionErrorCodes.ConnectionNotFound)]
    [InlineData("off", RepositoryConnectionErrorCodes.ConnectionDisabled)]
    [InlineData("gone", RepositoryConnectionErrorCodes.ConnectionNotFound)]
    [InlineData("gitlab", RepositoryConnectionErrorCodes.ConnectionHostMismatch)]
    public async Task SubmitAsync_WithAnUnusableConnection_FailsAndStoresNothing(string connectionId, string code)
    {
        await SeedConnectionAsync("off", enabled: false);
        await SeedConnectionAsync("gone", deleted: true, accountId: "1002");
        await SeedConnectionAsync("gitlab", provider: GitProvider.GitLab, serverUrl: "https://gitlab.com", accountId: "1003");

        var exception = await Assert.ThrowsAsync<RepositoryConnectionRequestException>(
            () => CreateService().SubmitAsync(NewRequest(isPublic: false, connectionId: connectionId)));

        Assert.Equal(code, exception.ErrorCode);
        await AssertNoRepositoryAsync();
    }

    [Fact]
    public async Task SubmitAsync_ForAPublicRepositoryWithoutCredentials_StaysUnchanged()
    {
        var repository = await CreateService().SubmitAsync(NewRequest(isPublic: true));

        await using var context = _database.Open();
        var stored = await context.Repositories.SingleAsync(item => item.Id == repository.Id);
        Assert.Null(stored.GitConnectionId);
        Assert.Null(stored.Provider);
        Assert.Empty(await context.GitConnectionAuditEvents.ToListAsync());
    }

    // ---- listing ----

    [Fact]
    public async Task GetListAsync_ReportsTheConnectionAndKeepsHasPasswordTrueForLegacyIslands()
    {
        await SeedConnectionAsync("c1");
        await SeedRepositoryAsync("connected", "acme", "one", "https://github.com/acme/one.git", connectionId: "c1");
        await SeedRepositoryAsync("island", "acme", "two", "https://gitee.com/acme/two.git", password: Pat);
        await SeedRepositoryAsync("open", "acme", "three", "https://github.com/acme/three.git");

        var list = await CreateService().GetListAsync();

        var connected = list.Items.Single(item => item.Id == "connected");
        Assert.True(connected.HasGitConnection);
        Assert.Equal("c1", connected.GitConnectionId);
        Assert.True(connected.HasPassword);
        var island = list.Items.Single(item => item.Id == "island");
        Assert.False(island.HasGitConnection);
        Assert.Null(island.GitConnectionId);
        Assert.True(island.HasPassword);
        var open = list.Items.Single(item => item.Id == "open");
        Assert.False(open.HasGitConnection);
        Assert.False(open.HasPassword);
    }

    [Fact]
    public async Task ListAndDetail_NeverReturnCredentialsEmbeddedInTheGitUrl()
    {
        await SeedRepositoryAsync("legacy", "acme", "one", "https://octocat:" + Pat + "@github.com/acme/one.git");
        await SeedRepositoryAsync("token-only", "acme", "two", "https://" + Pat + "@github.com/acme/two.git");

        var list = await CreateService().GetListAsync();
        var adminList = await CreateAdminService().GetRepositoriesAsync(1, 20, null, null);
        var detail = await CreateAdminService().GetRepositoryByIdAsync("legacy");

        foreach (var text in list.Items.SelectMany(item => new[] { item.GitUrl, item.SourceLocation })
                     .Concat(adminList.Items.SelectMany(item => new[] { item.GitUrl, item.SourceLocation }))
                     .Concat([detail!.GitUrl, detail.SourceLocation]))
        {
            Assert.DoesNotContain(Pat, text);
            Assert.DoesNotContain("@", text);
        }

        Assert.Equal("https://github.com/acme/one.git", list.Items.Single(item => item.Id == "legacy").GitUrl);
        await using var context = _database.Open();
        Assert.Contains(Pat, (await context.Repositories.SingleAsync(item => item.Id == "legacy")).GitUrl);
    }

    // ---- visibility ----

    [Fact]
    public async Task UpdateVisibilityAsync_ToPrivate_NeedsAUsableConnectionOrALegacyPassword()
    {
        await SeedConnectionAsync("c1");
        await SeedConnectionAsync("off", enabled: false, accountId: "1002");
        await SeedRepositoryAsync("with-connection", "acme", "a", "https://github.com/acme/a.git", connectionId: "c1");
        await SeedRepositoryAsync("disabled-connection", "acme", "b", "https://github.com/acme/b.git", connectionId: "off", password: Pat);
        await SeedRepositoryAsync("legacy-island", "acme", "c", "https://gitee.com/acme/c.git", password: Pat);
        await SeedRepositoryAsync("nothing", "acme", "d", "https://github.com/acme/d.git");

        Assert.IsType<Ok<UpdateVisibilityResponse>>(await MakePrivateAsync("with-connection"));
        Assert.IsType<Ok<UpdateVisibilityResponse>>(await MakePrivateAsync("legacy-island"));
        Assert.IsType<BadRequest<UpdateVisibilityResponse>>(await MakePrivateAsync("disabled-connection"));
        Assert.IsType<BadRequest<UpdateVisibilityResponse>>(await MakePrivateAsync("nothing"));
    }

    [Fact]
    public async Task UpdateVisibilityAsync_ToPublic_NeverNeedsACredential()
    {
        await SeedRepositoryAsync("nothing", "acme", "d", "https://github.com/acme/d.git", isPublic: false);

        var result = await CreateService().UpdateVisibilityAsync(new UpdateVisibilityRequest { RepositoryId = "nothing", IsPublic = true });

        Assert.IsType<Ok<UpdateVisibilityResponse>>(result);
    }

    // ---- admin update ----

    [Theory]
    [InlineData("octocat", null)]
    [InlineData(null, Pat)]
    public async Task AdminUpdate_WithLegacyCredentialFields_FailsAndChangesNothing(string? account, string? password)
    {
        await SeedRepositoryAsync("r1", "acme", "one", "https://github.com/acme/one.git", password: "old-secret");
        var service = CreateAdminService();

        var exception = await Assert.ThrowsAsync<RepositoryConnectionRequestException>(
            () => service.UpdateRepositoryAsync("r1", new UpdateRepositoryRequest { AuthAccount = account, AuthPassword = password }));

        Assert.Equal(RepositoryConnectionErrorCodes.LegacyCredentialFieldsRejected, exception.ErrorCode);
        await using var context = _database.Open();
        Assert.Equal("old-secret", (await context.Repositories.SingleAsync()).AuthPassword);
    }

    [Fact]
    public async Task AdminUpdate_ReassignsTheConnectionWritesBothAuditEventsAndLeavesTheLegacyFieldsAlone()
    {
        await SeedConnectionAsync("c1");
        await SeedConnectionAsync("c2", accountId: "1002");
        await SeedRepositoryAsync("r1", "acme", "one", "https://github.com/acme/one.git", connectionId: "c1",
            provider: GitProvider.GitHub, baseUrl: "https://github.com", password: "old-secret");

        var updated = await CreateAdminService(userId: "admin-1")
            .UpdateRepositoryAsync("r1", new UpdateRepositoryRequest { GitConnectionId = "c2" });

        Assert.True(updated);
        await using var context = _database.Open();
        var repository = await context.Repositories.SingleAsync();
        Assert.Equal("c2", repository.GitConnectionId);
        Assert.Equal("old-secret", repository.AuthPassword);
        var events = await context.GitConnectionAuditEvents.OrderBy(item => item.GitConnectionId).ToListAsync();
        Assert.Collection(
            events,
            first =>
            {
                Assert.Equal("c1", first.GitConnectionId);
                Assert.Equal(GitConnectionAuditEventType.RepositoryUnassigned, first.EventType);
                Assert.Equal("admin-1", first.ActorUserId);
            },
            second =>
            {
                Assert.Equal("c2", second.GitConnectionId);
                Assert.Equal(GitConnectionAuditEventType.RepositoryAssigned, second.EventType);
            });
    }

    [Fact]
    public async Task AdminUpdate_ToTheSameConnection_WritesNoAuditEvent()
    {
        await SeedConnectionAsync("c1");
        await SeedRepositoryAsync("r1", "acme", "one", "https://github.com/acme/one.git", connectionId: "c1",
            provider: GitProvider.GitHub, baseUrl: "https://github.com");

        await CreateAdminService().UpdateRepositoryAsync("r1", new UpdateRepositoryRequest { GitConnectionId = "c1", IsPublic = false });

        await using var context = _database.Open();
        Assert.Empty(await context.GitConnectionAuditEvents.ToListAsync());
        Assert.False((await context.Repositories.SingleAsync()).IsPublic);
    }

    [Theory]
    [InlineData("missing", RepositoryConnectionErrorCodes.ConnectionNotFound)]
    [InlineData("off", RepositoryConnectionErrorCodes.ConnectionDisabled)]
    [InlineData("gitlab", RepositoryConnectionErrorCodes.ConnectionHostMismatch)]
    public async Task AdminUpdate_ToAnUnusableConnection_FailsAndKeepsTheCurrentOne(string connectionId, string code)
    {
        await SeedConnectionAsync("c1");
        await SeedConnectionAsync("off", enabled: false, accountId: "1002");
        await SeedConnectionAsync("gitlab", provider: GitProvider.GitLab, serverUrl: "https://gitlab.com", accountId: "1003");
        await SeedRepositoryAsync("r1", "acme", "one", "https://github.com/acme/one.git", connectionId: "c1",
            provider: GitProvider.GitHub, baseUrl: "https://github.com");

        var exception = await Assert.ThrowsAsync<RepositoryConnectionRequestException>(
            () => CreateAdminService().UpdateRepositoryAsync("r1", new UpdateRepositoryRequest { GitConnectionId = connectionId }));

        Assert.Equal(code, exception.ErrorCode);
        await using var context = _database.Open();
        Assert.Equal("c1", (await context.Repositories.SingleAsync()).GitConnectionId);
        Assert.Empty(await context.GitConnectionAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task AdminUpdate_OfAnUnknownRepository_ReturnsFalse()
    {
        Assert.False(await CreateAdminService().UpdateRepositoryAsync("missing", new UpdateRepositoryRequest { IsPublic = true }));
    }

    // ---- helpers ----

    private Task<IResult> MakePrivateAsync(string repositoryId)
        => CreateService().UpdateVisibilityAsync(new UpdateVisibilityRequest { RepositoryId = repositoryId, IsPublic = false });

    private static RepositorySubmitRequest NewRequest(
        string url = "https://github.com/acme/widgets.git",
        bool isPublic = true,
        string branch = "main",
        string? connectionId = null,
        string? account = null,
        string? password = null)
        => new()
        {
            GitUrl = url,
            OrgName = "acme",
            RepoName = "widgets",
            BranchName = branch,
            LanguageCode = "zh",
            IsPublic = isPublic,
            GitConnectionId = connectionId,
            AuthAccount = account,
            AuthPassword = password
        };

    private RepositoryService CreateService(string userId = "user-1")
    {
        var context = _database.Open();
        var user = new TestUser(userId);
        // The platform reports no repository, so the submitted visibility is kept as sent.
        var platform = new Mock<IGitPlatformService>();
        platform.Setup(item => item.CheckRepoExistsAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new GitRepoInfo(false, string.Empty, null, null, 0, 0, null, null, false));
        return new RepositoryService(
            context,
            platform.Object,
            user,
            Mock.Of<IGitHubAppService>(),
            Mock.Of<IOrganizationService>(),
            new RepositoryFullRegenerationCleaner(),
            new RepositoryGenerationLockService(context),
            Options.Create(new RepositoryAnalyzerOptions()),
            new GitCredentialResolver(context, _protector, NullLogger<GitCredentialResolver>.Instance),
            new GitConnectionAuthorizationService(user, context),
            new BranchActionAuditor(context, NullLogger<BranchActionAuditor>.Instance));
    }

    private AdminRepositoryService CreateAdminService(string userId = "admin-1")
    {
        var context = _database.Open();
        var monitor = new Mock<IOptionsMonitor<WikiGeneratorOptions>>();
        monitor.SetupGet(item => item.CurrentValue).Returns(new WikiGeneratorOptions());
        return new AdminRepositoryService(
            context,
            Mock.Of<IGitPlatformService>(),
            Mock.Of<IRepositoryAnalyzer>(),
            Mock.Of<IWikiGenerator>(),
            new RepositoryFullRegenerationCleaner(),
            new RepositoryScanPlanResolver(monitor.Object),
            new RepositoryGenerationLockService(context),
            new TestUser(userId),
            new BranchActionAuditor(context, NullLogger<BranchActionAuditor>.Instance));
    }

    private async Task SeedConnectionAsync(
        string id,
        bool enabled = true,
        bool deleted = false,
        string accountId = "1001",
        GitProvider provider = GitProvider.GitHub,
        string serverUrl = "https://github.com")
    {
        await using var context = _database.Open();
        if (!await context.Users.AnyAsync(item => item.Id == "admin-1"))
        {
            context.Users.Add(new User { Id = "admin-1", Name = "admin-1", Email = "admin-1@example.com" });
        }

        var connection = new GitConnection
        {
            Id = id,
            Provider = provider,
            NormalizedServerUrl = serverUrl,
            ExternalAccountId = accountId,
            DisplayName = id,
            AccountName = id,
            ProtectedToken = _protector.Protect(Pat),
            CreatedByUserId = "user-2",
            IsEnabled = enabled
        };
        if (deleted)
        {
            connection.MarkAsDeleted();
        }

        context.GitConnections.Add(connection);
        await context.SaveChangesAsync();
    }

    private async Task SeedRepositoryAsync(
        string id, string org, string name, string gitUrl,
        string? connectionId = null, GitProvider? provider = null, string? baseUrl = null,
        string? password = null, bool isPublic = true)
    {
        await using var context = _database.Open();
        context.Repositories.Add(new Repository
        {
            Id = id,
            OwnerUserId = "user-1",
            GitUrl = gitUrl,
            OrgName = org,
            RepoName = name,
            Status = RepositoryStatus.Completed,
            GitConnectionId = connectionId,
            Provider = provider,
            ProviderBaseUrl = baseUrl,
            AuthPassword = password,
            IsPublic = isPublic
        });
        await context.SaveChangesAsync();
    }

    private async Task AssertNoRepositoryAsync()
    {
        await using var context = _database.Open();
        Assert.Empty(await context.Repositories.ToListAsync());
        Assert.Empty(await context.GitConnectionAuditEvents.ToListAsync());
    }

    private sealed class TestUser(string userId) : IUserContext
    {
        public string? UserId => userId;
        public string? UserName => userId;
        public string? Email => null;
        public bool IsAuthenticated => true;
        public ClaimsPrincipal? User => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test"));
    }
}
