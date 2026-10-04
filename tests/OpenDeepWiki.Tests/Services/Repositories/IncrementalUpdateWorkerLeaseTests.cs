using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

/// <summary>
/// Incremental jobs hold the lease of their own branch, so work on other branches of the repository continues,
/// and a branch whose update needs a full generation hands over to a full generation task.
/// </summary>
public sealed class IncrementalUpdateWorkerLeaseTests : IDisposable
{
    private readonly SqliteScratchDatabase _database;

    public IncrementalUpdateWorkerLeaseTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task ProcessTask_HoldsTheBranchLockWhileItRunsAndReleasesItAfterwards()
    {
        await SeedAsync(("b1", "inc-1"));
        RepositoryGenerationLock? observed = null;
        var service = new Mock<IIncrementalUpdateService>();
        service
            .Setup(x => x.ProcessIncrementalUpdateAsync("repo-a", "b1", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await using var context = _database.Open();
                observed = await context.RepositoryGenerationLocks.AsNoTracking().SingleAsync();
                return new IncrementalUpdateResult { Success = true, CurrentCommitId = "new-sha" };
            });

        await RunAsync(service.Object, "inc-1");

        Assert.NotNull(observed);
        Assert.Equal("b1", observed!.BranchId);
        Assert.Equal(RepositoryGenerationLockScope.Branch, observed.Scope);
        Assert.Equal(RepositoryGenerationLockOwnerType.IncrementalTask, observed.OwnerType);
        await using var verification = _database.Open();
        Assert.Empty(await verification.RepositoryGenerationLocks.ToListAsync());
        var task = await verification.IncrementalUpdateTasks.SingleAsync();
        Assert.Equal(IncrementalUpdateStatus.Completed, task.Status);
        Assert.Equal("new-sha", task.TargetCommitId);
    }

