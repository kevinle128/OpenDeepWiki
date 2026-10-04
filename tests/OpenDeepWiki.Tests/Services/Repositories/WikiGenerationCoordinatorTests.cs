using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class WikiGenerationCoordinatorTests
{
    [Fact]
    public async Task TryBeginAsync_WhenMaxConcurrentIsOne_SecondAcquireIsClusterFull()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        SeedRepository(context, "repo-b");
        await context.SaveChangesAsync();

        var first = CreateCoordinator(context, "instance-a", maxConcurrent: 1);
        var second = CreateCoordinator(context, "instance-b", maxConcurrent: 1);

        var firstResult = await BeginRepositoryAsync(first, context, "repo-a");
        var secondResult = await BeginRepositoryAsync(second, context, "repo-b");

        Assert.Equal(WikiGenerationAcquireStatus.Acquired, firstResult.Status);
        Assert.Equal(WikiGenerationAcquireStatus.ClusterFull, secondResult.Status);
        Assert.Equal(1, await context.WikiGenerationSlots.CountAsync(slot => slot.InstanceId != null));
    }

    [Fact]
    public async Task TryBeginAsync_WhenMaxConcurrentIsTwo_TwoInstancesCanRunTogether()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        SeedRepository(context, "repo-b");
        await context.SaveChangesAsync();

        var first = CreateCoordinator(context, "instance-a", maxConcurrent: 2);
        var second = CreateCoordinator(context, "instance-b", maxConcurrent: 2);

        var firstResult = await BeginRepositoryAsync(first, context, "repo-a");
        var secondResult = await BeginRepositoryAsync(second, context, "repo-b");

        Assert.Equal(WikiGenerationAcquireStatus.Acquired, firstResult.Status);
        Assert.Equal(WikiGenerationAcquireStatus.Acquired, secondResult.Status);
        Assert.NotEqual(firstResult.Lease!.SlotIndex, secondResult.Lease!.SlotIndex);
    }

    [Fact]
    public async Task TryBeginAsync_WhenRepositoryIsBoundByAnotherInstance_ReturnsRepositoryBusy()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        SeedRepository(context, "repo-b");
        await context.SaveChangesAsync();

        var first = CreateCoordinator(context, "instance-a", maxConcurrent: 2);
        var second = CreateCoordinator(context, "instance-b", maxConcurrent: 2);

        Assert.Equal(WikiGenerationAcquireStatus.Acquired, (await BeginRepositoryAsync(first, context, "repo-a")).Status);

        var busy = await BeginRepositoryAsync(second, context, "repo-a");
        var otherRepo = await BeginRepositoryAsync(second, context, "repo-b");

        Assert.Equal(WikiGenerationAcquireStatus.RepositoryBusy, busy.Status);
        Assert.Equal(WikiGenerationAcquireStatus.Acquired, otherRepo.Status);
    }

    [Fact]
    public async Task TryBeginAsync_AfterRelease_AllowsAnotherRepositoryToStart()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        SeedRepository(context, "repo-b");
        await context.SaveChangesAsync();

        var coordinator = CreateCoordinator(context, "instance-a", maxConcurrent: 1);
        var first = await BeginRepositoryAsync(coordinator, context, "repo-a");
        Assert.Equal(WikiGenerationAcquireStatus.Acquired, first.Status);

        await coordinator.ReleaseAsync(context, first.Lease!);

        var second = await BeginRepositoryAsync(coordinator, context, "repo-b");
        Assert.Equal(WikiGenerationAcquireStatus.Acquired, second.Status);
        Assert.Empty(await context.RepositoryGenerationLocks.Where(item => item.RepositoryId == "repo-a").ToListAsync());
    }

    [Fact]
    public async Task TryBeginAsync_WhenSlotIsStale_AllowsAnotherInstanceToTakeIt()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        SeedRepository(context, "repo-b");
        await context.SaveChangesAsync();

        var options = new WikiGeneratorOptions
        {
            MaxConcurrentGenerations = 1,
            GenerationLeaseTimeoutSeconds = 60
        };
        var first = CreateCoordinator(context, "instance-a", options);
        var acquired = await BeginRepositoryAsync(first, context, "repo-a");
        Assert.Equal(WikiGenerationAcquireStatus.Acquired, acquired.Status);

        var slot = await context.WikiGenerationSlots.SingleAsync();
        slot.HeartbeatAt = DateTime.UtcNow.AddMinutes(-5);
        var generationLock = await context.RepositoryGenerationLocks.SingleAsync();
        generationLock.HeartbeatAt = DateTime.UtcNow.AddMinutes(-5);
        await context.SaveChangesAsync();

        var second = CreateCoordinator(context, "instance-b", options);
        var stolen = await BeginRepositoryAsync(second, context, "repo-b");

        Assert.Equal(WikiGenerationAcquireStatus.Acquired, stolen.Status);
        Assert.Equal("instance-b", (await context.WikiGenerationSlots.SingleAsync()).InstanceId);
    }

    [Fact]
    public async Task RecoverStaleWorkAsync_ResetsProcessingRepositoryToPending()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a", RepositoryStatus.Processing);
        context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = "repo-a",
            OwnerType = RepositoryGenerationLockOwnerType.Repository,
            OwnerId = "repo-a",
            Scope = RepositoryGenerationLockScope.Repository,
            InstanceId = "dead-instance",
            HeartbeatAt = DateTime.UtcNow.AddMinutes(-10),
            AcquiredAt = DateTime.UtcNow.AddMinutes(-10)
        });
        await context.SaveChangesAsync();

        var coordinator = CreateCoordinator(context, "instance-a", maxConcurrent: 1);
        await coordinator.RecoverStaleWorkAsync(context);

        var repository = await context.Repositories.SingleAsync();
        var generationLock = await context.RepositoryGenerationLocks.SingleAsync();
        Assert.Equal(RepositoryStatus.Pending, repository.Status);
        Assert.Null(generationLock.InstanceId);
    }

    [Fact]
    public async Task TryBeginAsync_TwoBranchesOfOneRepository_RunTogetherWithTheirOwnLeases()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        await context.SaveChangesAsync();

        var coordinator = CreateCoordinator(context, "instance-a", maxConcurrent: 2);

        var first = await BeginBranchAsync(coordinator, context, "repo-a", "b1", "task-1");
        var second = await BeginBranchAsync(coordinator, context, "repo-a", "b2", "task-2");

        Assert.Equal(WikiGenerationAcquireStatus.Acquired, first.Status);
        Assert.Equal(WikiGenerationAcquireStatus.Acquired, second.Status);
        Assert.Equal("b1", first.Lease!.BranchId);
        Assert.Equal("b2", second.Lease!.BranchId);
        Assert.Equal(2, await context.RepositoryGenerationLocks.CountAsync());
    }

    [Fact]
    public async Task TryBeginAsync_WhenTheSameBranchIsRunning_ReturnsRepositoryBusy()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        await context.SaveChangesAsync();

        var first = CreateCoordinator(context, "instance-a", maxConcurrent: 2);
        var second = CreateCoordinator(context, "instance-b", maxConcurrent: 2);
        Assert.Equal(WikiGenerationAcquireStatus.Acquired, (await BeginBranchAsync(first, context, "repo-a", "b1", "task-1")).Status);

        var busy = await BeginBranchAsync(second, context, "repo-a", "b1", "task-2", RepositoryGenerationLockOwnerType.IncrementalTask);

        Assert.Equal(WikiGenerationAcquireStatus.RepositoryBusy, busy.Status);
    }

    [Fact]
    public async Task TryBeginAsync_WhenClusterIsFull_KeepsTheBranchReservationForLater()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        await context.SaveChangesAsync();
        var lockService = new RepositoryGenerationLockService(
            context, new WikiGenerationInstanceIdentity("api"), new StaticOptionsMonitor<WikiGeneratorOptions>(new WikiGeneratorOptions()));
        Assert.True(await lockService.TryAcquireAsync(
            context, "repo-a", RepositoryGenerationLockOwnerType.BranchTask, "task-1",
            RepositoryGenerationLockScope.Branch, branchId: "b1"));
        var coordinator = CreateCoordinator(context, "instance-a", maxConcurrent: 1);
        Assert.Equal(WikiGenerationAcquireStatus.Acquired, (await BeginRepositoryAsync(coordinator, context, "repo-b")).Status);

        var full = await BeginBranchAsync(coordinator, context, "repo-a", "b1", "task-1");

        Assert.Equal(WikiGenerationAcquireStatus.ClusterFull, full.Status);
        var reservation = await context.RepositoryGenerationLocks.SingleAsync(item => item.RepositoryId == "repo-a");
        Assert.Equal("b1", reservation.BranchId);
        Assert.Null(reservation.InstanceId);
    }

    [Fact]
    public async Task ReleaseAsync_OfABranchLease_KeepsTheOtherBranchLock()
    {
        using var context = CreateContext();
        SeedRepository(context, "repo-a");
        await context.SaveChangesAsync();
        var coordinator = CreateCoordinator(context, "instance-a", maxConcurrent: 2);
        var first = await BeginBranchAsync(coordinator, context, "repo-a", "b1", "task-1");
        await BeginBranchAsync(coordinator, context, "repo-a", "b2", "task-2");

        await coordinator.ReleaseAsync(context, first.Lease!);

        Assert.Equal("b2", (await context.RepositoryGenerationLocks.SingleAsync()).BranchId);
    }

    private static Task<(WikiGenerationAcquireStatus Status, WikiGenerationWorkLease? Lease)> BeginBranchAsync(
        IWikiGenerationCoordinator coordinator,
        IContext context,
        string repositoryId,
        string branchId,
        string ownerId,
        RepositoryGenerationLockOwnerType ownerType = RepositoryGenerationLockOwnerType.BranchTask)
    {
        return coordinator.TryBeginAsync(
            context,
            repositoryId,
            ownerType,
            ownerId,
            RepositoryGenerationLockScope.Branch,
            WikiGenerationWorkType.BranchTask,
            branchId: branchId);
    }

    private static Task<(WikiGenerationAcquireStatus Status, WikiGenerationWorkLease? Lease)> BeginRepositoryAsync(
        IWikiGenerationCoordinator coordinator,
        IContext context,
        string repositoryId)
    {
        return coordinator.TryBeginAsync(
            context,
            repositoryId,
            RepositoryGenerationLockOwnerType.Repository,
            repositoryId,
            RepositoryGenerationLockScope.Repository,
            WikiGenerationWorkType.Repository);
    }

    private static WikiGenerationCoordinator CreateCoordinator(
        IContext context,
        string instanceId,
        int maxConcurrent)
    {
        return CreateCoordinator(context, instanceId, new WikiGeneratorOptions
        {
            MaxConcurrentGenerations = maxConcurrent
        });
    }

    private static WikiGenerationCoordinator CreateCoordinator(
        IContext context,
        string instanceId,
        WikiGeneratorOptions options)
    {
        var identity = new WikiGenerationInstanceIdentity(instanceId);
        var optionsMonitor = new StaticOptionsMonitor<WikiGeneratorOptions>(options);
        var lockService = new RepositoryGenerationLockService(context, identity, optionsMonitor);
        return new WikiGenerationCoordinator(lockService, identity, optionsMonitor);
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TestDbContext(options);
    }

    private static void SeedRepository(
        TestDbContext context,
        string id,
        RepositoryStatus status = RepositoryStatus.Pending)
    {
        context.Repositories.Add(new Repository
        {
            Id = id,
            OwnerUserId = "user-1",
            GitUrl = $"https://github.com/demo/{id}.git",
            OrgName = "demo",
            RepoName = id,
            Status = status
        });
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options);

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
