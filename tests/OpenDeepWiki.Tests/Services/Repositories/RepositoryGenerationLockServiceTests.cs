using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Postgresql;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using OpenDeepWiki.Tests.Infrastructure;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class RepositoryGenerationLockServiceTests
{
    [Fact]
    public async Task TryAcquireAsync_AllowsReservationWithoutBindingInstance()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        await context.SaveChangesAsync();

        var service = CreateService(context, "api-instance");
        var acquired = await service.TryAcquireAsync(
            context,
            "repo-a",
            RepositoryGenerationLockOwnerType.Repository,
            "repo-a",
            RepositoryGenerationLockScope.Repository);

        Assert.True(acquired);
        var generationLock = await context.RepositoryGenerationLocks.SingleAsync();
        Assert.Null(generationLock.InstanceId);
    }

    [Fact]
    public async Task TryAcquireAsync_BindsReservationToCurrentInstance()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        await context.SaveChangesAsync();

        var service = CreateService(context, "worker-a");
        Assert.True(await service.TryAcquireAsync(
            context,
            "repo-a",
            RepositoryGenerationLockOwnerType.Repository,
            "repo-a",
            RepositoryGenerationLockScope.Repository));

        Assert.True(await service.TryAcquireAsync(
            context,
            "repo-a",
            RepositoryGenerationLockOwnerType.Repository,
            "repo-a",
            RepositoryGenerationLockScope.Repository,
            bindToCurrentInstance: true));

        var generationLock = await context.RepositoryGenerationLocks.SingleAsync();
        Assert.Equal("worker-a", generationLock.InstanceId);
        Assert.NotNull(generationLock.HeartbeatAt);
    }

    [Fact]
    public async Task TryAcquireAsync_RejectsLiveLockHeldByAnotherInstance()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        await context.SaveChangesAsync();

        var first = CreateService(context, "worker-a");
        var second = CreateService(context, "worker-b");

        Assert.True(await first.TryAcquireAsync(
            context,
            "repo-a",
            RepositoryGenerationLockOwnerType.Repository,
            "repo-a",
            RepositoryGenerationLockScope.Repository,
            bindToCurrentInstance: true));

        Assert.False(await second.TryAcquireAsync(
            context,
            "repo-a",
            RepositoryGenerationLockOwnerType.Repository,
            "repo-a",
            RepositoryGenerationLockScope.Repository,
            bindToCurrentInstance: true));
    }

    // ---- repository and branch lock matrix (real SQLite) ----

    [Fact]
    public async Task TryAcquireAsync_TwoBranchesOfOneRepository_BothAcquire()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1", "b2");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");

        var first = await AcquireBranchAsync(service, context, "repo-a", "b1", "task-1");
        var second = await AcquireBranchAsync(service, context, "repo-a", "b2", "task-2");

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(2, await context.RepositoryGenerationLocks.CountAsync());
    }

    [Fact]
    public async Task TryAcquireAsync_SameBranchForFullAndIncrementalWork_ConflictsOnTheSecond()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");

        Assert.True(await AcquireBranchAsync(service, context, "repo-a", "b1", "full-task"));
        var incremental = await service.TryAcquireAsync(
            context, "repo-a", RepositoryGenerationLockOwnerType.IncrementalTask, "inc-task",
            RepositoryGenerationLockScope.Branch, branchId: "b1");

        Assert.False(incremental);
    }

    [Fact]
    public async Task TryAcquireAsync_BranchLockWhileRepositoryLockIsHeld_Conflicts()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");

        Assert.True(await AcquireRepositoryAsync(service, context, "repo-a"));

        Assert.False(await AcquireBranchAsync(service, context, "repo-a", "b1", "task-1"));
    }

    [Fact]
    public async Task TryAcquireAsync_RepositoryLockWhileAnyBranchLockIsHeld_Conflicts()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1", "b2");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");

        Assert.True(await AcquireBranchAsync(service, context, "repo-a", "b2", "task-2"));

        Assert.False(await AcquireRepositoryAsync(service, context, "repo-a"));
        Assert.Single(await context.RepositoryGenerationLocks.ToListAsync());
    }

    [Fact]
    public async Task TryAcquireAsync_LocksOfOtherRepositories_NeverConflict()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "a1");
        await SeedRepositoryWithBranchesAsync(database, "repo-b", "b1");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");

        Assert.True(await AcquireRepositoryAsync(service, context, "repo-a"));
        Assert.True(await AcquireBranchAsync(service, context, "repo-b", "b1", "task-1"));
    }

    [Fact]
    public async Task TryAcquireAsync_SameOwnerAgain_IsIdempotentForBranchAndRepositoryLocks()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");

        Assert.True(await AcquireBranchAsync(service, context, "repo-a", "b1", "task-1"));
        Assert.True(await AcquireBranchAsync(service, context, "repo-a", "b1", "task-1"));
        Assert.Single(await context.RepositoryGenerationLocks.ToListAsync());
    }

    [Theory]
    [InlineData(RepositoryGenerationLockScope.Branch, null)]
    [InlineData(RepositoryGenerationLockScope.Repository, "b1")]
    public async Task TryAcquireAsync_WhenScopeAndBranchIdDisagree_Throws(RepositoryGenerationLockScope scope, string? branchId)
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");

        await Assert.ThrowsAsync<ArgumentException>(() => service.TryAcquireAsync(
            context, "repo-a", RepositoryGenerationLockOwnerType.BranchTask, "task-1", scope, branchId: branchId));
        Assert.Empty(await context.RepositoryGenerationLocks.ToListAsync());
    }

    [Fact]
    public async Task ReleaseAsync_WithBranchId_RemovesOnlyThatBranchLock()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1", "b2");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");
        await AcquireBranchAsync(service, context, "repo-a", "b1", "task-1");
        await AcquireBranchAsync(service, context, "repo-a", "b2", "task-2");

        await service.ReleaseAsync(
            context, "repo-a", RepositoryGenerationLockOwnerType.BranchTask, "task-1", branchId: "b1");

        var remaining = await context.RepositoryGenerationLocks.SingleAsync();
        Assert.Equal("b2", remaining.BranchId);
    }

    [Fact]
    public async Task ReleaseAsync_WhenBranchIdDoesNotMatchTheOwner_KeepsTheLock()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1", "b2");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");
        await AcquireBranchAsync(service, context, "repo-a", "b1", "task-1");

        await service.ReleaseAsync(
            context, "repo-a", RepositoryGenerationLockOwnerType.BranchTask, "task-1", branchId: "b2");

        Assert.Single(await context.RepositoryGenerationLocks.ToListAsync());
    }

    [Fact]
    public async Task HeartbeatAsync_WithBranchId_TouchesOnlyTheOwnersLock()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1", "b2");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");
        Assert.True(await service.TryAcquireAsync(
            context, "repo-a", RepositoryGenerationLockOwnerType.BranchTask, "task-1",
            RepositoryGenerationLockScope.Branch, bindToCurrentInstance: true, branchId: "b1"));
        Assert.True(await service.TryAcquireAsync(
            context, "repo-a", RepositoryGenerationLockOwnerType.BranchTask, "task-2",
            RepositoryGenerationLockScope.Branch, bindToCurrentInstance: true, branchId: "b2"));
        await context.RepositoryGenerationLocks
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.HeartbeatAt, DateTime.UtcNow.AddMinutes(-1)));

        await service.HeartbeatAsync(
            context, "repo-a", RepositoryGenerationLockOwnerType.BranchTask, "task-1", branchId: "b1");

        var locks = await context.RepositoryGenerationLocks.AsNoTracking().ToListAsync();
        Assert.True(locks.Single(item => item.BranchId == "b1").HeartbeatAt > DateTime.UtcNow.AddSeconds(-30));
        Assert.True(locks.Single(item => item.BranchId == "b2").HeartbeatAt < DateTime.UtcNow.AddSeconds(-30));
    }

    [Fact]
    public async Task TryAcquireAsync_WhenTheBlockingBranchLockIsStale_RecoversItsTaskAndTakesOver()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1");
        await using var context = database.Open();
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "task-1",
            RepositoryId = "repo-a",
            BranchId = "b1",
            Status = BranchGenerationTaskStatus.Processing
        });
        context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
        {
            Id = "stale",
            RepositoryId = "repo-a",
            BranchId = "b1",
            OwnerType = RepositoryGenerationLockOwnerType.BranchTask,
            OwnerId = "task-1",
            Scope = RepositoryGenerationLockScope.Branch,
            InstanceId = "dead-worker",
            HeartbeatAt = DateTime.UtcNow.AddMinutes(-30)
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, "worker-a");

        var acquired = await AcquireRepositoryAsync(service, context, "repo-a");

        Assert.True(acquired);
        var lockRow = await context.RepositoryGenerationLocks.SingleAsync();
        Assert.Null(lockRow.BranchId);
        Assert.Equal(BranchGenerationTaskStatus.Pending, (await context.BranchGenerationTasks.SingleAsync()).Status);
    }

    [Fact]
    public async Task TryAcquireAsync_RepositoryAndBranchRequestsRace_NeverBothSucceed()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1");

        for (var round = 0; round < 15; round++)
        {
            await using var repositoryContext = database.Open();
            await using var branchContext = database.Open();
            var repositoryService = CreateService(repositoryContext, "worker-a");
            var branchService = CreateService(branchContext, "worker-b");
            var gate = new TaskCompletionSource();

            var repositoryAttempt = Task.Run(async () =>
            {
                await gate.Task;
                return await AcquireRepositoryAsync(repositoryService, repositoryContext, "repo-a", $"owner-{round}");
            });
            var branchAttempt = Task.Run(async () =>
            {
                await gate.Task;
                return await AcquireBranchAsync(branchService, branchContext, "repo-a", "b1", $"task-{round}");
            });
            gate.SetResult();
            var results = await Task.WhenAll(repositoryAttempt, branchAttempt);

            Assert.True(results.Count(result => result) <= 1, $"Round {round}: repository and branch lock were both granted.");

            await using var verification = database.Open();
            var locks = await verification.RepositoryGenerationLocks.ToListAsync();
            Assert.False(locks.Any(item => item.BranchId == null) && locks.Any(item => item.BranchId != null));
            verification.RepositoryGenerationLocks.RemoveRange(locks);
            await verification.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task TryAcquireAsync_SameBranchRace_GrantsExactlyOneOwner()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1");

        for (var round = 0; round < 10; round++)
        {
            await using var firstContext = database.Open();
            await using var secondContext = database.Open();
            var gate = new TaskCompletionSource();
            var first = Task.Run(async () =>
            {
                await gate.Task;
                return await AcquireBranchAsync(CreateService(firstContext, "worker-a"), firstContext, "repo-a", "b1", $"full-{round}");
            });
            var second = Task.Run(async () =>
            {
                await gate.Task;
                return await CreateService(secondContext, "worker-b").TryAcquireAsync(
                    secondContext, "repo-a", RepositoryGenerationLockOwnerType.IncrementalTask, $"inc-{round}",
                    RepositoryGenerationLockScope.Branch, branchId: "b1");
            });
            gate.SetResult();

            var results = await Task.WhenAll(first, second);

            Assert.Equal(1, results.Count(result => result));
            await using var verification = database.Open();
            verification.RepositoryGenerationLocks.RemoveRange(await verification.RepositoryGenerationLocks.ToListAsync());
            await verification.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task GetLockAsync_ForABranch_ReturnsTheLockThatBlocksIt()
    {
        using var database = await SqliteScratchDatabase.CreateAsync();
        await SeedRepositoryWithBranchesAsync(database, "repo-a", "b1", "b2");
        await using var context = database.Open();
        var service = CreateService(context, "worker-a");
        await AcquireBranchAsync(service, context, "repo-a", "b1", "task-1");

        var blocking = await service.GetLockAsync("repo-a", branchId: "b1");
        var other = await service.GetLockAsync("repo-a", branchId: "b2");

        Assert.Equal("task-1", blocking?.OwnerId);
        Assert.Null(other);
    }

    [PostgresFact]
    public async Task Postgres_TryAcquireAsync_RepositoryAndBranchRequestsRace_NeverBothSucceed()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        PostgresqlDbContext Open() => new(new DbContextOptionsBuilder<PostgresqlDbContext>().UseNpgsql(database.ConnectionString).Options);
        await using (var seed = Open())
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Users.Add(new User { Id = "user-1", Name = "user-1", Email = "user-1@example.com" });
            seed.Repositories.Add(new Repository
            {
                Id = "repo-a",
                OwnerUserId = "user-1",
                GitUrl = "https://github.com/demo/repo-a.git",
                OrgName = "demo",
                RepoName = "repo-a",
                Status = RepositoryStatus.Completed
            });
            seed.RepositoryBranches.Add(new RepositoryBranch { Id = "b1", RepositoryId = "repo-a", BranchName = "b1" });
            await seed.SaveChangesAsync();
        }

        for (var round = 0; round < 25; round++)
        {
            await using var repositoryContext = Open();
            await using var branchContext = Open();
            var gate = new TaskCompletionSource();
            var repositoryAttempt = Task.Run(async () =>
            {
                await gate.Task;
                return await AcquireRepositoryAsync(CreateService(repositoryContext, "worker-a"), repositoryContext, "repo-a", $"owner-{round}");
            });
            var branchAttempt = Task.Run(async () =>
            {
                await gate.Task;
                return await AcquireBranchAsync(CreateService(branchContext, "worker-b"), branchContext, "repo-a", "b1", $"task-{round}");
            });
            gate.SetResult();
            var results = await Task.WhenAll(repositoryAttempt, branchAttempt);

            Assert.True(results.Count(result => result) <= 1, $"Round {round}: repository and branch lock were both granted.");
            await using var verification = Open();
            var locks = await verification.RepositoryGenerationLocks.ToListAsync();
            Assert.False(locks.Any(item => item.BranchId == null) && locks.Any(item => item.BranchId != null));
            verification.RepositoryGenerationLocks.RemoveRange(locks);
            await verification.SaveChangesAsync();
        }
    }

    private static Task<bool> AcquireBranchAsync(
        RepositoryGenerationLockService service, IContext context, string repositoryId, string branchId, string ownerId)
        => service.TryAcquireAsync(
            context, repositoryId, RepositoryGenerationLockOwnerType.BranchTask, ownerId,
            RepositoryGenerationLockScope.Branch, branchId: branchId);

    private static Task<bool> AcquireRepositoryAsync(
        RepositoryGenerationLockService service, IContext context, string repositoryId, string? ownerId = null)
        => service.TryAcquireAsync(
            context, repositoryId, RepositoryGenerationLockOwnerType.Repository, ownerId ?? repositoryId,
            RepositoryGenerationLockScope.Repository);

    private static async Task SeedRepositoryWithBranchesAsync(
        SqliteScratchDatabase database, string repositoryId, params string[] branchIds)
    {
        await using var context = database.Open();
        context.Repositories.Add(new Repository
        {
            Id = repositoryId,
            OwnerUserId = "user-1",
            GitUrl = $"https://github.com/demo/{repositoryId}.git",
            OrgName = "demo",
            RepoName = repositoryId,
            Status = RepositoryStatus.Completed
        });
        foreach (var branchId in branchIds)
        {
            context.RepositoryBranches.Add(new RepositoryBranch { Id = branchId, RepositoryId = repositoryId, BranchName = branchId });
        }

        await context.SaveChangesAsync();
    }

    private static RepositoryGenerationLockService CreateService(IContext context, string instanceId)
    {
        return new RepositoryGenerationLockService(
            context,
            new WikiGenerationInstanceIdentity(instanceId),
            new StaticOptionsMonitor<WikiGeneratorOptions>(new WikiGeneratorOptions()));
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static void SeedRepository(TestDbContext context, string id)
    {
        context.Repositories.Add(new Repository
        {
            Id = id,
            OwnerUserId = "user-1",
            GitUrl = $"https://github.com/demo/{id}.git",
            OrgName = "demo",
            RepoName = id,
            Status = RepositoryStatus.Pending
        });
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable OnChange(Action<T, string?> listener) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
