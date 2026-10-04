using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

/// <summary>
/// Branch jobs of one repository run independently: each branch has its own lock and lease,
/// while the global generation slots still cap the total.
/// </summary>
public sealed class BranchGenerationWorkerTests : IDisposable
{
    private readonly SqliteScratchDatabase _database;

    public BranchGenerationWorkerTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task Dispatch_TwoBranchesOfOneRepository_RunAtTheSameTimeAndReleaseTheirOwnLocks()
    {
        await SeedAsync("repo-a", ("b1", "task-1"), ("b2", "task-2"));
        var bothRunning = new TaskCompletionSource();
        var running = 0;
        var processor = new Mock<IRepositoryBranchProcessor>();
        processor
            .Setup(x => x.ProcessBranchAsync(
                It.IsAny<IContext>(), It.IsAny<Repository>(), It.IsAny<RepositoryBranch>(),
                It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
            .Returns(async (IContext _, Repository _, RepositoryBranch branch, string? _, bool _, CancellationToken token) =>
            {
                if (Interlocked.Increment(ref running) == 2)
                {
                    bothRunning.TrySetResult();
                }

                // Neither job can finish until the other one started, so this only completes when both run together.
                await bothRunning.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                return $"commit-{branch.BranchName}";
            });
        using var worker = CreateWorker(processor.Object, maxConcurrent: 2);

        await DispatchAsync(worker);
        await WaitUntilAsync(async () =>
        {
            await using var context = _database.Open();
            return await context.BranchGenerationTasks.CountAsync(item => item.Status == BranchGenerationTaskStatus.Completed) == 2;
        });

        await using var verification = _database.Open();
        Assert.Empty(await verification.RepositoryGenerationLocks.ToListAsync());
        Assert.Equal(2, await verification.BranchGenerationTasks.CountAsync(item => item.TargetCommitId != null));
    }

    [Fact]
    public async Task Dispatch_WhenTheClusterIsFull_LeavesTheSecondBranchPendingWithItsReservation()
    {
        await SeedAsync("repo-a", ("b1", "task-1"), ("b2", "task-2"));
        var release = new TaskCompletionSource();
        var processor = new Mock<IRepositoryBranchProcessor>();
        processor
            .Setup(x => x.ProcessBranchAsync(
                It.IsAny<IContext>(), It.IsAny<Repository>(), It.IsAny<RepositoryBranch>(),
                It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
            .Returns(async (IContext _, Repository _, RepositoryBranch _, string? _, bool _, CancellationToken token) =>
            {
                await release.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                return "commit";
            });
        using var worker = CreateWorker(processor.Object, maxConcurrent: 1);

        await DispatchAsync(worker);

        await using (var context = _database.Open())
        {
            var tasks = await context.BranchGenerationTasks.AsNoTracking().OrderBy(item => item.CreatedAt).ToListAsync();
            Assert.Equal(BranchGenerationTaskStatus.Processing, tasks[0].Status);
            Assert.Equal(BranchGenerationTaskStatus.Pending, tasks[1].Status);
            var locks = await context.RepositoryGenerationLocks.AsNoTracking().ToListAsync();
            Assert.Equal(2, locks.Count);
            Assert.NotNull(locks.Single(item => item.OwnerId == "task-1").InstanceId);
            Assert.Null(locks.Single(item => item.OwnerId == "task-2").InstanceId);
        }

        release.SetResult();
        await WaitUntilAsync(async () =>
        {
            await using var context = _database.Open();
            return await context.BranchGenerationTasks.CountAsync(item => item.Status == BranchGenerationTaskStatus.Completed) == 1;
        });
    }

    [Fact]
    public async Task Dispatch_WhenARepositoryLockIsHeld_SkipsEveryBranchTask()
    {
        await SeedAsync("repo-a", ("b1", "task-1"));
        await using (var context = _database.Open())
        {
            // A repository-scope lock replaces the branch reservation of the seed.
            context.RepositoryGenerationLocks.RemoveRange(await context.RepositoryGenerationLocks.ToListAsync());
            context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
            {
                Id = "repo-lock",
                RepositoryId = "repo-a",
                OwnerType = RepositoryGenerationLockOwnerType.Repository,
                OwnerId = "repo-a",
                Scope = RepositoryGenerationLockScope.Repository,
                InstanceId = "other-instance",
                HeartbeatAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var processor = new Mock<IRepositoryBranchProcessor>(MockBehavior.Strict);
        using var worker = CreateWorker(processor.Object, maxConcurrent: 2);

        await DispatchAsync(worker);

        await using var verification = _database.Open();
        Assert.Equal(BranchGenerationTaskStatus.Pending, (await verification.BranchGenerationTasks.SingleAsync()).Status);
        processor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunningWorker_RetriesWorkspaceDeletesThatFailedAfterABranchRemoval()
    {
        await SeedAsync("repo-a");
        var root = Path.Combine(TestPaths.ScratchRoot, $"worker-cleanup-{Guid.NewGuid():N}");
        var tree = RepositoryWorkspacePath.ForBranch(
            new RepositoryAnalyzerOptions { RepositoriesDirectory = root }, "acme", "widgets", "gone");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "file.txt"), "content");
        File.WriteAllText(Path.Combine(root, "acme", "widgets", ".pending-workspace-removals"), "gone" + Environment.NewLine);
        try
        {
            using var worker = CreateWorker(Mock.Of<IRepositoryBranchProcessor>(), maxConcurrent: 1, root);

            await worker.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(tree)));
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }

            Assert.False(File.Exists(Path.Combine(root, "acme", "widgets", ".pending-workspace-removals")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ---- helpers ----

    private BranchGenerationWorker CreateWorker(IRepositoryBranchProcessor processor, int maxConcurrent, string? repositoriesDirectory = null)
    {
        var options = new WikiGeneratorOptions { MaxConcurrentGenerations = maxConcurrent };
        var optionsMonitor = new StaticOptionsMonitor<WikiGeneratorOptions>(options);
        var identity = new WikiGenerationInstanceIdentity("worker-instance");

        var services = new ServiceCollection();
        services.AddScoped<IContext>(_ => _database.Open());
        services.AddSingleton(identity);
        services.AddSingleton<IOptionsMonitor<WikiGeneratorOptions>>(optionsMonitor);
        services.AddScoped<IRepositoryGenerationLockService, RepositoryGenerationLockService>();
        services.AddScoped<IWikiGenerationCoordinator, WikiGenerationCoordinator>();
        services.AddSingleton(processor);
        services.AddScoped<IUserContext>(_ => new AnonymousUserContext());
        services.AddScoped<IBranchActionAuditor, BranchActionAuditor>();
        services.AddSingleton(Options.Create(new RepositoryAnalyzerOptions
        {
            RepositoriesDirectory = repositoriesDirectory ?? Path.Combine(TestPaths.ScratchRoot, "worker-unused-root")
        }));
        services.AddLogging();
        services.AddScoped<IIndexedBranchRemovalService, IndexedBranchRemovalService>();
        var provider = services.BuildServiceProvider();

        return new BranchGenerationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BranchGenerationWorker>.Instance,
            optionsMonitor);
    }

    private static async Task DispatchAsync(BranchGenerationWorker worker)
    {
        var method = typeof(BranchGenerationWorker).GetMethod(
            "DispatchPendingTasksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(worker, [CancellationToken.None])!;
    }

    private async Task SeedAsync(string repositoryId, params (string BranchId, string TaskId)[] branches)
    {
        await using var context = _database.Open();
        context.Repositories.Add(new Repository
        {
            Id = repositoryId,
            OwnerUserId = "user-1",
            GitUrl = "https://github.com/acme/widgets.git",
            OrgName = "acme",
            RepoName = "widgets",
            Status = RepositoryStatus.Completed
        });
        var created = DateTime.UtcNow.AddMinutes(-10);
        foreach (var (branchId, taskId) in branches)
        {
            context.RepositoryBranches.Add(new RepositoryBranch
            {
                Id = branchId,
                RepositoryId = repositoryId,
                BranchName = branchId,
                GenerationStatus = BranchGenerationTaskStatus.Pending
            });
            context.BranchGenerationTasks.Add(new BranchGenerationTask
            {
                Id = taskId,
                RepositoryId = repositoryId,
                BranchId = branchId,
                Status = BranchGenerationTaskStatus.Pending,
                CreatedAt = created = created.AddSeconds(1)
            });
            context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
            {
                Id = $"lock-{taskId}",
                RepositoryId = repositoryId,
                BranchId = branchId,
                OwnerType = RepositoryGenerationLockOwnerType.BranchTask,
                OwnerId = taskId,
                Scope = RepositoryGenerationLockScope.Branch
            });
        }

        await context.SaveChangesAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("The condition was not met in time.");
    }

    private sealed class AnonymousUserContext : IUserContext
    {
        public string? UserId => null;
        public string? UserName => null;
        public string? Email => null;
        public bool IsAuthenticated => false;
        public System.Security.Claims.ClaimsPrincipal? User => null;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
