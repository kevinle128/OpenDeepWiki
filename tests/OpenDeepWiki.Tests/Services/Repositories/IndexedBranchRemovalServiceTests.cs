using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Postgresql;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Tests.Infrastructure;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

/// <summary>
/// Branch removal on a real SQLite database: only data of that branch goes, active work blocks the removal
/// without any change, and the workspace is deleted after the commit.
/// </summary>
public sealed class IndexedBranchRemovalServiceTests : IDisposable
{
    private readonly SqliteScratchDatabase _database;
    private Func<MasterDbContext> _open;
    private readonly string _root = Path.Combine(TestPaths.ScratchRoot, $"removal-{Guid.NewGuid():N}");

    public IndexedBranchRemovalServiceTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
        _open = _database.Open;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _database.Dispose();
        MakeWritable(_root);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- scoped removal ----

    [Fact]
    public async Task RemoveAsync_OfAnIdleBranch_RemovesOnlyThatBranchesData()
    {
        await SeedAsync("b1", "b2");

        var result = await Service("user-2").RemoveAsync("repo-a", "b1", CancellationToken.None);

        Assert.Equal("b1", result.BranchName);
        await using var context = _open();
        Assert.Equal(["b2"], await context.RepositoryBranches.Select(item => item.Id).ToListAsync());
        Assert.Equal(["b2-lang"], await context.BranchLanguages.Select(item => item.Id).ToListAsync());
        Assert.Equal(["b2-cat-child", "b2-cat-root"], (await context.DocCatalogs.Select(item => item.Id).ToListAsync()).Order());
        Assert.Equal(["b2-doc"], await context.DocFiles.Select(item => item.Id).ToListAsync());
        Assert.Equal(["b2-translation"], await context.TranslationTasks.Select(item => item.Id).ToListAsync());
        Assert.Equal(["b2-inc"], await context.IncrementalUpdateTasks.Select(item => item.Id).ToListAsync());
        Assert.Equal(["b2-full"], await context.BranchGenerationTasks.Select(item => item.Id).ToListAsync());
        Assert.Equal(["b2-graph"], await context.GraphifyArtifacts.Select(item => item.Id).ToListAsync());
        Assert.Equal(["b2-log", "repo-log"], (await context.RepositoryProcessingLogs.Select(item => item.Id).ToListAsync()).Order());
    }

