using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models.Admin;
using OpenDeepWiki.Models.ConnectedRepositories;
using OpenDeepWiki.Models.GitHub;
using OpenDeepWiki.Services.Admin;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.GitHub;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

/// <summary>
/// GitHub App imports keep their routes and request shape, and now give each repository the stable GitHub identity.
/// A remote that a Git connection or an earlier App import registered is then found, not duplicated.
/// </summary>
public sealed class GitHubAppImportAdapterTests : IDisposable
{
    private readonly SqliteScratchDatabase _database;

    public GitHubAppImportAdapterTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task UserImport_WhenTheRepositoryIdIsKnown_StoresTheStableGitHubIdentity()
    {
        await using var context = _database.Open();

        await UserService(context).ImportAsync(UserRequest(Repo(42, "acme/widgets")), "user-1");

        var repository = await context.Repositories.SingleAsync();
        Assert.Equal(GitProvider.GitHub, repository.Provider);
        Assert.Equal("https://github.com", repository.ProviderBaseUrl);
        Assert.Equal("42", repository.ProviderRepositoryId);
        Assert.Equal("main", repository.DefaultBranch);
        Assert.Null(repository.GitConnectionId);
    }

    [Fact]
    public async Task UserImport_WithoutARepositoryId_KeepsTheLegacyShape()
    {
        await using var context = _database.Open();

        await UserService(context).ImportAsync(UserRequest(Repo(null, "acme/widgets")), "user-1");

        var repository = await context.Repositories.SingleAsync();
        Assert.Null(repository.Provider);
        Assert.Null(repository.ProviderRepositoryId);
    }

    [Fact]
    public async Task UserImport_WhenTheRemoteIsAlreadyRegisteredUnderAnotherName_SkipsIt()
    {
        await using var context = _database.Open();
        context.Repositories.Add(new Repository
        {
            Id = "existing", OwnerUserId = "user-2", GitUrl = "https://github.com/acme/old-name.git", OrgName = "acme", RepoName = "old-name",
            Provider = GitProvider.GitHub, ProviderBaseUrl = "https://github.com", ProviderRepositoryId = "42", GitConnectionId = null
        });
        await context.SaveChangesAsync();

        var result = await UserService(context).ImportAsync(UserRequest(Repo(42, "acme/widgets")), "user-1");

        Assert.Equal(0, result.Imported);
        Assert.Equal(1, result.Skipped);
        Assert.Single(await context.Repositories.ToListAsync());
    }

    [Fact]
    public async Task AdminBatchImport_StoresTheStableIdentityAndSkipsAnAlreadyRegisteredRemote()
    {
        await using var context = _database.Open();
        context.Departments.Add(new Department { Id = "dept-1", Name = "Engineering", IsActive = true });
        context.Repositories.Add(new Repository
        {
            Id = "existing", OwnerUserId = "user-2", GitUrl = "https://github.com/acme/renamed.git", OrgName = "acme", RepoName = "renamed",
            Provider = GitProvider.GitHub, ProviderBaseUrl = "https://github.com", ProviderRepositoryId = "7"
        });
        await context.SaveChangesAsync();

        var result = await AdminService(context).BatchImportAsync(new BatchImportRequest
        {
            InstallationId = 1,
            DepartmentId = "dept-1",
            LanguageCode = "en",
            Repos = [Repo(7, "acme/gadgets"), Repo(8, "acme/tools")]
        }, "user-1");

        Assert.Equal(1, result.Imported);
        Assert.Equal(1, result.Skipped);
        var imported = await context.Repositories.SingleAsync(item => item.RepoName == "tools");
        Assert.Equal("8", imported.ProviderRepositoryId);
        Assert.Equal(GitProvider.GitHub, imported.Provider);
    }

