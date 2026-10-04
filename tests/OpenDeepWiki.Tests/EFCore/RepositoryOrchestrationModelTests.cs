using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Postgresql;
using OpenDeepWiki.Tests.Infrastructure;
using Xunit;

namespace OpenDeepWiki.Tests.EFCore;

/// <summary>
/// Constraint behavior of the remote identity and branch-aware lock schema, on real SQLite and PostgreSQL.
/// </summary>
public sealed class RepositoryOrchestrationModelTests : IDisposable
{
    private readonly List<SqliteScratchDatabase> _databases = [];

    public void Dispose()
    {
        foreach (var database in _databases)
        {
            database.Dispose();
        }
    }

    // ---- SQLite ----

    [Fact]
    public async Task Sqlite_RemoteIdentity_IsUniqueAmongActiveRepositories()
    {
        var database = await NewDatabaseAsync();
        await using var context = database.Open();
        context.Repositories.Add(Remote("r1", "acme", "widgets", GitProvider.GitHub, "https://github.com", "42"));
        await context.SaveChangesAsync();

        context.Repositories.Add(Remote("r2", "acme", "widgets-renamed", GitProvider.GitHub, "https://github.com", "42"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Sqlite_RemoteIdentity_AllowsSameIdOnAnotherProviderOrServer()
    {
        var database = await NewDatabaseAsync();
        await using var context = database.Open();
        context.Repositories.AddRange(
            Remote("r1", "acme", "widgets", GitProvider.GitHub, "https://github.com", "42"),
            Remote("r2", "acme~gitlab", "widgets", GitProvider.GitLab, "https://gitlab.com", "42"),
            Remote("r3", "acme~corp", "widgets", GitProvider.GitLab, "https://git.corp.example", "42"));

        await context.SaveChangesAsync();

        Assert.Equal(3, await context.Repositories.CountAsync());
    }

    [Fact]
    public async Task Sqlite_RemoteIdentity_IgnoresSoftDeletedRepositories()
    {
        var database = await NewDatabaseAsync();
        await using var context = database.Open();
        var deleted = Remote("r1", "acme", "widgets", GitProvider.GitHub, "https://github.com", "42");
        deleted.MarkAsDeleted();
        context.Repositories.Add(deleted);
        await context.SaveChangesAsync();

        context.Repositories.Add(Remote("r2", "acme~github", "widgets", GitProvider.GitHub, "https://github.com", "42"));
        await context.SaveChangesAsync();

        Assert.Equal(2, await context.Repositories.CountAsync());
    }

    [Fact]
    public async Task Sqlite_LegacyRepositoriesWithoutIdentity_StayUniqueByPathAndNeverCollideOnIdentity()
    {
        var database = await NewDatabaseAsync();
        await using var context = database.Open();
        context.Repositories.AddRange(
            Legacy("l1", "acme", "one"),
            Legacy("l2", "acme", "two"));
        await context.SaveChangesAsync();

        context.Repositories.Add(Legacy("l3", "acme", "one"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Sqlite_RepositoryMetadata_RoundTrips()
    {
        var database = await NewDatabaseAsync();
        await using (var context = database.Open())
        {
            var repository = Remote("r1", "acme", "widgets", GitProvider.GitLab, "https://git.corp.example", "7");
            repository.DefaultBranch = "develop";
            context.Repositories.Add(repository);
            await context.SaveChangesAsync();
        }

        await using var verification = database.Open();
        var stored = await verification.Repositories.SingleAsync();
        Assert.Equal(GitProvider.GitLab, stored.Provider);
        Assert.Equal("https://git.corp.example", stored.ProviderBaseUrl);
        Assert.Equal("7", stored.ProviderRepositoryId);
        Assert.Equal("develop", stored.DefaultBranch);
    }

    [Fact]
    public async Task Sqlite_Locks_AllowOnePerBranchAndOneRepositoryScopeLock()
    {
        var database = await NewDatabaseAsync();
        await using var context = database.Open();
        await SeedRepositoryWithBranchesAsync(context, "repo-1", "b1", "b2");

        context.RepositoryGenerationLocks.AddRange(
            BranchLock("l1", "repo-1", "b1", "task-1"),
            BranchLock("l2", "repo-1", "b2", "task-2"));
        await context.SaveChangesAsync();

        context.RepositoryGenerationLocks.Add(BranchLock("l3", "repo-1", "b1", "task-3"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        context.RepositoryGenerationLocks.Add(RepositoryLock("l4", "repo-1"));
        await context.SaveChangesAsync();
        context.RepositoryGenerationLocks.Add(RepositoryLock("l5", "repo-1", ownerId: "other"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Sqlite_RemovingABranch_RemovesOnlyItsLock()
    {
        var database = await NewDatabaseAsync();
        await using var context = database.Open();
        await SeedRepositoryWithBranchesAsync(context, "repo-1", "b1", "b2");
        context.RepositoryGenerationLocks.AddRange(
            BranchLock("l1", "repo-1", "b1", "task-1"),
            BranchLock("l2", "repo-1", "b2", "task-2"));
        await context.SaveChangesAsync();

        await using var deleting = database.Open();
        deleting.RepositoryBranches.Remove(await deleting.RepositoryBranches.SingleAsync(branch => branch.Id == "b1"));
        await deleting.SaveChangesAsync();

        await using var verification = database.Open();
        Assert.Equal(["l2"], await verification.RepositoryGenerationLocks.Select(item => item.Id).ToListAsync());
    }

    [Fact]
    public async Task Sqlite_IncrementalTask_StoresTheRequestingUser()
    {
        var database = await NewDatabaseAsync();
        await using (var context = database.Open())
        {
            await SeedRepositoryWithBranchesAsync(context, "repo-1", "b1");
            context.IncrementalUpdateTasks.Add(new IncrementalUpdateTask
            {
                Id = "t1",
                RepositoryId = "repo-1",
                BranchId = "b1",
                RequestedBy = "user-2"
            });
            await context.SaveChangesAsync();
        }

        await using var verification = database.Open();
        Assert.Equal("user-2", (await verification.IncrementalUpdateTasks.SingleAsync()).RequestedBy);
    }

    // ---- PostgreSQL ----

    [PostgresFact]
    public async Task Postgres_RemoteIdentityAndLockConstraints_MatchSqlite()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var context = new PostgresqlDbContext(
            new DbContextOptionsBuilder<PostgresqlDbContext>().UseNpgsql(database.ConnectionString).Options);
        await context.Database.EnsureCreatedAsync();
        context.Users.Add(new User { Id = "user-1", Name = "user-1", Email = "user-1@example.com" });
        var deleted = Remote("r0", "acme", "old", GitProvider.GitHub, "https://github.com", "42");
        deleted.MarkAsDeleted();
        context.Repositories.AddRange(
            deleted,
            Remote("r1", "acme", "widgets", GitProvider.GitHub, "https://github.com", "42"),
            Remote("r2", "acme~gitlab", "widgets", GitProvider.GitLab, "https://gitlab.com", "42"),
            Legacy("l1", "acme", "legacy-one"),
            Legacy("l2", "acme", "legacy-two"));
        await context.SaveChangesAsync();
        await SeedBranchesAsync(context, "r1", "b1", "b2");

        context.Repositories.Add(Remote("r3", "acme3", "widgets", GitProvider.GitHub, "https://github.com", "42"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();

        context.RepositoryGenerationLocks.AddRange(
            BranchLock("l1", "r1", "b1", "task-1"),
            BranchLock("l2", "r1", "b2", "task-2"),
            RepositoryLock("l3", "r1"));
        await context.SaveChangesAsync();
        context.RepositoryGenerationLocks.Add(BranchLock("l4", "r1", "b1", "task-3"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
        context.RepositoryGenerationLocks.Add(RepositoryLock("l5", "r1", ownerId: "other"));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    // ---- helpers ----

    private async Task<SqliteScratchDatabase> NewDatabaseAsync()
    {
        var database = await SqliteScratchDatabase.CreateAsync();
        _databases.Add(database);
        return database;
    }

    private static Repository Remote(
        string id, string org, string name, GitProvider provider, string baseUrl, string remoteId) => new()
    {
        Id = id,
        OwnerUserId = "user-1",
        GitUrl = $"{baseUrl}/{org}/{name}.git",
        OrgName = org,
        RepoName = name,
        Provider = provider,
        ProviderBaseUrl = baseUrl,
        ProviderRepositoryId = remoteId
    };

    private static Repository Legacy(string id, string org, string name) => new()
    {
        Id = id,
        OwnerUserId = "user-1",
        GitUrl = $"https://example.com/{org}/{name}.git",
        OrgName = org,
        RepoName = name
    };

    private static RepositoryGenerationLock BranchLock(string id, string repositoryId, string branchId, string ownerId) => new()
    {
        Id = id,
        RepositoryId = repositoryId,
        BranchId = branchId,
        OwnerType = RepositoryGenerationLockOwnerType.BranchTask,
        OwnerId = ownerId,
        Scope = RepositoryGenerationLockScope.Branch
    };

    private static RepositoryGenerationLock RepositoryLock(string id, string repositoryId, string? ownerId = null) => new()
    {
        Id = id,
        RepositoryId = repositoryId,
        OwnerType = RepositoryGenerationLockOwnerType.Repository,
        OwnerId = ownerId ?? repositoryId,
        Scope = RepositoryGenerationLockScope.Repository
    };

    private static async Task SeedRepositoryWithBranchesAsync(MasterDbContext context, string repositoryId, params string[] branchIds)
    {
        context.Repositories.Add(Remote(repositoryId, "acme", "widgets", GitProvider.GitHub, "https://github.com", "42"));
        await context.SaveChangesAsync();
        await SeedBranchesAsync(context, repositoryId, branchIds);
    }

    private static async Task SeedBranchesAsync(MasterDbContext context, string repositoryId, params string[] branchIds)
    {
        foreach (var branchId in branchIds)
        {
            context.RepositoryBranches.Add(new RepositoryBranch { Id = branchId, RepositoryId = repositoryId, BranchName = branchId });
        }

        await context.SaveChangesAsync();
    }
}
