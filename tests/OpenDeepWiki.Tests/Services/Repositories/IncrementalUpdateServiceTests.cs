using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Notifications;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public class IncrementalUpdateServiceTests
{
    [Fact]
    public async Task TriggerManualUpdateAsync_CreatesTaskEvenWhenNoRemoteChangeInformationExists()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "same-sha");
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var taskId = await service.TriggerManualUpdateAsync(repository.Id, branch.Id);

        var task = await context.IncrementalUpdateTasks.SingleAsync(t => t.Id == taskId);
        Assert.True(task.IsManualTrigger);
        Assert.Equal(IncrementalUpdateStatus.Pending, task.Status);
        Assert.Equal("same-sha", task.PreviousCommitId);
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WhenHeadUnchanged_ReturnsSuccessWithoutDocumentUpdates()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "same-sha");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "same-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                CommitId = "same-sha",
                PreviousCommitId = "same-sha"
            });

        var service = CreateService(context, analyzer: analyzer);

        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id);

        Assert.True(result.Success);
        Assert.Equal(0, result.ChangedFilesCount);
        Assert.Equal(0, result.UpdatedDocumentsCount);
        analyzer.Verify(
            x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "same-sha", It.IsAny<CancellationToken>()),
            Times.Once);
        analyzer.Verify(
            x => x.GetChangedFilesAsync(It.IsAny<RepositoryWorkspace>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WhenCommitChanges_PreparesWorkspaceOnceAndUpdatesBranch()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "old-sha");
        SeedBranchLanguage(context, branch.Id, "zh");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "old-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                CommitId = "new-sha",
                PreviousCommitId = "old-sha"
            });
        analyzer
            .Setup(x => x.GetChangedFilesAsync(
                It.IsAny<RepositoryWorkspace>(),
                "old-sha",
                "new-sha",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["src/app.cs"]);
        SetupNoDeletedFiles(analyzer, "old-sha", "new-sha");

        var wikiGenerator = new Mock<IWikiGenerator>(MockBehavior.Strict);
        wikiGenerator
            .Setup(x => x.IncrementalUpdateAsync(
                It.IsAny<RepositoryWorkspace>(),
                It.IsAny<BranchLanguage>(),
                It.Is<string[]>(files => files.SequenceEqual(new[] { "src/app.cs" })),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var notificationService = new Mock<ISubscriberNotificationService>(MockBehavior.Strict);
        notificationService
            .Setup(x => x.NotifySubscribersAsync(
                It.Is<RepositoryUpdateNotification>(n => n.RepositoryId == repository.Id && n.CommitId == "new-sha"),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = CreateService(
            context,
            analyzer: analyzer,
            wikiGenerator: wikiGenerator,
            notificationService: notificationService);

        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id);

        Assert.True(result.Success);
        Assert.Equal(1, result.ChangedFilesCount);
        Assert.Equal(1, result.UpdatedDocumentsCount);

        var updatedBranch = await context.RepositoryBranches.SingleAsync(b => b.Id == branch.Id);
        Assert.Equal("new-sha", updatedBranch.LastCommitId);
        Assert.NotNull(updatedBranch.LastProcessedAt);

        analyzer.Verify(
            x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "old-sha", It.IsAny<CancellationToken>()),
            Times.Once);
        wikiGenerator.VerifyAll();
        notificationService.VerifyAll();
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WhenCommitAdvancesWithoutChangedFiles_StillAdvancesStoredCommit()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "old-sha");
        await context.SaveChangesAsync();

        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "old-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace
            {
                Organization = repository.OrgName,
                RepositoryName = repository.RepoName,
                BranchName = branch.BranchName,
                WorkingDirectory = "C:\\temp\\repo",
                CommitId = "new-sha",
                PreviousCommitId = "old-sha"
            });
        analyzer
            .Setup(x => x.GetChangedFilesAsync(
                It.IsAny<RepositoryWorkspace>(),
                "old-sha",
                "new-sha",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        SetupNoDeletedFiles(analyzer, "old-sha", "new-sha");

        var service = CreateService(context, analyzer: analyzer);

        var result = await service.ProcessIncrementalUpdateAsync(repository.Id, branch.Id);

        Assert.True(result.Success);
        Assert.Equal(0, result.ChangedFilesCount);

        var updatedBranch = await context.RepositoryBranches.SingleAsync(b => b.Id == branch.Id);
        Assert.Equal("new-sha", updatedBranch.LastCommitId);
        Assert.NotNull(updatedBranch.LastProcessedAt);
    }

    [Fact]
    public async Task TriggerManualUpdateAsync_RecordsTheRequestingUser()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "sha");
        await context.SaveChangesAsync();

        var taskId = await CreateService(context).TriggerManualUpdateAsync(repository.Id, branch.Id, requestedBy: "user-7");

        Assert.Equal("user-7", (await context.IncrementalUpdateTasks.SingleAsync(item => item.Id == taskId)).RequestedBy);
    }

    [Fact]
    public async Task TriggerManualUpdateAsync_WhenBranchBelongsToAnotherRepository_RejectsAndCreatesNoTask()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var other = SeedRepository(context, generateSkill: false);
        other.OrgName = "other";
        var foreignBranch = SeedBranch(context, other.Id, "main", "sha");
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<IncrementalUpdateRejectedException>(
            () => CreateService(context).TriggerManualUpdateAsync(repository.Id, foreignBranch.Id));

        Assert.Equal(IncrementalUpdateErrorCodes.BranchNotFound, exception.ErrorCode);
        Assert.Empty(await context.IncrementalUpdateTasks.ToListAsync());
    }

    [Fact]
    public async Task TriggerManualUpdateAsync_WhenBranchOrRepositoryIsMissing_RejectsAndCreatesNoTask()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        await context.SaveChangesAsync();
        var service = CreateService(context);

        var missingBranch = await Assert.ThrowsAsync<IncrementalUpdateRejectedException>(
            () => service.TriggerManualUpdateAsync(repository.Id, "missing"));
        var missingRepository = await Assert.ThrowsAsync<IncrementalUpdateRejectedException>(
            () => service.TriggerManualUpdateAsync("missing", "missing"));

        Assert.Equal(IncrementalUpdateErrorCodes.BranchNotFound, missingBranch.ErrorCode);
        Assert.Equal(IncrementalUpdateErrorCodes.RepositoryNotFound, missingRepository.ErrorCode);
        Assert.Empty(await context.IncrementalUpdateTasks.ToListAsync());
    }

    [Fact]
    public async Task TriggerManualUpdateAsync_WhenFullGenerationOfTheBranchIsActive_RejectsWithStableCode()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "sha");
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "full",
            RepositoryId = repository.Id,
            BranchId = branch.Id,
            Status = BranchGenerationTaskStatus.Pending
        });
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<IncrementalUpdateRejectedException>(
            () => CreateService(context).TriggerManualUpdateAsync(repository.Id, branch.Id));

        Assert.Equal(IncrementalUpdateErrorCodes.BranchGenerationActive, exception.ErrorCode);
    }

    [Fact]
    public async Task TriggerManualUpdateAsync_WhenFullGenerationOfAnotherBranchIsActive_StillCreatesTheTask()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "sha");
        var other = SeedBranch(context, repository.Id, "other", "sha");
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "full",
            RepositoryId = repository.Id,
            BranchId = other.Id,
            Status = BranchGenerationTaskStatus.Processing
        });
        await context.SaveChangesAsync();

        var taskId = await CreateService(context).TriggerManualUpdateAsync(repository.Id, branch.Id);

        Assert.NotNull(await context.IncrementalUpdateTasks.SingleAsync(item => item.Id == taskId));
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WhenBranchBelongsToAnotherRepository_FailsWithoutPreparingAWorkspace()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var other = SeedRepository(context, generateSkill: false);
        other.OrgName = "other";
        var foreignBranch = SeedBranch(context, other.Id, "main", "old-sha");
        await context.SaveChangesAsync();
        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);

        var result = await CreateService(context, analyzer: analyzer).ProcessIncrementalUpdateAsync(repository.Id, foreignBranch.Id);

        Assert.False(result.Success);
        Assert.False(result.RequiresFullGeneration);
        analyzer.VerifyNoOtherCalls();
        Assert.Equal("old-sha", (await context.RepositoryBranches.SingleAsync()).LastCommitId);
    }

    [Fact]
    public async Task ProcessIncrementalUpdateAsync_WhenSourceFilesWereDeleted_AsksForFullGenerationAndKeepsTheBaseline()
    {
        using var context = CreateContext();
        var repository = SeedRepository(context, generateSkill: false);
        var branch = SeedBranch(context, repository.Id, "main", "old-sha");
        SeedBranchLanguage(context, branch.Id, "zh");
        await context.SaveChangesAsync();
        var analyzer = new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        analyzer
            .Setup(x => x.PrepareWorkspaceAsync(repository, branch.BranchName, "old-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoryWorkspace { CommitId = "new-sha", PreviousCommitId = "old-sha", WorkingDirectory = "C:\\temp\\repo" });
        analyzer
            .Setup(x => x.GetChangedFilesAsync(It.IsAny<RepositoryWorkspace>(), "old-sha", "new-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["src/kept.cs"]);
        analyzer
            .Setup(x => x.GetDeletedFilesAsync(It.IsAny<RepositoryWorkspace>(), "old-sha", "new-sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(["src/removed.cs"]);
        var wikiGenerator = new Mock<IWikiGenerator>(MockBehavior.Strict);

        var result = await CreateService(context, analyzer: analyzer, wikiGenerator: wikiGenerator)
            .ProcessIncrementalUpdateAsync(repository.Id, branch.Id);

        Assert.False(result.Success);
        Assert.True(result.RequiresFullGeneration);
        Assert.Equal("old-sha", (await context.RepositoryBranches.SingleAsync()).LastCommitId);
        wikiGenerator.VerifyNoOtherCalls();
    }

    private static void SetupNoDeletedFiles(Mock<IRepositoryAnalyzer> analyzer, string from, string to)
    {
        analyzer
            .Setup(x => x.GetDeletedFilesAsync(It.IsAny<RepositoryWorkspace>(), from, to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
    }

    private static IncrementalUpdateService CreateService(
        TestDbContext context,
        Mock<IRepositoryAnalyzer>? analyzer = null,
        Mock<IWikiGenerator>? wikiGenerator = null,
        Mock<ISubscriberNotificationService>? notificationService = null)
    {
        analyzer ??= new Mock<IRepositoryAnalyzer>(MockBehavior.Strict);
        wikiGenerator ??= new Mock<IWikiGenerator>(MockBehavior.Strict);
        if (notificationService == null)
        {
            notificationService = new Mock<ISubscriberNotificationService>(MockBehavior.Strict);
            notificationService
                .Setup(x => x.NotifySubscribersAsync(It.IsAny<RepositoryUpdateNotification>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        }

        return new IncrementalUpdateService(
            analyzer.Object,
            wikiGenerator.Object,
            Mock.Of<IRepositorySkillMarkdownBuilder>(),
            notificationService.Object,
            context,
            Options.Create(new IncrementalUpdateOptions()),
            Mock.Of<ILogger<IncrementalUpdateService>>());
    }

    private static TestDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new TestDbContext(options);
    }

    private static Repository SeedRepository(TestDbContext context, bool generateSkill)
    {
        var repository = new Repository
        {
            Id = Guid.NewGuid().ToString(),
            OwnerUserId = "user-1",
            GitUrl = "https://github.com/demo/repo.git",
            OrgName = "demo",
            RepoName = "repo",
            Status = RepositoryStatus.Completed,
            GenerateSkill = generateSkill
        };

        context.Repositories.Add(repository);
        return repository;
    }

    private static RepositoryBranch SeedBranch(
        TestDbContext context,
        string repositoryId,
        string branchName,
        string? lastCommitId)
    {
        var branch = new RepositoryBranch
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryId = repositoryId,
            BranchName = branchName,
            LastCommitId = lastCommitId
        };

        context.RepositoryBranches.Add(branch);
        return branch;
    }

    private static BranchLanguage SeedBranchLanguage(
        TestDbContext context,
        string branchId,
        string languageCode)
    {
        var language = new BranchLanguage
        {
            Id = Guid.NewGuid().ToString(),
            RepositoryBranchId = branchId,
            LanguageCode = languageCode,
            IsDefault = true
        };

        context.BranchLanguages.Add(language);
        return language;
    }

    private sealed class TestDbContext(DbContextOptions<TestDbContext> options) : MasterDbContext(options)
    {
    }
}