    [Fact]
    public async Task ProcessTask_WhileAnotherBranchOfTheRepositoryIsLocked_StillRuns()
    {
        await SeedAsync(("b1", "inc-1"), ("b2", null));
        await using (var context = _database.Open())
        {
            context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
            {
                Id = "other",
                RepositoryId = "repo-a",
                BranchId = "b2",
                OwnerType = RepositoryGenerationLockOwnerType.BranchTask,
                OwnerId = "full-b2",
                Scope = RepositoryGenerationLockScope.Branch,
                InstanceId = "elsewhere",
                HeartbeatAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var service = new Mock<IIncrementalUpdateService>();
        service
            .Setup(x => x.ProcessIncrementalUpdateAsync("repo-a", "b1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IncrementalUpdateResult { Success = true, CurrentCommitId = "new-sha" });

        await RunAsync(service.Object, "inc-1");

        await using var verification = _database.Open();
        Assert.Equal(IncrementalUpdateStatus.Completed, (await verification.IncrementalUpdateTasks.SingleAsync()).Status);
        Assert.Equal(["other"], await verification.RepositoryGenerationLocks.Select(item => item.Id).ToListAsync());
    }

    [Fact]
    public async Task ProcessTask_WhileTheSameBranchHasAFullGenerationReservation_IsSkipped()
    {
        await SeedAsync(("b1", "inc-1"));
        await using (var context = _database.Open())
        {
            context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
            {
                Id = "full",
                RepositoryId = "repo-a",
                BranchId = "b1",
                OwnerType = RepositoryGenerationLockOwnerType.BranchTask,
                OwnerId = "full-b1",
                Scope = RepositoryGenerationLockScope.Branch
            });
            await context.SaveChangesAsync();
        }

        var service = new Mock<IIncrementalUpdateService>(MockBehavior.Strict);

        await RunAsync(service.Object, "inc-1");

        await using var verification = _database.Open();
        Assert.Equal(IncrementalUpdateStatus.Pending, (await verification.IncrementalUpdateTasks.SingleAsync()).Status);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProcessTask_WhenDeletedFilesNeedAFullGeneration_ReplacesTheTaskWithAFullGenerationTask()
    {
        await SeedAsync(("b1", "inc-1"));
        var service = new Mock<IIncrementalUpdateService>();
        service
            .Setup(x => x.ProcessIncrementalUpdateAsync("repo-a", "b1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IncrementalUpdateResult
            {
                Success = false,
                RequiresFullGeneration = true,
                ErrorMessage = "Deleted files need a full generation of the branch"
            });

        await RunAsync(service.Object, "inc-1");

        await using var verification = _database.Open();
        var incremental = await verification.IncrementalUpdateTasks.SingleAsync();
        Assert.Equal(IncrementalUpdateStatus.Cancelled, incremental.Status);
        Assert.Contains("full generation", incremental.ErrorMessage);
        var full = await verification.BranchGenerationTasks.SingleAsync();
        Assert.Equal("b1", full.BranchId);
        Assert.Equal(BranchGenerationTaskStatus.Pending, full.Status);
        Assert.Equal("user-5", full.RequestedBy);
        var reservation = await verification.RepositoryGenerationLocks.SingleAsync();
        Assert.Equal(full.Id, reservation.OwnerId);
        Assert.Equal("b1", reservation.BranchId);
    }

    // ---- helpers ----

    private async Task RunAsync(IIncrementalUpdateService updateService, string taskId)
    {
        var wikiOptions = new WikiGeneratorOptions { MaxConcurrentGenerations = 2 };
        var optionsMonitor = new StaticOptionsMonitor<WikiGeneratorOptions>(wikiOptions);
        var services = new ServiceCollection();
        services.AddScoped<IContext>(_ => _database.Open());
        services.AddSingleton(new WikiGenerationInstanceIdentity("worker-instance"));
        services.AddSingleton<IOptionsMonitor<WikiGeneratorOptions>>(optionsMonitor);
        services.AddScoped<IRepositoryGenerationLockService, RepositoryGenerationLockService>();
        services.AddScoped<IWikiGenerationCoordinator, WikiGenerationCoordinator>();
        services.AddScoped<IBranchFullGenerationCleaner, BranchFullGenerationCleaner>();
        services.AddScoped<IBranchGenerationTaskService, BranchGenerationTaskService>();
        services.AddSingleton(updateService);
        using var provider = services.BuildServiceProvider();

        var worker = new IncrementalUpdateWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IncrementalUpdateWorker>.Instance,
            Options.Create(new IncrementalUpdateOptions()));

        await using var context = _database.Open();
        var pending = await context.IncrementalUpdateTasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        var method = typeof(IncrementalUpdateWorker).GetMethod("ProcessSingleTaskAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var scope = provider.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<IWikiGenerationCoordinator>();
        await (Task<bool>)method.Invoke(worker, [coordinator, wikiOptions, pending, CancellationToken.None])!;
    }

    private async Task SeedAsync(params (string BranchId, string? TaskId)[] branches)
    {
        await using var context = _database.Open();
        context.Repositories.Add(new Repository
        {
            Id = "repo-a",
            OwnerUserId = "user-1",
            GitUrl = "https://github.com/acme/widgets.git",
            OrgName = "acme",
            RepoName = "widgets",
            Status = RepositoryStatus.Completed
        });
        foreach (var (branchId, taskId) in branches)
        {
            context.RepositoryBranches.Add(new RepositoryBranch
            {
                Id = branchId,
                RepositoryId = "repo-a",
                BranchName = branchId,
                LastCommitId = "old-sha"
            });
            if (taskId is not null)
            {
                context.IncrementalUpdateTasks.Add(new IncrementalUpdateTask
                {
                    Id = taskId,
                    RepositoryId = "repo-a",
                    BranchId = branchId,
                    Status = IncrementalUpdateStatus.Pending,
                    IsManualTrigger = true,
                    RequestedBy = "user-5"
                });
            }
        }

        await context.SaveChangesAsync();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
