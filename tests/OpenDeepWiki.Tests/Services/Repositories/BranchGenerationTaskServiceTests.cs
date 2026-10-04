using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Endpoints;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class BranchGenerationTaskServiceTests
{
    [Fact]
    public async Task BranchCleaner_CleansOnlyTargetBranch()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var target = SeedBranchWithDocument(context, repository.Id, "target");
        var other = SeedBranchWithDocument(context, repository.Id, "other");
        context.IncrementalUpdateTasks.Add(new IncrementalUpdateTask
        {
            Id = "target-incremental",
            RepositoryId = repository.Id,
            BranchId = target.BranchId,
            Status = IncrementalUpdateStatus.Pending
        });
        context.IncrementalUpdateTasks.Add(new IncrementalUpdateTask
        {
            Id = "other-incremental",
            RepositoryId = repository.Id,
            BranchId = other.BranchId,
            Status = IncrementalUpdateStatus.Pending
        });
        await context.SaveChangesAsync();

        var branch = await context.RepositoryBranches.SingleAsync(item => item.Id == target.BranchId);
        await new BranchFullGenerationCleaner().CleanAsync(context, branch);
        await context.SaveChangesAsync();

        Assert.Empty(await context.DocCatalogs.Where(item => item.BranchLanguageId == target.BranchLanguageId).ToListAsync());
        Assert.Empty(await context.DocFiles.Where(item => item.BranchLanguageId == target.BranchLanguageId).ToListAsync());
        Assert.Single(await context.DocCatalogs.Where(item => item.BranchLanguageId == other.BranchLanguageId).ToListAsync());
        Assert.Single(await context.DocFiles.Where(item => item.BranchLanguageId == other.BranchLanguageId).ToListAsync());
        Assert.DoesNotContain(await context.IncrementalUpdateTasks.ToListAsync(), item => item.Id == "target-incremental");
        Assert.Contains(await context.IncrementalUpdateTasks.ToListAsync(), item => item.Id == "other-incremental");
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_WhenRepositoryLockExists_ReturnsConflict()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        context.RepositoryGenerationLocks.Add(new RepositoryGenerationLock
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repository.Id,
            OwnerType = RepositoryGenerationLockOwnerType.Repository,
            OwnerId = repository.Id,
            Scope = RepositoryGenerationLockScope.Repository
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var result = await service.EnqueueFullGenerationAsync(repository.Id, branch.BranchId);

        Assert.False(result.Success);
        Assert.Equal("GENERATION_LOCK_CONFLICT", result.ErrorCode);
        Assert.NotNull(result.ActiveLock);
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_WhenActiveTaskExists_ReturnsActiveTask()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "active-task",
            RepositoryId = repository.Id,
            BranchId = branch.BranchId,
            Status = BranchGenerationTaskStatus.Pending,
            Mode = BranchGenerationTaskMode.Full
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var result = await service.EnqueueFullGenerationAsync(repository.Id, branch.BranchId);

        Assert.False(result.Success);
        Assert.Equal("BRANCH_GENERATION_ACTIVE", result.ErrorCode);
        Assert.Equal("active-task", result.Task?.Id);
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_WhenTaskSaveFails_ReleasesRepositoryLock()
    {
        await using var context = CreateFailingContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        await context.SaveChangesAsync();
        context.ThrowOnSaveNumber = context.SaveCount + 2;
        var service = CreateService(context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.EnqueueFullGenerationAsync(repository.Id, branch.BranchId));

        Assert.Empty(await context.RepositoryGenerationLocks.ToListAsync());
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_WhenOnlyAnotherBranchHasAnActiveTask_StillEnqueues()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var busy = SeedBranchWithDocument(context, repository.Id, "busy");
        var idle = SeedBranchWithDocument(context, repository.Id, "idle");
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "busy-task",
            RepositoryId = repository.Id,
            BranchId = busy.BranchId,
            Status = BranchGenerationTaskStatus.Processing,
            Mode = BranchGenerationTaskMode.Full
        });
        context.RepositoryGenerationLocks.Add(BranchLock(repository.Id, busy.BranchId, "busy-task"));
        await context.SaveChangesAsync();

        var result = await CreateService(context).EnqueueFullGenerationAsync(repository.Id, idle.BranchId);

        Assert.True(result.Success);
        Assert.Equal(idle.BranchId, result.Task!.BranchId);
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_ForTwoBranches_CreatesTwoTasksWithTheirOwnBranchLocks()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var first = SeedBranchWithDocument(context, repository.Id, "first");
        var second = SeedBranchWithDocument(context, repository.Id, "second");
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var firstResult = await service.EnqueueFullGenerationAsync(repository.Id, first.BranchId);
        var secondResult = await service.EnqueueFullGenerationAsync(repository.Id, second.BranchId);

        Assert.True(firstResult.Success);
        Assert.True(secondResult.Success);
        var locks = await context.RepositoryGenerationLocks.ToListAsync();
        Assert.Equal(
            new[] { first.BranchId, second.BranchId }.Order(),
            locks.Select(item => item.BranchId!).Order());
        Assert.All(locks, item => Assert.Equal(RepositoryGenerationLockScope.Branch, item.Scope));
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_TwiceForOneBranch_KeepsOneActiveTask()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var first = await service.EnqueueFullGenerationAsync(repository.Id, branch.BranchId);
        var second = await service.EnqueueFullGenerationAsync(repository.Id, branch.BranchId);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal("BRANCH_GENERATION_ACTIVE", second.ErrorCode);
        Assert.Equal(first.Task!.Id, second.Task!.Id);
        Assert.Single(await context.BranchGenerationTasks.ToListAsync());
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_WhenBranchBelongsToAnotherRepository_ReturnsNotFoundAndCreatesNothing()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var other = SeedRepository(context, RepositoryStatus.Completed);
        var foreignBranch = SeedBranchWithDocument(context, other.Id, "main");
        await context.SaveChangesAsync();

        var result = await CreateService(context).EnqueueFullGenerationAsync(repository.Id, foreignBranch.BranchId);

        Assert.False(result.Success);
        Assert.Equal("BRANCH_NOT_FOUND", result.ErrorCode);
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
        Assert.Empty(await context.RepositoryGenerationLocks.ToListAsync());
    }

    [Fact]
    public async Task EnqueueFullGenerationAsync_RecordsTheRequestingUser()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        await context.SaveChangesAsync();

        var result = await CreateService(context).EnqueueFullGenerationAsync(repository.Id, branch.BranchId, "user-9");

        Assert.Equal("user-9", result.Task!.RequestedBy);
    }

    [Fact]
    public async Task CancelAsync_ReleasesOnlyTheLockOfThatBranch()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var first = SeedBranchWithDocument(context, repository.Id, "first");
        var second = SeedBranchWithDocument(context, repository.Id, "second");
        await context.SaveChangesAsync();
        var service = CreateService(context);
        var firstResult = await service.EnqueueFullGenerationAsync(repository.Id, first.BranchId);
        await service.EnqueueFullGenerationAsync(repository.Id, second.BranchId);

        var cancelled = await service.CancelAsync(firstResult.Task!.Id);

        Assert.True(cancelled.Success);
        Assert.Equal(second.BranchId, (await context.RepositoryGenerationLocks.SingleAsync()).BranchId);
    }

    [Fact]
    public async Task RetryAsync_ReservesTheLockOfItsOwnBranch()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var failed = SeedBranchWithDocument(context, repository.Id, "failed");
        var running = SeedBranchWithDocument(context, repository.Id, "running");
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "failed-task",
            RepositoryId = repository.Id,
            BranchId = failed.BranchId,
            Status = BranchGenerationTaskStatus.Failed,
            Mode = BranchGenerationTaskMode.Full
        });
        context.RepositoryGenerationLocks.Add(BranchLock(repository.Id, running.BranchId, "running-task"));
        await context.SaveChangesAsync();

        var result = await CreateService(context).RetryAsync("failed-task");

        Assert.True(result.Success);
        var locks = await context.RepositoryGenerationLocks.ToListAsync();
        Assert.Contains(locks, item => item.OwnerId == "failed-task" && item.BranchId == failed.BranchId);
        Assert.Contains(locks, item => item.OwnerId == "running-task" && item.BranchId == running.BranchId);
    }

    [Fact]
    public async Task AuthorizeBranchOperationAsync_WhenAnonymous_ReturnsUnauthorized()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        await context.SaveChangesAsync();

        var result = await BranchGenerationEndpoints.AuthorizeBranchOperationAsync(
            context,
            new TestUserContext(null, isAuthenticated: false),
            repository.Id,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, await ExecuteStatusCodeAsync(result));
    }

    [Fact]
    public async Task AuthorizeBranchOperationAsync_WhenAuthenticatedNonOwner_IsAllowed()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        await context.SaveChangesAsync();

        var result = await BranchGenerationEndpoints.AuthorizeBranchOperationAsync(
            context,
            new TestUserContext("user-2"),
            repository.Id,
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task AuthorizeBranchOperationAsync_WhenRepositoryIsMissing_ReturnsNotFound()
    {
        using var context = CreateContext();

        var result = await BranchGenerationEndpoints.AuthorizeBranchOperationAsync(
            context,
            new TestUserContext("user-2"),
            "missing",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status404NotFound, await ExecuteStatusCodeAsync(result));
    }

    [Fact]
    public async Task AuthorizeBranchTaskOperationAsync_AllowsAnyAuthenticatedUserAndRejectsAnonymous()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, RepositoryStatus.Completed);
        var branch = SeedBranchWithDocument(context, repository.Id, "main");
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "task-1",
            RepositoryId = repository.Id,
            BranchId = branch.BranchId,
            Status = BranchGenerationTaskStatus.Failed,
            Mode = BranchGenerationTaskMode.Full
        });
        await context.SaveChangesAsync();

        var anonymous = await BranchGenerationEndpoints.AuthorizeBranchTaskOperationAsync(
            context, new TestUserContext(null, isAuthenticated: false), "task-1", CancellationToken.None);
        var nonOwner = await BranchGenerationEndpoints.AuthorizeBranchTaskOperationAsync(
            context, new TestUserContext("user-2"), "task-1", CancellationToken.None);
        var missing = await BranchGenerationEndpoints.AuthorizeBranchTaskOperationAsync(
            context, new TestUserContext("user-2"), "missing", CancellationToken.None);

        Assert.NotNull(anonymous);
        Assert.Equal(StatusCodes.Status401Unauthorized, await ExecuteStatusCodeAsync(anonymous));
        Assert.Null(nonOwner);
        Assert.NotNull(missing);
        Assert.Equal(StatusCodes.Status404NotFound, await ExecuteStatusCodeAsync(missing));
    }

    private static RepositoryGenerationLock BranchLock(string repositoryId, string branchId, string ownerId) => new()
    {
        Id = Guid.NewGuid().ToString(),
        RepositoryId = repositoryId,
        BranchId = branchId,
        OwnerType = RepositoryGenerationLockOwnerType.BranchTask,
        OwnerId = ownerId,
        Scope = RepositoryGenerationLockScope.Branch
    };

    private static BranchGenerationTaskService CreateService(TestDbContext context)
    {
        return new BranchGenerationTaskService(
            context,
            new RepositoryGenerationLockService(context),
            new BranchFullGenerationCleaner());
    }

    private static Repository SeedRepository(TestDbContext context, RepositoryStatus status)
    {
        var repository = new Repository
        {
            Id = Guid.NewGuid().ToString(),
            OwnerUserId = "user-1",
            GitUrl = "https://example.com/org/repo.git",
            OrgName = "org",
            RepoName = "repo",
            Status = status
        };
        context.Repositories.Add(repository);
        return repository;
    }

    private static (string BranchId, string BranchLanguageId) SeedBranchWithDocument(
        TestDbContext context,
        string repositoryId,
        string branchName)
    {
        var branchId = Guid.NewGuid().ToString();
        var branchLanguageId = Guid.NewGuid().ToString();
        var docFileId = Guid.NewGuid().ToString();

        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repositoryId,
            BranchName = branchName,
            LastCommitId = $"{branchName}-commit",
            LastProcessedAt = DateTime.UtcNow
        });
        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = branchLanguageId,
            RepositoryBranchId = branchId,
            LanguageCode = "zh"
        });
        context.DocFiles.Add(new DocFile
        {
            Id = docFileId,
            BranchLanguageId = branchLanguageId,
            Content = "# Existing"
        });
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = Guid.NewGuid().ToString(),
            BranchLanguageId = branchLanguageId,
            Title = "Existing",
            Path = $"existing-{branchName}",
            DocFileId = docFileId
        });

        return (branchId, branchLanguageId);
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
    }

    private static TestDbContext CreateFailingContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
    }

    private static async Task<int> ExecuteStatusCodeAsync(IResult result)
    {
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .Configure<JsonOptions>(_ => { })
                .BuildServiceProvider(),
            Response =
            {
                Body = new MemoryStream()
            }
        };

        await result.ExecuteAsync(httpContext);
        return httpContext.Response.StatusCode;
    }

    private sealed class TestUserContext(string? userId, bool isAuthenticated = true, bool isAdmin = false) : IUserContext
    {
        public string? UserId { get; } = userId;
        public string? UserName => UserId;
        public string? Email => UserId is null ? null : $"{UserId}@example.com";
        public bool IsAuthenticated { get; } = isAuthenticated;
        public ClaimsPrincipal? User { get; } = CreatePrincipal(userId, isAuthenticated, isAdmin);

        private static ClaimsPrincipal CreatePrincipal(string? userId, bool isAuthenticated, bool isAdmin)
        {
            if (!isAuthenticated || string.IsNullOrWhiteSpace(userId))
            {
                return new ClaimsPrincipal(new ClaimsIdentity());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId)
            };
            if (isAdmin)
            {
                claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options)
    {
        public int SaveCount { get; private set; }

        public int? ThrowOnSaveNumber { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            if (ThrowOnSaveNumber == SaveCount)
            {
                throw new InvalidOperationException("Simulated task save failure");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