    [Fact]
    public async Task ListInstallationRepos_MarksARepositoryAsImportedWhenItsIdentityIsRegistered()
    {
        await using var context = _database.Open();
        context.GitHubAppInstallations.Add(new GitHubAppInstallation { Id = "i1", InstallationId = 1, AccountLogin = "acme" });
        context.Repositories.Add(new Repository
        {
            Id = "existing", OwnerUserId = "user-2", GitUrl = "https://github.com/acme/renamed.git", OrgName = "acme", RepoName = "renamed",
            Provider = GitProvider.GitHub, ProviderBaseUrl = "https://github.com", ProviderRepositoryId = "42"
        });
        await context.SaveChangesAsync();
        var app = new Mock<IGitHubAppService>();
        app.Setup(x => x.ListInstallationReposAsync(1, 1, 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubRepoListResult
            {
                TotalCount = 2,
                Repositories =
                [
                    new GitHubInstallationRepo { Id = 42, FullName = "acme/widgets", Name = "widgets", Owner = "acme", CloneUrl = "https://github.com/acme/widgets.git" },
                    new GitHubInstallationRepo { Id = 43, FullName = "acme/other", Name = "other", Owner = "acme", CloneUrl = "https://github.com/acme/other.git" }
                ]
            });

        var list = await new UserGitHubImportService(context, app.Object, NullLogger<UserGitHubImportService>.Instance)
            .ListInstallationReposAsync(1, 1, 30);

        Assert.True(list.Repositories.Single(item => item.Id == 42).AlreadyImported);
        Assert.False(list.Repositories.Single(item => item.Id == 43).AlreadyImported);
    }

    [Fact]
    public async Task AnAppImportAndALaterConnectionOfTheSameRemote_ConvergeOnOneRepository()
    {
        await using (var context = _database.Open())
        {
            await UserService(context).ImportAsync(UserRequest(Repo(42, "acme/widgets")), "user-1");

            // The repository-wide worker has finished its first pass; until then a connect is refused as busy.
            (await context.Repositories.SingleAsync()).Status = RepositoryStatus.Completed;
            await context.SaveChangesAsync();
        }

        var protector = new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider());
        await using var connectContext = _database.Open();
        connectContext.GitConnections.Add(new GitConnection
        {
            Id = "c1", Provider = GitProvider.GitHub, NormalizedServerUrl = "https://github.com", ExternalAccountId = "1",
            DisplayName = "octocat", ProtectedToken = protector.Protect("ghp_token"), CreatedByUserId = "user-1"
        });
        await connectContext.SaveChangesAsync();
        var catalog = new FakeRemoteCatalog(GitProvider.GitHub);
        catalog.AddRepository("42", "acme/widgets", "Private", "main", "main", "dev");
        var service = new ConnectedRepositoryService(
            connectContext,
            new StubUser("user-1"),
            new GitConnectionAuthorizationService(new StubUser("user-1"), connectContext),
            protector,
            new GitProviderClientResolver([catalog]),
            new RepositoryGenerationLockService(connectContext),
            new BranchActionAuditor(connectContext, NullLogger<BranchActionAuditor>.Instance),
            NullLogger<ConnectedRepositoryService>.Instance);

        var response = await service.ConnectAsync(new ConnectRepositoryRequest("c1", "42", ["dev"], null), CancellationToken.None);

        Assert.False(response.RepositoryCreated);
        await using var verification = _database.Open();
        var repository = await verification.Repositories.SingleAsync();
        Assert.Equal("c1", repository.GitConnectionId);
        Assert.Equal(["dev", "main"], (await verification.RepositoryBranches.Select(item => item.BranchName).ToListAsync()).Order());
    }

    // ---- helpers ----

    private static UserGitHubImportService UserService(IContext context)
        => new(context, Mock.Of<IGitHubAppService>(), NullLogger<UserGitHubImportService>.Instance);

    private static AdminGitHubImportService AdminService(IContext context)
        => new(
            context,
            Mock.Of<IGitHubAppService>(),
            Mock.Of<IAdminSettingsService>(),
            Mock.Of<IHttpClientFactory>(),
            new GitHubAppCredentialCache(),
            new ConfigurationBuilder().Build(),
            Mock.Of<ILogger<AdminGitHubImportService>>());

    private static UserImportRequest UserRequest(params BatchImportRepo[] repos)
        => new() { InstallationId = 1, LanguageCode = "en", Repos = repos.ToList() };

    private static BatchImportRepo Repo(long? id, string fullName)
    {
        var parts = fullName.Split('/');
        return new BatchImportRepo
        {
            Id = id,
            FullName = fullName,
            Owner = parts[0],
            Name = parts[1],
            CloneUrl = $"https://github.com/{fullName}.git",
            DefaultBranch = "main"
        };
    }

    private sealed class StubUser(string userId) : OpenDeepWiki.Services.Auth.IUserContext
    {
        public string? UserId => userId;
        public string? UserName => userId;
        public string? Email => null;
        public bool IsAuthenticated => true;
        public System.Security.Claims.ClaimsPrincipal? User => new(new System.Security.Claims.ClaimsIdentity());
    }
}
