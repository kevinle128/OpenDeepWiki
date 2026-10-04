using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models.ConnectedRepositories;
using OpenDeepWiki.Postgresql;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Tests.Infrastructure;
using OpenDeepWiki.Tests.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

/// <summary>
/// Connect and add-branches orchestration on a real SQLite database: one repository per stable remote,
/// atomic multi-branch writes, independent full generation tasks, and idempotent repeats.
/// </summary>
public sealed class ConnectedRepositoryServiceTests : IDisposable
{
    private const string Pat = "ghp_CANARY_connected_repository_0123456789";

    private readonly SqliteScratchDatabase _database;
    private readonly FakeRemoteCatalog _github = new(GitProvider.GitHub);
    private readonly FakeRemoteCatalog _gitlab = new(GitProvider.GitLab);
    private readonly IGitConnectionSecretProtector _protector =
        new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider());
    private readonly ListLogger<ConnectedRepositoryService> _logger = new();

    public ConnectedRepositoryServiceTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
        _github.AddRepository("42", "acme/widgets", "Private", "main", "main", "dev", "release", "feature/x");
        _github.AddRepository("44", "acme/open", "Public", "main", "main", "dev");
    }

    public void Dispose() => _database.Dispose();

    // ---- identity and creation ----

    [Fact]
    public async Task ConnectAsync_CreatesOneRepositoryWithItsBranchesLanguagesTasksAndLocksInOneCommit()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        var response = await Service().ConnectAsync(Connect("c1", "42", "main", "dev", "release"), CancellationToken.None);

        Assert.True(response.RepositoryCreated);
        Assert.Equal("acme", response.OrgName);
        Assert.Equal("widgets", response.RepoName);
        Assert.Equal(["main", "dev", "release"], response.Branches.Select(item => item.BranchName));
        Assert.All(response.Branches, item =>
        {
            Assert.True(item.Created);
            Assert.NotNull(item.TaskId);
            Assert.Equal("Pending", item.TaskStatus);
        });

        await using var context = _database.Open();
        var repository = await context.Repositories.SingleAsync();
        Assert.Equal(GitProvider.GitHub, repository.Provider);
        Assert.Equal("https://github.com", repository.ProviderBaseUrl);
        Assert.Equal("42", repository.ProviderRepositoryId);
        Assert.Equal("https://github.com/acme/widgets.git", repository.GitUrl);
        Assert.Equal("c1", repository.GitConnectionId);
        Assert.Equal("user-1", repository.OwnerUserId);
        Assert.Equal("main", repository.DefaultBranch);
        Assert.False(repository.IsPublic);
        Assert.Null(repository.AuthPassword);
        Assert.Null(repository.AuthAccount);
        Assert.Equal(RepositoryStatus.Completed, repository.Status);

        Assert.Equal(3, await context.RepositoryBranches.CountAsync());
        Assert.Equal(3, await context.BranchLanguages.CountAsync(item => item.LanguageCode == "en"));
        var tasks = await context.BranchGenerationTasks.ToListAsync();
        Assert.Equal(3, tasks.Count);
        Assert.All(tasks, task =>
        {
            Assert.Equal(BranchGenerationTaskStatus.Pending, task.Status);
            Assert.Equal("user-1", task.RequestedBy);
        });
        var locks = await context.RepositoryGenerationLocks.ToListAsync();
        Assert.Equal(3, locks.Count);
        Assert.All(locks, item =>
        {
            Assert.Equal(RepositoryGenerationLockScope.Branch, item.Scope);
            Assert.NotNull(item.BranchId);
        });
        var events = await context.GitConnectionAuditEvents.ToListAsync();
        Assert.Contains(events, item => item.EventType == GitConnectionAuditEventType.RepositoryAssigned && item.ActorUserId == "user-1");
        Assert.Equal(3, events.Count(item => item.EventType == GitConnectionAuditEventType.BranchAdded));
    }

    [Fact]
    public async Task ConnectAsync_ForABranchWithASlash_StoresTheBranchName()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        var response = await Service().ConnectAsync(Connect("c1", "42", "feature/x"), CancellationToken.None);

        Assert.Equal("feature/x", Assert.Single(response.Branches).BranchName);
    }

    [Theory]
    [InlineData("Public", true)]
    [InlineData("Private", false)]
    [InlineData("Internal", false)]
    public async Task ConnectAsync_StoresVisibilityFromTheProvider(string visibility, bool expectedPublic)
    {
        _github.AddRepository("43", "acme/open", visibility, "main", "main");
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        await Service().ConnectAsync(Connect("c1", "43", "main"), CancellationToken.None);

        await using var context = _database.Open();
        Assert.Equal(expectedPublic, (await context.Repositories.SingleAsync()).IsPublic);
    }

    [Fact]
    public async Task ConnectAsync_WhenASecondConnectionSelectsTheSameRemote_KeepsOneRepositoryAndFails()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedConnectionAsync("c2", GitProvider.GitHub, "https://github.com", "1002", createdBy: "user-2");

        var first = await Service("user-1").ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);
        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service("user-2").ConnectAsync(Connect("c2", "42", "dev"), CancellationToken.None));

        Assert.True(first.RepositoryCreated);
        Assert.Equal(ConnectedRepositoryErrorCodes.AlreadyConnected, exception.ErrorCode);
        await using var context = _database.Open();
        var repository = await context.Repositories.SingleAsync();
        Assert.Equal("c1", repository.GitConnectionId);
        Assert.Equal("user-1", repository.OwnerUserId);
        Assert.Equal(1, await context.RepositoryBranches.CountAsync());
    }

    [Fact]
    public async Task ConnectAsync_WhenTheRemoteWasRenamed_KeepsTheRepositoryAndRefreshesMetadata()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var first = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);
        _github.AddRepository("42", "acme-labs/widgets-pro", "Public", "trunk", "main", "dev");

        var second = await Service().ConnectAsync(Connect("c1", "42", "dev"), CancellationToken.None);

        Assert.Equal(first.RepositoryId, second.RepositoryId);
        Assert.Equal("acme-labs", second.OrgName);
        Assert.Equal("widgets-pro", second.RepoName);
        await using var context = _database.Open();
        var repository = await context.Repositories.SingleAsync();
        Assert.Equal("https://github.com/acme-labs/widgets-pro.git", repository.GitUrl);
        Assert.Equal("trunk", repository.DefaultBranch);
        Assert.Equal(2, await context.RepositoryBranches.CountAsync());
    }

    [Fact]
    public async Task ConnectAsync_WhenGitHubAndGitLabBothContainTheSamePath_KeepsTwoRepositoriesWithDistinctSlugs()
    {
        _gitlab.AddRepository("7", "acme/widgets", "Private", "main", "main");
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedConnectionAsync("c2", GitProvider.GitLab, "https://gitlab.com", "2001");

        var github = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);
        var gitlab = await Service().ConnectAsync(Connect("c2", "7", "main"), CancellationToken.None);

        Assert.NotEqual(github.RepositoryId, gitlab.RepositoryId);
        await using var context = _database.Open();
        var repositories = await context.Repositories.OrderBy(item => item.CreatedAt).ToListAsync();
        Assert.Equal(2, repositories.Count);
        Assert.Equal(("acme", "widgets"), (repositories[0].OrgName, repositories[0].RepoName));
        Assert.Equal("acme~gitlab", repositories[1].OrgName);
        Assert.Equal("widgets", repositories[1].RepoName);
        Assert.Equal("https://gitlab.com/acme/widgets.git", repositories[1].GitUrl);
    }

    [Fact]
    public async Task ConnectAsync_WhenSeveralServersShareThePath_DerivesFurtherSlugs()
    {
        _gitlab.AddRepository("7", "acme/widgets", "Private", "main", "main");
        _gitlab.AddRepository("8", "acme/widgets", "Private", "main", "main");
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedConnectionAsync("c2", GitProvider.GitLab, "https://gitlab.com", "2001");
        await SeedConnectionAsync("c3", GitProvider.GitLab, "https://git.corp.example", "3001");

        await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);
        await Service().ConnectAsync(Connect("c2", "7", "main"), CancellationToken.None);
        await Service().ConnectAsync(Connect("c3", "8", "main"), CancellationToken.None);

        await using var context = _database.Open();
        var names = await context.Repositories.Select(item => item.OrgName).ToListAsync();
        Assert.Equal(["acme", "acme~gitlab", "acme~gitlab~2"], names.Order());
    }

    [Fact]
    public async Task ConnectAsync_ForAGitLabSubgroup_JoinsTheNamespaceIntoOneRouteSegment()
    {
        _gitlab.AddRepository("9", "group/sub/project", "Private", "main", "main");
        await SeedConnectionAsync("c2", GitProvider.GitLab, "https://gitlab.com", "2001");

        await Service().ConnectAsync(Connect("c2", "9", "main"), CancellationToken.None);

        await using var context = _database.Open();
        var repository = await context.Repositories.SingleAsync();
        Assert.Equal("group_sub", repository.OrgName);
        Assert.Equal("project", repository.RepoName);
        Assert.Equal("https://gitlab.com/group/sub/project.git", repository.GitUrl);
    }

    // ---- atomicity and idempotency ----

    [Fact]
    public async Task ConnectAsync_WhenOneSelectedBranchDoesNotExist_WritesNothing()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(Connect("c1", "42", "main", "ghost", "dev"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.RemoteBranchNotFound, exception.ErrorCode);
        Assert.Equal(["ghost"], exception.Branches);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ConnectAsync_WhenANewBranchCannotGetItsLock_RollsBackTheWholeRequest()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var first = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);
        await using (var context = _database.Open())
        {
            // A repository-scope lock (for example a repository regeneration) excludes new branch locks.
            context.RepositoryGenerationLocks.RemoveRange(await context.RepositoryGenerationLocks.ToListAsync());
            context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
            {
                Id = "repo-lock",
                RepositoryId = first.RepositoryId,
                OwnerType = RepositoryGenerationLockOwnerType.Repository,
                OwnerId = first.RepositoryId,
                Scope = RepositoryGenerationLockScope.Repository
            });
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().AddIndexedBranchesAsync(first.RepositoryId, Add("dev", "release"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.GenerationLockConflict, exception.ErrorCode);
        await using var verification = _database.Open();
        Assert.Equal(["main"], await verification.RepositoryBranches.Select(item => item.BranchName).ToListAsync());
        Assert.Equal(1, await verification.BranchGenerationTasks.CountAsync());
        Assert.Equal(["repo-lock"], await verification.RepositoryGenerationLocks.Select(item => item.Id).ToListAsync());
    }

    [Fact]
    public async Task AddIndexedBranchesAsync_WithExistingAndNewBranches_QueuesOnlyTheNewOnes()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var first = await Service().ConnectAsync(Connect("c1", "44", "main"), CancellationToken.None);

        var second = await Service("user-2").AddIndexedBranchesAsync(first.RepositoryId, Add("main", "dev"), CancellationToken.None);

        Assert.False(second.Branches.Single(item => item.BranchName == "main").Created);
        Assert.Equal(first.Branches[0].TaskId, second.Branches.Single(item => item.BranchName == "main").TaskId);
        Assert.True(second.Branches.Single(item => item.BranchName == "dev").Created);
        var newTaskId = second.Branches.Single(item => item.Created).TaskId;
        await using var context = _database.Open();
        Assert.Equal(2, await context.BranchGenerationTasks.CountAsync());
        Assert.Equal("user-2", (await context.BranchGenerationTasks.SingleAsync(item => item.Id == newTaskId)).RequestedBy);
    }

    [Fact]
    public async Task ConnectAsync_RepeatedWithTheSameBranches_ChangesNothing()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var first = await Service().ConnectAsync(Connect("c1", "42", "main", "dev"), CancellationToken.None);

        var second = await Service().ConnectAsync(Connect("c1", "42", "dev", "main"), CancellationToken.None);

        Assert.Equal(first.RepositoryId, second.RepositoryId);
        Assert.False(second.RepositoryCreated);
        Assert.All(second.Branches, item => Assert.False(item.Created));
        await using var context = _database.Open();
        Assert.Equal(1, await context.Repositories.CountAsync());
        Assert.Equal(2, await context.RepositoryBranches.CountAsync());
        Assert.Equal(2, await context.BranchGenerationTasks.CountAsync());
        Assert.Equal(2, await context.RepositoryGenerationLocks.CountAsync());
    }

    [Fact]
    public async Task ConnectAsync_WithDuplicateNamesInTheRequest_CollapsesThem()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        var response = await Service().ConnectAsync(Connect("c1", "42", "main", " main ", "main"), CancellationToken.None);

        Assert.Equal(["main"], response.Branches.Select(item => item.BranchName));
    }

    [Fact]
    public async Task ConnectAsync_WhenManyRequestsRaceForOneRemote_KeepsOneRepositoryAndOneTaskPerBranch()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedConnectionAsync("c2", GitProvider.GitHub, "https://github.com", "1002", createdBy: "user-2");
        var gate = new TaskCompletionSource();

        var attempts = new[]
        {
            RunAfterGateAsync(gate, "user-1", Connect("c1", "42", "main", "dev")),
            RunAfterGateAsync(gate, "user-1", Connect("c1", "42", "main", "release")),
            RunAfterGateAsync(gate, "user-1", Connect("c1", "42", "dev", "release")),
            RunAfterGateAsync(gate, "user-1", Connect("c1", "42", "main"))
        };
        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        await using var context = _database.Open();
        Assert.Equal(1, await context.Repositories.CountAsync());
        Assert.Equal(["dev", "main", "release"], (await context.RepositoryBranches.Select(item => item.BranchName).ToListAsync()).Order());
        Assert.Equal(3, await context.BranchGenerationTasks.CountAsync());
        Assert.Equal(3, await context.RepositoryGenerationLocks.CountAsync());
        Assert.Single(responses.Select(item => item.RepositoryId).Distinct());
        Assert.Equal(1, responses.Count(item => item.RepositoryCreated));
    }

    [PostgresFact]
    public async Task Postgres_ConnectAsync_WhenManyRequestsRaceForOneRemote_KeepsOneRepositoryAndOneTaskPerBranch()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        PostgresqlDbContext Open() => new(new DbContextOptionsBuilder<PostgresqlDbContext>().UseNpgsql(database.ConnectionString).Options);
        await using (var seed = Open())
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Users.AddRange(
                new User { Id = "user-1", Name = "user-1", Email = "user-1@example.com" },
                new User { Id = "user-2", Name = "user-2", Email = "user-2@example.com" });
            foreach (var (id, account, user) in new[] { ("c1", "1001", "user-1"), ("c2", "1002", "user-2") })
            {
                seed.GitConnections.Add(new GitConnection
                {
                    Id = id, Provider = GitProvider.GitHub, NormalizedServerUrl = "https://github.com", ExternalAccountId = account,
                    DisplayName = id, ProtectedToken = _protector.Protect(Pat), CreatedByUserId = user
                });
            }

            await seed.SaveChangesAsync();
        }

        var gate = new TaskCompletionSource();
        async Task<ConnectedRepositoryResponse> RunAsync(string user, ConnectRepositoryRequest request)
        {
            await gate.Task;
            return await Service(user, () => Open()).ConnectAsync(request, CancellationToken.None);
        }

        var attempts = new[]
        {
            RunAsync("user-1", Connect("c1", "42", "main", "dev")),
            RunAsync("user-1", Connect("c1", "42", "main", "release")),
            RunAsync("user-1", Connect("c1", "42", "dev", "release")),
            RunAsync("user-1", Connect("c1", "42", "main")),
            RunAsync("user-1", Connect("c1", "42", "main", "dev", "release"))
        };
        gate.SetResult();
        var responses = await Task.WhenAll(attempts);

        await using var context = Open();
        Assert.Equal(1, await context.Repositories.CountAsync());
        Assert.Equal(["dev", "main", "release"], (await context.RepositoryBranches.Select(item => item.BranchName).ToListAsync()).Order());
        Assert.Equal(3, await context.BranchGenerationTasks.CountAsync());
        Assert.Equal(3, await context.RepositoryGenerationLocks.CountAsync());
        Assert.Single(responses.Select(item => item.RepositoryId).Distinct());
    }

    // ---- connection state is checked inside the write ----

    [Fact]
    public async Task ConnectAsync_WhenTheConnectionIsDisabledAfterValidation_FailsInsideTheTransactionAndWritesNothing()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        _github.OnGetRepository = async () =>
        {
            await using var context = _database.Open();
            var connection = await context.GitConnections.SingleAsync();
            connection.IsEnabled = false;
            await context.SaveChangesAsync();
        };

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.ConnectionDisabled, exception.ErrorCode);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ConnectAsync_WhenTheConnectionIsDeletedAfterValidation_FailsAndWritesNothing()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        _github.OnGetRepository = async () =>
        {
            await using var context = _database.Open();
            var connection = await context.GitConnections.SingleAsync();
            connection.MarkAsDeleted();
            connection.ProtectedToken = string.Empty;
            await context.SaveChangesAsync();
        };

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.ConnectionNotFound, exception.ErrorCode);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ConnectAsync_ChangesTheConnectionStampSoAConcurrentDeleteCannotWin()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        string stampBefore;
        await using (var context = _database.Open())
        {
            stampBefore = (await context.GitConnections.SingleAsync()).ConcurrencyStamp;
        }

        await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        await using var verification = _database.Open();
        Assert.NotEqual(stampBefore, (await verification.GitConnections.SingleAsync()).ConcurrencyStamp);
    }

    [Fact]
    public async Task ADeleteThatReadTheConnectionBeforeAConnectCommitted_FailsItsStampCheck()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await using var deleting = _database.Open();
        // The delete service reads the connection and counts its repositories (none yet) before it saves.
        var staleCopy = await deleting.GitConnections.SingleAsync();

        await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        staleCopy.MarkAsDeleted();
        staleCopy.ProtectedToken = string.Empty;
        staleCopy.ConcurrencyStamp = Guid.NewGuid().ToString();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => deleting.SaveChangesAsync());
        await using var verification = _database.Open();
        Assert.False((await verification.GitConnections.SingleAsync()).IsDeleted);
        Assert.Equal("c1", (await verification.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task ConnectAsync_WhenTheConnectionIsDisabled_FailsBeforeCallingTheProvider()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001", enabled: false);

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.ConnectionDisabled, exception.ErrorCode);
        Assert.Equal(0, _github.RepositoryCalls);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task ConnectAsync_WhenTheConnectionDoesNotExist_Fails()
    {
        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(Connect("missing", "42", "main"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.ConnectionNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task ConnectAsync_WhenTheProviderFails_WritesNothingAndKeepsTheStableCode()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        _github.Failure = new GitProviderException(GitProviderErrorCodes.RateLimited, TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAsync<GitProviderException>(
            () => Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.RateLimited, exception.Code);
        await AssertNothingWrittenAsync();
    }

    // ---- existing repositories ----

    [Fact]
    public async Task ConnectAsync_ForARepositoryOfAnotherConnection_FailsAndChangesNothing()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedConnectionAsync("c2", GitProvider.GitHub, "https://github.com", "1002", createdBy: "user-2");
        await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);
        Repository before;
        await using (var context = _database.Open())
        {
            before = await context.Repositories.AsNoTracking().SingleAsync();
        }

        // The provider data of the second connection differs, so an overwrite would show.
        _github.AddRepository("42", "acme/widgets", "Public", "other", "main", "dev");
        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service("user-2").ConnectAsync(Connect("c2", "42", "dev"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.AlreadyConnected, exception.ErrorCode);
        await using var verification = _database.Open();
        var after = await verification.Repositories.AsNoTracking().SingleAsync();
        Assert.Equal("c1", after.GitConnectionId);
        Assert.Equal(before.GitUrl, after.GitUrl);
        Assert.Equal(before.ProviderRepositoryId, after.ProviderRepositoryId);
        Assert.Equal(before.IsPublic, after.IsPublic);
        Assert.Equal(before.OrgName, after.OrgName);
        Assert.Equal(before.RepoName, after.RepoName);
        Assert.Equal(before.DefaultBranch, after.DefaultBranch);
        Assert.Equal(["main"], await verification.RepositoryBranches.Select(item => item.BranchName).ToListAsync());
        Assert.Single(await verification.BranchGenerationTasks.ToListAsync());
    }

    [Fact]
    public async Task ConnectAsync_ForALegacyRowOfAnotherConnection_FailsAndChangesNothing()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedConnectionAsync("c2", GitProvider.GitHub, "https://github.com", "1002", createdBy: "user-2");
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: "https://github.com/acme/widgets.git",
            provider: GitProvider.GitHub, baseUrl: "https://github.com", connectionId: "c1");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service("user-2").ConnectAsync(Connect("c2", "42", "dev"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.AlreadyConnected, exception.ErrorCode);
        await using var verification = _database.Open();
        var repository = await verification.Repositories.SingleAsync();
        Assert.Equal("c1", repository.GitConnectionId);
        Assert.Null(repository.ProviderRepositoryId);
        Assert.Empty(await verification.BranchGenerationTasks.ToListAsync());
    }

    [Fact]
    public async Task ConnectAsync_ForARepositoryOfTheSameConnection_QueuesTheNewBranch()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        var response = await Service().ConnectAsync(Connect("c1", "42", "dev"), CancellationToken.None);

        Assert.False(response.RepositoryCreated);
        await using var verification = _database.Open();
        Assert.Equal("c1", (await verification.Repositories.SingleAsync()).GitConnectionId);
        Assert.Equal(2, await verification.BranchGenerationTasks.CountAsync());
    }

    [Fact]
    public async Task ConnectAsync_ForARepositoryWhoseConnectionWasDeleted_AttachesTheNewConnection()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedConnectionAsync("c2", GitProvider.GitHub, "https://github.com", "1002", createdBy: "user-2");
        await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);
        await using (var context = _database.Open())
        {
            (await context.GitConnections.SingleAsync(item => item.Id == "c1")).MarkAsDeleted();
            await context.SaveChangesAsync();
        }

        await Service("user-2").ConnectAsync(Connect("c2", "42", "dev"), CancellationToken.None);

        await using var verification = _database.Open();
        Assert.Equal("c2", (await verification.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task ConnectAsync_ForARepositoryWhoseConnectionIsDisabled_BlocksNewIndexingEvenThroughAnotherConnection()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedConnectionAsync("c2", GitProvider.GitHub, "https://github.com", "1002", createdBy: "user-2");
        await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);
        await using (var context = _database.Open())
        {
            (await context.GitConnections.SingleAsync(item => item.Id == "c1")).IsEnabled = false;
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service("user-2").ConnectAsync(Connect("c2", "42", "dev"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.ConnectionDisabled, exception.ErrorCode);
        await using var verification = _database.Open();
        Assert.Equal(["main"], await verification.RepositoryBranches.Select(item => item.BranchName).ToListAsync());
    }

    [Fact]
    public async Task ConnectAsync_ForARepositoryFromAnAppImport_AttachesTheConnectionAndKeepsTheRow()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedRepositoryAsync("app-import", "acme", "widgets", gitUrl: "https://github.com/acme/widgets.git",
            provider: GitProvider.GitHub, baseUrl: "https://github.com", remoteId: "42", connectionId: null);

        var response = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        Assert.False(response.RepositoryCreated);
        Assert.Equal("app-import", response.RepositoryId);
        await using var context = _database.Open();
        Assert.Equal("c1", (await context.Repositories.SingleAsync()).GitConnectionId);
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets.git")]
    [InlineData("https://github.com/acme/widgets")]
    [InlineData("https://GitHub.com/Acme/Widgets.git/")]
    public async Task ConnectAsync_ForALegacyRepositoryOfTheSameRemote_AdoptsItInsteadOfCreatingASibling(string legacyUrl)
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: legacyUrl);

        var response = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        Assert.False(response.RepositoryCreated);
        Assert.Equal("legacy", response.RepositoryId);
        await using var context = _database.Open();
        var repository = await context.Repositories.SingleAsync();
        Assert.Equal("42", repository.ProviderRepositoryId);
        Assert.Equal("c1", repository.GitConnectionId);
        Assert.Equal("https://github.com", repository.ProviderBaseUrl);
    }

    [Fact]
    public async Task ConnectAsync_ForALegacyRepositoryWithCredentialsInItsUrl_DoesNotAdoptIt()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedRepositoryAsync("legacy", "other", "legacy", gitUrl: "https://user:secret@github.com/acme/widgets.git");

        var response = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        Assert.True(response.RepositoryCreated);
        await using var context = _database.Open();
        Assert.Equal(2, await context.Repositories.CountAsync());
        Assert.Null((await context.Repositories.SingleAsync(item => item.Id == "legacy")).ProviderRepositoryId);
    }

    [Fact]
    public async Task ConnectAsync_ForDuplicateLegacyRowsOfOneRemote_AdoptsTheAssignedRowFirstThenTheEarliest()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        // Insertion order is the reverse of the preference on purpose.
        await SeedRepositoryAsync("dup-plain-late", "mirror-a", "widgets", gitUrl: "https://github.com/acme/widgets.git");
        await SeedRepositoryAsync("dup-plain-early", "mirror-b", "widgets", gitUrl: "https://github.com/acme/widgets.git");
        await SeedRepositoryAsync("dup-assigned", "mirror-c", "widgets", gitUrl: "https://github.com/acme/widgets.git",
            connectionId: "c1");
        await using (var context = _database.Open())
        {
            var day = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            (await context.Repositories.SingleAsync(item => item.Id == "dup-plain-late")).CreatedAt = day.AddDays(1);
            (await context.Repositories.SingleAsync(item => item.Id == "dup-plain-early")).CreatedAt = day;
            (await context.Repositories.SingleAsync(item => item.Id == "dup-assigned")).CreatedAt = day.AddDays(2);
            await context.SaveChangesAsync();
        }

        var response = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        Assert.Equal("dup-assigned", response.RepositoryId);

        await using (var context = _database.Open())
        {
            (await context.Repositories.SingleAsync(item => item.Id == "dup-assigned")).MarkAsDeleted();
            await context.SaveChangesAsync();
        }

        _github.AddRepository("43", "acme/widgets", "Private", "main", "main");
        var second = await Service().ConnectAsync(Connect("c1", "43", "main"), CancellationToken.None);
        Assert.Equal("dup-plain-early", second.RepositoryId);
    }

    // ---- re-adding removed branches ----

    [Fact]
    public async Task AddIndexedBranchesAsync_ForASoftDeletedBranch_RestoresItWithoutAUniqueKeyFailure()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var first = await Service().ConnectAsync(Connect("c1", "42", "main", "dev"), CancellationToken.None);
        await using (var context = _database.Open())
        {
            // An earlier cleanup soft-deleted the branch together with its language and task rows.
            var branch = await context.RepositoryBranches.SingleAsync(item => item.BranchName == "dev");
            branch.MarkAsDeleted();
            branch.GenerationStatus = BranchGenerationTaskStatus.Failed;
            branch.LastCommitId = "stale-commit";
            (await context.BranchLanguages.SingleAsync(item => item.RepositoryBranchId == branch.Id)).MarkAsDeleted();
            var staleTask = await context.BranchGenerationTasks.SingleAsync(item => item.BranchId == branch.Id);
            staleTask.Status = BranchGenerationTaskStatus.Cancelled;
            context.RepositoryGenerationLocks.RemoveRange(
                await context.RepositoryGenerationLocks.Where(item => item.BranchId == branch.Id).ToListAsync());
            await context.SaveChangesAsync();
        }

        var again = await Service().AddIndexedBranchesAsync(first.RepositoryId, Add("dev"), CancellationToken.None);

        var result = Assert.Single(again.Branches);
        Assert.True(result.Created);
        await using var verification = _database.Open();
        var restored = await verification.RepositoryBranches.SingleAsync(item => item.BranchName == "dev");
        Assert.False(restored.IsDeleted);
        Assert.Null(restored.LastCommitId);
        Assert.Equal(BranchGenerationTaskStatus.Pending, restored.GenerationStatus);
        Assert.Equal(1, await verification.BranchLanguages.CountAsync(item => item.RepositoryBranchId == restored.Id && !item.IsDeleted));
        Assert.Equal(1, await verification.BranchGenerationTasks.CountAsync(item => item.BranchId == restored.Id && item.Status == BranchGenerationTaskStatus.Pending));
    }

    // ---- add-branches guards ----

    [Fact]
    public async Task AddIndexedBranchesAsync_ForAnUnknownRepository_Fails()
    {
        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().AddIndexedBranchesAsync("missing", Add("main"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.RepositoryNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task AddIndexedBranchesAsync_ForARepositoryWithoutAConnection_Fails()
    {
        await SeedRepositoryAsync("legacy", "acme", "legacy", gitUrl: "https://example.com/acme/legacy.git");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().AddIndexedBranchesAsync("legacy", Add("main"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.RepositoryNotConnected, exception.ErrorCode);
    }

    [Fact]
    public async Task AddIndexedBranchesAsync_WhenTheRepositoryConnectionIsDisabled_Fails()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var first = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);
        await using (var context = _database.Open())
        {
            (await context.GitConnections.SingleAsync()).IsEnabled = false;
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().AddIndexedBranchesAsync(first.RepositoryId, Add("dev"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.ConnectionDisabled, exception.ErrorCode);
    }

    // ---- validation ----

    [Fact]
    public async Task Requests_WhenAnonymous_AreRejectedBeforeAnyProviderCall()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var service = Service(userId: null);

        var connect = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => service.ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None));
        var add = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => service.AddIndexedBranchesAsync("r", Add("main"), CancellationToken.None));
        var list = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => service.ListIndexedBranchesAsync("r", CancellationToken.None));

        Assert.All([connect, add, list], item => Assert.Equal(ConnectedRepositoryErrorCodes.Unauthorized, item.ErrorCode));
        Assert.Equal(0, _github.RepositoryCalls);
    }

    [Fact]
    public async Task ConnectAsync_WithoutBranches_Fails()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        var empty = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(new ConnectRepositoryRequest("c1", "42", [], null), CancellationToken.None));
        var nothing = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(new ConnectRepositoryRequest("c1", "42", null, null), CancellationToken.None));
        var blanks = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(Connect("c1", "42", " ", ""), CancellationToken.None));

        Assert.All([empty, nothing, blanks], item => Assert.Equal(ConnectedRepositoryErrorCodes.NoBranchesSelected, item.ErrorCode));
    }

    [Fact]
    public async Task ConnectAsync_WithTooManyBranches_Fails()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var names = Enumerable.Range(0, 51).Select(index => $"branch-{index}").ToArray();

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(Connect("c1", "42", names), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.TooManyBranches, exception.ErrorCode);
        Assert.Equal(0, _github.RepositoryCalls);
    }

    [Theory]
    [InlineData("bad\nname")]
    [InlineData("a..b")]
    [InlineData("-leading-dash")]
    [InlineData("tab\tname")]
    [InlineData("back\\slash")]
    [InlineData("end/")]
    public async Task ConnectAsync_WithAnUnsafeBranchName_FailsBeforeAnyProviderCall(string name)
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(Connect("c1", "42", name), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.InvalidBranchName, exception.ErrorCode);
        Assert.Equal(0, _github.RepositoryCalls);
    }

    [Theory]
    [InlineData(null, "42")]
    [InlineData("", "42")]
    [InlineData("c1", null)]
    [InlineData("c1", " ")]
    public async Task ConnectAsync_WithoutConnectionOrRemoteId_Fails(string? connectionId, string? remoteId)
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(new ConnectRepositoryRequest(connectionId, remoteId, ["main"], null), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.InvalidRequest, exception.ErrorCode);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("en", "en")]
    [InlineData("vi", "vi")]
    [InlineData("zh", "zh")]
    public async Task ConnectAsync_UsesTheRequestedLanguageOrEnglish(string? requested, string expected)
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        await Service().ConnectAsync(new ConnectRepositoryRequest("c1", "42", ["main"], requested), CancellationToken.None);

        await using var context = _database.Open();
        Assert.Equal(expected, (await context.BranchLanguages.SingleAsync()).LanguageCode);
    }

    [Fact]
    public async Task ConnectAsync_WithoutASkillChoice_GeneratesTheSkillLikeASubmit()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        await using var context = _database.Open();
        Assert.True((await context.Repositories.SingleAsync()).GenerateSkill);
    }

    [Fact]
    public async Task ConnectAsync_WithSkillGenerationOff_StoresItOnTheNewRepository()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        await Service().ConnectAsync(
            new ConnectRepositoryRequest("c1", "42", ["main"], null, GenerateSkill: false), CancellationToken.None);

        await using var context = _database.Open();
        Assert.False((await context.Repositories.SingleAsync()).GenerateSkill);
    }

    [Fact]
    public async Task ConnectAsync_ForARepositoryThatExists_KeepsItsSkillSetting()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await Service().ConnectAsync(
            new ConnectRepositoryRequest("c1", "42", ["main"], null, GenerateSkill: false), CancellationToken.None);

        await Service().ConnectAsync(
            new ConnectRepositoryRequest("c1", "42", ["dev"], null, GenerateSkill: true), CancellationToken.None);

        await using var context = _database.Open();
        Assert.False((await context.Repositories.SingleAsync()).GenerateSkill);
    }

    [Fact]
    public async Task ConnectAsync_WithAMalformedLanguageCode_Fails()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(new ConnectRepositoryRequest("c1", "42", ["main"], "zh; DROP"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.InvalidRequest, exception.ErrorCode);
    }

    [Fact]
    public async Task ConnectAsync_WhenTheProviderReturnsAnUnsafeRepositoryPath_FailsAndWritesNothing()
    {
        _github.AddRepository("66", "acme/../etc", "Private", "main", "main");
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ConnectAsync(Connect("c1", "66", "main"), CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.RemoteInvalid, exception.ErrorCode);
        await AssertNothingWrittenAsync();
    }

    // ---- secrets ----

    [Fact]
    public async Task NoCredentialReachesTheDatabaseAuditEventsOrLogs()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");

        await Service().ConnectAsync(Connect("c1", "42", "main", "dev"), CancellationToken.None);

        Assert.DoesNotContain(_logger.Lines, line => line.Contains(Pat));
        await using var context = _database.Open();
        var repository = await context.Repositories.SingleAsync();
        Assert.DoesNotContain(Pat, repository.GitUrl);
        Assert.Null(repository.AuthPassword);
        var serialized = string.Join(
            "|",
            (await context.GitConnectionAuditEvents.ToListAsync()).Select(item => $"{item.EventType}{item.ErrorCode}{item.CorrelationId}"));
        Assert.DoesNotContain(Pat, serialized);
        Assert.Contains(Pat, _github.TokensSeen);
    }

    // ---- listing ----

    [Fact]
    public async Task ListAndAddIndexedBranches_ForAPrivateConnectedRepositoryOfAnotherUser_AreAllowed()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var response = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        var list = await Service("user-2").ListIndexedBranchesAsync(response.RepositoryId, CancellationToken.None);
        var add = await Service("user-2").AddIndexedBranchesAsync(response.RepositoryId, Add("dev"), CancellationToken.None);
        Assert.Single(list);
        Assert.Equal("dev", Assert.Single(add.Branches).BranchName);
        Assert.Equal(2, (await Service().ListIndexedBranchesAsync(response.RepositoryId, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ListIndexedBranchesAsync_ReturnsBranchesWithTheirActiveTask()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        var response = await Service().ConnectAsync(Connect("c1", "44", "main", "dev"), CancellationToken.None);

        var branches = await Service("user-2").ListIndexedBranchesAsync(response.RepositoryId, CancellationToken.None);

        Assert.Equal(["dev", "main"], branches.Select(item => item.BranchName).Order());
        Assert.All(branches, item =>
        {
            Assert.Equal(["en"], item.Languages);
            Assert.NotNull(item.ActiveTaskId);
            Assert.Equal("Full", item.ActiveTaskKind);
        });
    }

    [Fact]
    public async Task ListIndexedBranchesAsync_ForAnUnknownRepository_Fails()
    {
        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().ListIndexedBranchesAsync("missing", CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.RepositoryNotFound, exception.ErrorCode);
    }

    // ---- legacy repository assignment ----

    [Fact]
    public async Task AdoptLegacyRepositoryAsync_LinksTheRepositoryToTheConnectionWithoutBranchesTasksOrLocks()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: "https://github.com/acme/widgets.git");

        await Service().AdoptLegacyRepositoryAsync("legacy", "c1", CancellationToken.None);

        await using var context = _database.Open();
        var repository = await context.Repositories.SingleAsync();
        Assert.Equal("c1", repository.GitConnectionId);
        Assert.Equal(GitProvider.GitHub, repository.Provider);
        Assert.Equal("https://github.com", repository.ProviderBaseUrl);
        Assert.Null(repository.ProviderRepositoryId);
        Assert.Equal("acme", repository.OrgName);
        Assert.Equal("https://github.com/acme/widgets.git", repository.GitUrl);
        Assert.Empty(await context.RepositoryBranches.ToListAsync());
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
        Assert.Empty(await context.RepositoryGenerationLocks.ToListAsync());
        var audit = Assert.Single(await context.GitConnectionAuditEvents.ToListAsync());
        Assert.Equal(GitConnectionAuditEventType.RepositoryAssigned, audit.EventType);
        Assert.Equal("user-1", audit.ActorUserId);
        Assert.Equal("legacy", audit.RepositoryId);
    }

    [Fact]
    public async Task AdoptLegacyRepositoryAsync_RepeatedForTheSameConnection_ChangesNothing()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: "https://github.com/acme/widgets.git");
        await Service().AdoptLegacyRepositoryAsync("legacy", "c1", CancellationToken.None);

        await Service().AdoptLegacyRepositoryAsync("legacy", "c1", CancellationToken.None);

        await using var context = _database.Open();
        Assert.Single(await context.GitConnectionAuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData("https://gitlab.com/acme/widgets.git", ConnectedRepositoryErrorCodes.RemoteInvalid)]
    [InlineData("http://github.com/acme/widgets.git", ConnectedRepositoryErrorCodes.RemoteInvalid)]
    [InlineData("https://user:secret@github.com/acme/widgets.git", ConnectedRepositoryErrorCodes.RemoteInvalid)]
    public async Task AdoptLegacyRepositoryAsync_ForAnotherOriginOrAUrlWithUserInfo_FailsAndLinksNothing(string url, string code)
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: url);

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().AdoptLegacyRepositoryAsync("legacy", "c1", CancellationToken.None));

        Assert.Equal(code, exception.ErrorCode);
        Assert.DoesNotContain("secret", exception.ToString());
        await using var context = _database.Open();
        Assert.Null((await context.Repositories.SingleAsync()).GitConnectionId);
        Assert.Empty(await context.GitConnectionAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task AdoptLegacyRepositoryAsync_ForADisabledConnection_FailsAndLinksNothing()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001", enabled: false);
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: "https://github.com/acme/widgets.git");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().AdoptLegacyRepositoryAsync("legacy", "c1", CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.ConnectionDisabled, exception.ErrorCode);
        await using var context = _database.Open();
        Assert.Null((await context.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task AdoptLegacyRepositoryAsync_ForARepositoryThatUsesAnotherConnection_KeepsItsConnection()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedConnectionAsync("c2", GitProvider.GitHub, "https://github.com", "1002");
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: "https://github.com/acme/widgets.git",
            provider: GitProvider.GitHub, baseUrl: "https://github.com", connectionId: "c2");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().AdoptLegacyRepositoryAsync("legacy", "c1", CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.AlreadyConnected, exception.ErrorCode);
        await using var context = _database.Open();
        Assert.Equal("c2", (await context.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task AdoptLegacyRepositoryAsync_ForAMissingConnectionOrRepository_Fails()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: "https://github.com/acme/widgets.git");

        var connection = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().AdoptLegacyRepositoryAsync("legacy", "missing", CancellationToken.None));
        var repository = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service().AdoptLegacyRepositoryAsync("missing", "c1", CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.ConnectionNotFound, connection.ErrorCode);
        Assert.Equal(ConnectedRepositoryErrorCodes.RepositoryNotFound, repository.ErrorCode);
    }

    [Fact]
    public async Task AdoptLegacyRepositoryAsync_ForASignedOutCaller_Fails()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: "https://github.com/acme/widgets.git");

        var exception = await Assert.ThrowsAsync<ConnectedRepositoryException>(
            () => Service(userId: null).AdoptLegacyRepositoryAsync("legacy", "c1", CancellationToken.None));

        Assert.Equal(ConnectedRepositoryErrorCodes.Unauthorized, exception.ErrorCode);
    }

    [Fact]
    public async Task ConnectAsync_ForARepositoryThatWasAssignedWithoutARemoteIdentity_AdoptsItInsteadOfCreatingASibling()
    {
        await SeedConnectionAsync("c1", GitProvider.GitHub, "https://github.com", "1001");
        await SeedRepositoryAsync("legacy", "acme", "widgets", gitUrl: "https://github.com/acme/widgets.git");
        await Service().AdoptLegacyRepositoryAsync("legacy", "c1", CancellationToken.None);

        var response = await Service().ConnectAsync(Connect("c1", "42", "main"), CancellationToken.None);

        Assert.False(response.RepositoryCreated);
        Assert.Equal("legacy", response.RepositoryId);
        await using var context = _database.Open();
        var repository = await context.Repositories.SingleAsync();
        Assert.Equal("42", repository.ProviderRepositoryId);
        Assert.Equal("c1", repository.GitConnectionId);
    }

    // ---- helpers ----

    private static ConnectRepositoryRequest Connect(string connectionId, string remoteId, params string[] branches)
        => new(connectionId, remoteId, branches, null);

    private static AddIndexedBranchesRequest Add(params string[] branches) => new(branches, null);

    private ConnectedRepositoryService Service(string? userId = "user-1", Func<IContext>? openContext = null)
    {
        IContext context = openContext is null ? _database.Open() : openContext();
        return new ConnectedRepositoryService(
            context,
            new StubUserContext(userId),
            new GitConnectionAuthorizationService(new StubUserContext(userId), context),
            _protector,
            new GitProviderClientResolver([_github, _gitlab]),
            new RepositoryGenerationLockService(context),
            new BranchActionAuditor(context, NullLogger<BranchActionAuditor>.Instance),
            _logger);
    }

    private async Task<ConnectedRepositoryResponse> RunAfterGateAsync(
        TaskCompletionSource gate, string userId, ConnectRepositoryRequest request)
    {
        await gate.Task;
        return await Service(userId).ConnectAsync(request, CancellationToken.None);
    }

    private async Task SeedConnectionAsync(
        string id, GitProvider provider, string serverUrl, string accountId, string createdBy = "user-1", bool enabled = true)
    {
        await using var context = _database.Open();
        context.GitConnections.Add(new GitConnection
        {
            Id = id,
            Provider = provider,
            NormalizedServerUrl = serverUrl,
            ExternalAccountId = accountId,
            DisplayName = id,
            AccountName = id,
            ProtectedToken = _protector.Protect(Pat),
            CreatedByUserId = createdBy,
            IsEnabled = enabled
        });
        await context.SaveChangesAsync();
    }

    private async Task SeedRepositoryAsync(
        string id, string org, string name, string gitUrl,
        GitProvider? provider = null, string? baseUrl = null, string? remoteId = null, string? connectionId = null)
    {
        await using var context = _database.Open();
        context.Repositories.Add(new Repository
        {
            Id = id,
            OwnerUserId = "user-2",
            GitUrl = gitUrl,
            OrgName = org,
            RepoName = name,
            Status = RepositoryStatus.Completed,
            Provider = provider,
            ProviderBaseUrl = baseUrl,
            ProviderRepositoryId = remoteId,
            GitConnectionId = connectionId
        });
        await context.SaveChangesAsync();
    }

    private async Task AssertNothingWrittenAsync()
    {
        await using var context = _database.Open();
        Assert.Empty(await context.Repositories.ToListAsync());
        Assert.Empty(await context.RepositoryBranches.ToListAsync());
        Assert.Empty(await context.BranchLanguages.ToListAsync());
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
        Assert.Empty(await context.RepositoryGenerationLocks.ToListAsync());
        Assert.Empty(await context.GitConnectionAuditEvents.ToListAsync());
    }

    private sealed class StubUserContext(string? userId) : IUserContext
    {
        public string? UserId => userId;
        public string? UserName => userId;
        public string? Email => null;
        public bool IsAuthenticated => userId is not null;
        public ClaimsPrincipal? User => userId is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test"));
    }
}