    [Fact]
    public async Task RemoveAsync_KeepsTheRepositoryItsConnectionAndRepositoryWideRecords()
    {
        await SeedAsync("b1", "b2");

        await Service().RemoveAsync("repo-a", "b1", CancellationToken.None);

        await using var context = _open();
        var repository = await context.Repositories.SingleAsync();
        Assert.False(repository.IsDeleted);
        Assert.Equal("connection-1", repository.GitConnectionId);
        Assert.False((await context.GitConnections.SingleAsync()).IsDeleted);
        Assert.Single(await context.UserBookmarks.ToListAsync());
        Assert.Single(await context.UserSubscriptions.ToListAsync());
        Assert.Single(await context.RepositoryAssignments.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_OfTheLastBranch_KeepsTheRepositoryAndTheConnection()
    {
        await SeedAsync("b1");

        await Service().RemoveAsync("repo-a", "b1", CancellationToken.None);

        await using var context = _open();
        Assert.Empty(await context.RepositoryBranches.ToListAsync());
        Assert.Single(await context.Repositories.ToListAsync());
        Assert.Single(await context.GitConnections.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_RecordsTheActorInTheAudit()
    {
        await SeedAsync("b1");

        await Service("user-2").RemoveAsync("repo-a", "b1", CancellationToken.None);

        await using var context = _open();
        var audit = await context.GitConnectionAuditEvents.SingleAsync();
        Assert.Equal(GitConnectionAuditEventType.BranchRemoved, audit.EventType);
        Assert.Equal("user-2", audit.ActorUserId);
        Assert.Equal("repo-a", audit.RepositoryId);
    }

    [PostgresFact]
    public async Task Postgres_RemoveAsync_OfAnIdleBranch_RemovesOnlyThatBranchesDataAndRespectsForeignKeys()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        _open = () => new PostgresqlDbContext(new DbContextOptionsBuilder<PostgresqlDbContext>().UseNpgsql(database.ConnectionString).Options);
        await using (var prepare = _open())
        {
            await prepare.Database.EnsureCreatedAsync();
            prepare.Users.AddRange(
                new User { Id = "user-1", Name = "user-1", Email = "user-1@example.com" },
                new User { Id = "user-2", Name = "user-2", Email = "user-2@example.com" });
            await prepare.SaveChangesAsync();
        }

        await SeedAsync("b1", "b2");
        await using (var context = _open())
        {
            (await context.BranchGenerationTasks.SingleAsync(item => item.Id == "b1-full")).Status = BranchGenerationTaskStatus.Pending;
            context.RepositoryGenerationLocks.Add(BranchLock("lock-1", "b1", "b1-full", BranchGenerationTaskStatus.Pending));
            await context.SaveChangesAsync();
        }

        await Service("user-2").RemoveAsync("repo-a", "b1", CancellationToken.None);

        await using var verification = _open();
        Assert.Equal(["b2"], await verification.RepositoryBranches.Select(item => item.Id).ToListAsync());
        Assert.Equal(["b2-cat-child", "b2-cat-root"], (await verification.DocCatalogs.Select(item => item.Id).ToListAsync()).Order());
        Assert.Empty(await verification.RepositoryGenerationLocks.ToListAsync());
        Assert.Equal(["b2-log", "repo-log"], (await verification.RepositoryProcessingLogs.Select(item => item.Id).ToListAsync()).Order());
        Assert.Single(await verification.Repositories.ToListAsync());
        Assert.Single(await verification.GitConnections.ToListAsync());
    }

    // ---- pending and processing work ----

    [Fact]
    public async Task RemoveAsync_WithPendingWork_CancelsItReleasesTheLockAndRemovesTheBranch()
    {
        await SeedAsync("b1", "b2");
        await using (var context = _open())
        {
            (await context.BranchGenerationTasks.SingleAsync(item => item.Id == "b1-full")).Status = BranchGenerationTaskStatus.Pending;
            (await context.IncrementalUpdateTasks.SingleAsync(item => item.Id == "b1-inc")).Status = IncrementalUpdateStatus.Pending;
            context.RepositoryGenerationLocks.AddRange(
                BranchLock("lock-1", "b1", "b1-full", BranchGenerationTaskStatus.Pending),
                BranchLock("lock-2", "b2", "b2-full", BranchGenerationTaskStatus.Pending));
            (await context.BranchGenerationTasks.SingleAsync(item => item.Id == "b2-full")).Status = BranchGenerationTaskStatus.Pending;
            await context.SaveChangesAsync();
        }

        await Service().RemoveAsync("repo-a", "b1", CancellationToken.None);

        await using var verification = _open();
        Assert.Equal(["b2"], await verification.RepositoryBranches.Select(item => item.Id).ToListAsync());
        Assert.Equal(["b2-full"], await verification.BranchGenerationTasks.Select(item => item.Id).ToListAsync());
        Assert.Equal(BranchGenerationTaskStatus.Pending, (await verification.BranchGenerationTasks.SingleAsync()).Status);
        Assert.Equal(["lock-2"], await verification.RepositoryGenerationLocks.Select(item => item.Id).ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_WhileAFullTaskIsProcessing_Returns409AndChangesNothing()
    {
        await SeedAsync("b1", "b2");
        await using (var context = _open())
        {
            (await context.BranchGenerationTasks.SingleAsync(item => item.Id == "b1-full")).Status = BranchGenerationTaskStatus.Processing;
            (await context.IncrementalUpdateTasks.SingleAsync(item => item.Id == "b1-inc")).Status = IncrementalUpdateStatus.Pending;
            context.RepositoryGenerationLocks.Add(BranchLock("lock-1", "b1", "b1-full", BranchGenerationTaskStatus.Processing));
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<IndexedBranchRemovalException>(
            () => Service().RemoveAsync("repo-a", "b1", CancellationToken.None));

        Assert.Equal(IndexedBranchRemovalErrorCodes.BranchJobActive, exception.ErrorCode);
        await using var verification = _open();
        Assert.Equal(2, await verification.RepositoryBranches.CountAsync());
        Assert.Equal(IncrementalUpdateStatus.Pending, (await verification.IncrementalUpdateTasks.SingleAsync(item => item.Id == "b1-inc")).Status);
        Assert.Equal(2, await verification.BranchGenerationTasks.CountAsync());
        Assert.Single(await verification.RepositoryGenerationLocks.ToListAsync());
        Assert.Equal(2, await verification.DocCatalogs.CountAsync(item => item.BranchLanguageId == "b1-lang"));
        Assert.Empty(await verification.GitConnectionAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_WhileAnIncrementalTaskIsProcessing_Returns409AndChangesNothing()
    {
        await SeedAsync("b1");
        await using (var context = _open())
        {
            (await context.IncrementalUpdateTasks.SingleAsync(item => item.Id == "b1-inc")).Status = IncrementalUpdateStatus.Processing;
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<IndexedBranchRemovalException>(
            () => Service().RemoveAsync("repo-a", "b1", CancellationToken.None));

        Assert.Equal(IndexedBranchRemovalErrorCodes.BranchJobActive, exception.ErrorCode);
        await using var verification = _open();
        Assert.Single(await verification.RepositoryBranches.ToListAsync());
        Assert.Single(await verification.BranchLanguages.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_WhileAnotherBranchIsProcessing_StillRemovesTheIdleOne()
    {
        await SeedAsync("b1", "b2");
        await using (var context = _open())
        {
            (await context.BranchGenerationTasks.SingleAsync(item => item.Id == "b2-full")).Status = BranchGenerationTaskStatus.Processing;
            await context.SaveChangesAsync();
        }

        await Service().RemoveAsync("repo-a", "b1", CancellationToken.None);

        await using var verification = _open();
        Assert.Equal(["b2"], await verification.RepositoryBranches.Select(item => item.Id).ToListAsync());
    }

    // ---- guards ----

    [Fact]
    public async Task RemoveAsync_WhenAnonymous_Fails()
    {
        await SeedAsync("b1");

        var exception = await Assert.ThrowsAsync<IndexedBranchRemovalException>(
            () => Service(userId: null).RemoveAsync("repo-a", "b1", CancellationToken.None));

        Assert.Equal(IndexedBranchRemovalErrorCodes.Unauthorized, exception.ErrorCode);
        await using var context = _open();
        Assert.Single(await context.RepositoryBranches.ToListAsync());
    }

    [Fact]
    public async Task RemoveAsync_ForUnknownRepositoryOrBranchOrAForeignBranch_Fails()
    {
        await SeedAsync("b1");
        await SeedSecondRepositoryAsync();
        var service = Service();

        var missingRepository = await Assert.ThrowsAsync<IndexedBranchRemovalException>(
            () => service.RemoveAsync("missing", "b1", CancellationToken.None));
        var missingBranch = await Assert.ThrowsAsync<IndexedBranchRemovalException>(
            () => service.RemoveAsync("repo-a", "missing", CancellationToken.None));
        var foreignBranch = await Assert.ThrowsAsync<IndexedBranchRemovalException>(
            () => service.RemoveAsync("repo-a", "other-branch", CancellationToken.None));

        Assert.Equal(IndexedBranchRemovalErrorCodes.RepositoryNotFound, missingRepository.ErrorCode);
        Assert.Equal(IndexedBranchRemovalErrorCodes.BranchNotFound, missingBranch.ErrorCode);
        Assert.Equal(IndexedBranchRemovalErrorCodes.BranchNotFound, foreignBranch.ErrorCode);
        await using var context = _open();
        Assert.Equal(2, await context.RepositoryBranches.CountAsync());
    }

    [Fact]
    public async Task RemoveAsync_ThenAddingTheSameBranchAgain_HasNoUniqueKeyFailure()
    {
        await SeedAsync("b1");
        await Service().RemoveAsync("repo-a", "b1", CancellationToken.None);

        await using var context = _open();
        context.RepositoryBranches.Add(new RepositoryBranch { Id = "b1-again", RepositoryId = "repo-a", BranchName = "b1" });
        await context.SaveChangesAsync();

        Assert.Equal("b1-again", (await context.RepositoryBranches.SingleAsync()).Id);
    }

    // ---- workspace ----

    [Fact]
    public async Task RemoveAsync_DeletesTheBranchWorkspaceAndKeepsTheOthers()
    {
        await SeedAsync("b1", "b2");
        var removed = CreateWorkspace("b1");
        var kept = CreateWorkspace("b2");

        var result = await Service().RemoveAsync("repo-a", "b1", CancellationToken.None);

        Assert.True(result.WorkspaceRemoved);
        Assert.False(Directory.Exists(Directory.GetParent(removed)!.FullName));
        Assert.True(File.Exists(Path.Combine(kept, "file.txt")));
        Assert.False(PendingCleanupMarkerExists());
    }

    [Fact]
    public async Task RemoveAsync_WhenNoWorkspaceExists_Succeeds()
    {
        await SeedAsync("b1");

        var result = await Service().RemoveAsync("repo-a", "b1", CancellationToken.None);

        Assert.True(result.WorkspaceRemoved);
    }

    [Fact]
    public async Task RemoveAsync_WhenTheWorkspaceCannotBeDeleted_KeepsTheDatabaseRemovalAndRetriesLater()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await SeedAsync("b1");
        var tree = CreateWorkspace("b1");
        var branchesDirectory = Directory.GetParent(Directory.GetParent(tree)!.FullName)!.FullName;
        File.SetUnixFileMode(branchesDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        RemoveIndexedBranchResponseHolder holder;
        try
        {
            var result = await Service().RemoveAsync("repo-a", "b1", CancellationToken.None);
            holder = new RemoveIndexedBranchResponseHolder(result.WorkspaceRemoved);
        }
        finally
        {
            File.SetUnixFileMode(branchesDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.False(holder.WorkspaceRemoved);
        await using (var context = _open())
        {
            Assert.Empty(await context.RepositoryBranches.ToListAsync());
        }

        Assert.True(Directory.Exists(tree));

        var cleaned = await Service().CleanupRemovedWorkspacesAsync("repo-a", CancellationToken.None);

        Assert.Equal(1, cleaned);
        Assert.False(Directory.Exists(tree));
        Assert.False(PendingCleanupMarkerExists());
    }

    [Fact]
    public async Task RemoveAsync_ForAPrivateLegacyRepositoryOfAnotherUser_ReportsItAsMissingAndChangesNothing()
    {
        await SeedAsync("b1");
        await using (var context = _open())
        {
            var repository = await context.Repositories.SingleAsync();
            repository.IsPublic = false;
            repository.OwnerUserId = "user-1";
            repository.GitConnectionId = null;
            await context.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<IndexedBranchRemovalException>(
            () => Service("user-2").RemoveAsync("repo-a", "b1", CancellationToken.None));

        Assert.Equal(IndexedBranchRemovalErrorCodes.RepositoryNotFound, exception.ErrorCode);
        await using var verification = _open();
        Assert.Single(await verification.RepositoryBranches.ToListAsync());
        await Service("user-1").RemoveAsync("repo-a", "b1", CancellationToken.None);
    }

    [Fact]
    public async Task CleanupAllRemovedWorkspacesAsync_WithoutASignedInUser_RemovesPendingDirectoriesOfEveryRepository()
    {
        await SeedAsync("b1");
        var pendingTree = CreateWorkspace("gone");
        var marker = Path.Combine(_root, "acme", "widgets", ".pending-workspace-removals");
        File.WriteAllText(marker, "gone" + Environment.NewLine);
        var activeTree = CreateWorkspace("b1");

        var cleaned = await Service(userId: null).CleanupAllRemovedWorkspacesAsync(CancellationToken.None);

        Assert.Equal(1, cleaned);
        Assert.False(Directory.Exists(pendingTree));
        Assert.True(Directory.Exists(activeTree));
        Assert.False(PendingCleanupMarkerExists());
    }

    [Fact]
    public async Task CleanupAllRemovedWorkspacesAsync_WhenNothingIsPending_ReturnsZero()
    {
        await SeedAsync("b1");

        Assert.Equal(0, await Service(userId: null).CleanupAllRemovedWorkspacesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_WhenAnotherActiveBranchSharesTheSanitizedWorkspacePath_KeepsTheDirectory()
    {
        await SeedAsync("b1", "b2");
        await using (var context = _open())
        {
            // "feature/x" and "feature_x" map to the same workspace directory.
            (await context.RepositoryBranches.SingleAsync(item => item.Id == "b1")).BranchName = "feature/x";
            (await context.RepositoryBranches.SingleAsync(item => item.Id == "b2")).BranchName = "feature_x";
            await context.SaveChangesAsync();
        }

        var shared = Path.Combine(_root, "acme", "widgets", "branches", "feature_x", "tree");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "file.txt"), "shared");

        var result = await Service().RemoveAsync("repo-a", "b1", CancellationToken.None);

        Assert.False(result.WorkspaceRemoved);
        Assert.True(File.Exists(Path.Combine(shared, "file.txt")));
    }

    [Theory]
    [InlineData("/data", "/data/acme/widgets/branches/main", true)]
    [InlineData("/data", "/data/../etc/passwd", false)]
    [InlineData("/data", "/data-evil/acme", false)]
    [InlineData("/data", "/data", false)]
    [InlineData("/data/", "/data/acme", true)]
    [InlineData("/data", "/other/data/acme", false)]
    public void IsStrictlyInside_ComparesFullPathsAndNeverAcceptsTheRootItself(string root, string path, bool expected)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, IndexedBranchRemovalService.IsStrictlyInside(root, path));
    }

    // ---- helpers ----

    private IndexedBranchRemovalService Service(string? userId = "user-1")
    {
        var context = _open();
        return new IndexedBranchRemovalService(
            context,
            new StubUserContext(userId),
            new BranchActionAuditor(context, NullLogger<BranchActionAuditor>.Instance),
            Options.Create(new RepositoryAnalyzerOptions { RepositoriesDirectory = _root }),
            NullLogger<IndexedBranchRemovalService>.Instance);
    }

    private string CreateWorkspace(string branchName)
    {
        var tree = RepositoryWorkspacePath.ForBranch(
            new RepositoryAnalyzerOptions { RepositoriesDirectory = _root }, "acme", "widgets", branchName);
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "file.txt"), "content");
        return tree;
    }

    private bool PendingCleanupMarkerExists()
        => File.Exists(Path.Combine(_root, "acme", "widgets", ".pending-workspace-removals"));

    private static void MakeWritable(string path)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Prepend(path))
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static RepositoryGenerationLock BranchLock(string id, string branchId, string taskId, BranchGenerationTaskStatus status) => new()
    {
        Id = id,
        RepositoryId = "repo-a",
        BranchId = branchId,
        OwnerType = RepositoryGenerationLockOwnerType.BranchTask,
        OwnerId = taskId,
        Scope = RepositoryGenerationLockScope.Branch,
        InstanceId = status == BranchGenerationTaskStatus.Processing ? "worker" : null,
        HeartbeatAt = status == BranchGenerationTaskStatus.Processing ? DateTime.UtcNow : null
    };

    private async Task SeedSecondRepositoryAsync()
    {
        await using var context = _open();
        context.Repositories.Add(new Repository
        {
            Id = "repo-b", OwnerUserId = "user-1", GitUrl = "https://github.com/acme/other.git", OrgName = "acme", RepoName = "other",
            Status = RepositoryStatus.Completed
        });
        context.RepositoryBranches.Add(new RepositoryBranch { Id = "other-branch", RepositoryId = "repo-b", BranchName = "main" });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Repository "repo-a" with a connection, assignment, bookmark, subscription, a repository-wide log,
    /// and for every branch: a language, a two-level catalog, a document, a translation task, completed full and
    /// incremental tasks, a graph artifact, and a log.
    /// </summary>
    private async Task SeedAsync(params string[] branchIds)
    {
        await using var context = _open();
        context.GitConnections.Add(new GitConnection
        {
            Id = "connection-1", Provider = GitProvider.GitHub, NormalizedServerUrl = "https://github.com",
            ExternalAccountId = "1", DisplayName = "octocat", ProtectedToken = "protected", CreatedByUserId = "user-1"
        });
        context.Departments.Add(new Department { Id = "dept-1", Name = "dept" });
        context.Repositories.Add(new Repository
        {
            Id = "repo-a", OwnerUserId = "user-1", GitUrl = "https://github.com/acme/widgets.git", OrgName = "acme",
            RepoName = "widgets", Status = RepositoryStatus.Completed, GitConnectionId = "connection-1"
        });
        context.UserBookmarks.Add(new UserBookmark { Id = "bookmark", UserId = "user-1", RepositoryId = "repo-a" });
        context.UserSubscriptions.Add(new UserSubscription { Id = "subscription", UserId = "user-1", RepositoryId = "repo-a" });
        context.RepositoryAssignments.Add(new RepositoryAssignment
        {
            Id = "assignment", RepositoryId = "repo-a", DepartmentId = "dept-1", AssigneeUserId = "user-1"
        });
        context.RepositoryProcessingLogs.Add(new RepositoryProcessingLog { Id = "repo-log", RepositoryId = "repo-a", Message = "repository" });
        await context.SaveChangesAsync();

        foreach (var id in branchIds)
        {
            context.RepositoryBranches.Add(new RepositoryBranch
            {
                Id = id, RepositoryId = "repo-a", BranchName = id
            });
        }

        await context.SaveChangesAsync();

        foreach (var id in branchIds)
        {
            context.BranchLanguages.Add(new BranchLanguage { Id = $"{id}-lang", RepositoryBranchId = id, LanguageCode = "zh" });
        }

        await context.SaveChangesAsync();

        foreach (var id in branchIds)
        {
            context.DocFiles.Add(new DocFile { Id = $"{id}-doc", BranchLanguageId = $"{id}-lang", Content = "# doc" });
            context.DocCatalogs.Add(new DocCatalog { Id = $"{id}-cat-root", BranchLanguageId = $"{id}-lang", Title = "root", Path = "root", DocFileId = $"{id}-doc" });
        }

        await context.SaveChangesAsync();

        foreach (var id in branchIds)
        {
            context.DocCatalogs.Add(new DocCatalog
            {
                Id = $"{id}-cat-child", BranchLanguageId = $"{id}-lang", ParentId = $"{id}-cat-root", Title = "child", Path = "root/child"
            });
            context.TranslationTasks.Add(new TranslationTask
            {
                Id = $"{id}-translation", RepositoryId = "repo-a", RepositoryBranchId = id,
                SourceBranchLanguageId = $"{id}-lang", TargetLanguageCode = "en"
            });
            context.IncrementalUpdateTasks.Add(new IncrementalUpdateTask
            {
                Id = $"{id}-inc", RepositoryId = "repo-a", BranchId = id, Status = IncrementalUpdateStatus.Completed
            });
            context.BranchGenerationTasks.Add(new BranchGenerationTask
            {
                Id = $"{id}-full", RepositoryId = "repo-a", BranchId = id, Status = BranchGenerationTaskStatus.Completed
            });
            context.GraphifyArtifacts.Add(new GraphifyArtifact { Id = $"{id}-graph", RepositoryId = "repo-a", RepositoryBranchId = id });
        }

        await context.SaveChangesAsync();

        foreach (var id in branchIds)
        {
            context.RepositoryProcessingLogs.Add(new RepositoryProcessingLog
            {
                Id = $"{id}-log", RepositoryId = "repo-a", BranchId = id, GenerationTaskId = $"{id}-full", Message = id
            });
        }

        await context.SaveChangesAsync();
    }

    private sealed record RemoveIndexedBranchResponseHolder(bool WorkspaceRemoved);

    private sealed class StubUserContext(string? userId) : IUserContext
    {
        public string? UserId => userId;
        public string? UserName => userId;
        public string? Email => null;
        public bool IsAuthenticated => userId is not null;
        public ClaimsPrincipal? User => new ClaimsPrincipal(new ClaimsIdentity());
    }
}
