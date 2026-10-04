using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Endpoints;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Notifications;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Endpoints;

/// <summary>
/// Full-generation and incremental-update routes through the real HTTP pipeline: every route needs a signed-in user,
/// any signed-in user may operate on any repository's branches, and each action records who started it.
/// </summary>
public sealed class BranchOperationEndpointsTests : IAsyncLifetime
{
    private const string Owner = "user-1";
    private const string Stranger = "user-2";

    private readonly SqliteScratchDatabase _database;
    private TestEndpointHost _host = null!;

    public BranchOperationEndpointsTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
    }

    public async Task InitializeAsync()
    {
        await using (var context = _database.Open())
        {
            context.GitConnections.Add(new GitConnection
            {
                Id = "connection-1",
                Provider = GitProvider.GitHub,
                NormalizedServerUrl = "https://github.com",
                ExternalAccountId = "1",
                DisplayName = "octocat",
                ProtectedToken = "protected",
                CreatedByUserId = Owner
            });
            context.Repositories.AddRange(
                Repo("repo-a", "widgets", "connection-1", isPublic: false),
                Repo("repo-b", "gadgets", null),
                Repo("repo-p", "secrets", null, isPublic: false));
            context.RepositoryBranches.AddRange(
                new RepositoryBranch { Id = "a-main", RepositoryId = "repo-a", BranchName = "main", LastCommitId = "sha" },
                new RepositoryBranch { Id = "a-dev", RepositoryId = "repo-a", BranchName = "dev", LastCommitId = "sha" },
                new RepositoryBranch { Id = "b-main", RepositoryId = "repo-b", BranchName = "main", LastCommitId = "sha" },
                new RepositoryBranch { Id = "p-main", RepositoryId = "repo-p", BranchName = "main", LastCommitId = "sha" });
            await context.SaveChangesAsync();
        }

        _host = await TestEndpointHost.StartAsync(
            services =>
            {
                services.AddScoped<IUserContext, UserContext>();
                services.AddScoped<IContext>(_ => _database.Open());
                services.AddScoped<IRepositoryGenerationLockService, RepositoryGenerationLockService>();
                services.AddScoped<IBranchFullGenerationCleaner, BranchFullGenerationCleaner>();
                services.AddScoped<IBranchGenerationTaskService, BranchGenerationTaskService>();
                services.AddScoped<IBranchActionAuditor, BranchActionAuditor>();
                services.AddScoped<IIncrementalUpdateService>(provider => new IncrementalUpdateService(
                    Mock.Of<IRepositoryAnalyzer>(),
                    Mock.Of<IWikiGenerator>(),
                    Mock.Of<IRepositorySkillMarkdownBuilder>(),
                    Mock.Of<ISubscriberNotificationService>(),
                    provider.GetRequiredService<IContext>(),
                    Options.Create(new IncrementalUpdateOptions()),
                    provider.GetRequiredService<ILogger<IncrementalUpdateService>>()));
            },
            app =>
            {
                app.MapBranchGenerationEndpoints();
                app.MapIncrementalUpdateEndpoints();
            });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _database.Dispose();
    }

    // ---- authentication ----

    [Theory]
    [InlineData("POST", "/api/v1/repositories/repo-a/branches/a-main/generation-tasks/full")]
    [InlineData("GET", "/api/v1/repositories/repo-a/branch-generation-tasks")]
    [InlineData("GET", "/api/v1/branch-generation-tasks/t1")]
    [InlineData("POST", "/api/v1/branch-generation-tasks/t1/retry")]
    [InlineData("POST", "/api/v1/branch-generation-tasks/t1/cancel")]
    [InlineData("POST", "/api/v1/repositories/repo-a/branches/a-main/incremental-update")]
    [InlineData("GET", "/api/v1/repositories/repo-a/incremental-updates")]
    [InlineData("GET", "/api/v1/incremental-updates/t1")]
    [InlineData("POST", "/api/v1/incremental-updates/t1/retry")]
    public async Task EveryBranchRoute_WhenAnonymous_Returns401AndChangesNothing(string method, string path)
    {
        var response = await _host.SendAsync(method, path, user: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        await using var context = _database.Open();
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
        Assert.Empty(await context.IncrementalUpdateTasks.ToListAsync());
        Assert.Empty(await context.RepositoryGenerationLocks.ToListAsync());
    }

    // ---- private repositories ----

    public static TheoryData<string, string> PrivateRepositoryRoutes => new()
    {
        { "GET", "/api/v1/branch-generation-tasks/p-full" },
        { "GET", "/api/v1/repositories/repo-p/branch-generation-tasks" },
        { "POST", "/api/v1/repositories/repo-p/branches/p-main/generation-tasks/full" },
        { "POST", "/api/v1/branch-generation-tasks/p-full/retry" },
        { "POST", "/api/v1/branch-generation-tasks/p-full/cancel" },
        { "GET", "/api/v1/repositories/repo-p/incremental-updates" },
        { "GET", "/api/v1/incremental-updates/p-inc" },
        { "POST", "/api/v1/incremental-updates/p-inc/retry" },
        { "POST", "/api/v1/repositories/repo-p/branches/p-main/incremental-update" }
    };

    private async Task SeedPrivateTasksAsync()
    {
        await using var context = _database.Open();
        context.BranchGenerationTasks.Add(new BranchGenerationTask
        {
            Id = "p-full", RepositoryId = "repo-p", BranchId = "p-main", Status = BranchGenerationTaskStatus.Failed
        });
        context.IncrementalUpdateTasks.Add(new IncrementalUpdateTask
        {
            Id = "p-inc", RepositoryId = "repo-p", BranchId = "p-main", Status = IncrementalUpdateStatus.Failed
        });
        await context.SaveChangesAsync();
    }

    [Theory]
    [MemberData(nameof(PrivateRepositoryRoutes))]
    public async Task PrivateRepositoryRoute_ForAUserWhoCannotSeeTheRepository_Returns404AndChangesNothing(string method, string path)
    {
        await SeedPrivateTasksAsync();
        var response = await _host.SendAsync(method, path, Stranger);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.DoesNotContain("secrets", response.Body);
        await using var context = _database.Open();
        Assert.Equal(BranchGenerationTaskStatus.Failed, (await context.BranchGenerationTasks.SingleAsync()).Status);
        Assert.Equal(IncrementalUpdateStatus.Failed, (await context.IncrementalUpdateTasks.SingleAsync()).Status);
        Assert.Empty(await context.GitConnectionAuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData("user-1", null)]
    [InlineData("admin-1", "Admin")]
    public async Task PrivateRepositoryReads_ForTheOwnerAndAnAdmin_AreAllowed(string user, string? role)
    {
        await SeedPrivateTasksAsync();
        var task = await _host.SendAsync("GET", "/api/v1/branch-generation-tasks/p-full", user, role: role);
        var tasks = await _host.SendAsync("GET", "/api/v1/repositories/repo-p/branch-generation-tasks", user, role: role);
        var incremental = await _host.SendAsync("GET", "/api/v1/incremental-updates/p-inc", user, role: role);
        var incrementalList = await _host.SendAsync("GET", "/api/v1/repositories/repo-p/incremental-updates", user, role: role);

        Assert.Equal(HttpStatusCode.OK, task.Status);
        Assert.Equal(HttpStatusCode.OK, tasks.Status);
        Assert.Equal(HttpStatusCode.OK, incremental.Status);
        Assert.Equal(HttpStatusCode.OK, incrementalList.Status);
        Assert.Single(tasks.Json.EnumerateArray());
    }

    [Fact]
    public async Task PrivateRepositoryFullGeneration_ForTheOwner_IsAllowed()
    {
        var response = await _host.SendAsync("POST", "/api/v1/repositories/repo-p/branches/p-main/generation-tasks/full", Owner);

        Assert.Equal(HttpStatusCode.Created, response.Status);
    }

    // ---- shared branch capability ----

    [Fact]
    public async Task FullGeneration_ByASignedInUserWhoDoesNotOwnTheRepository_IsAllowedAndRecordsTheActor()
    {
        var response = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-main/generation-tasks/full", Stranger);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        await using var context = _database.Open();
        var task = await context.BranchGenerationTasks.SingleAsync();
        Assert.Equal(Stranger, task.RequestedBy);
        var audit = await context.GitConnectionAuditEvents.SingleAsync();
        Assert.Equal(GitConnectionAuditEventType.BranchRebuildRequested, audit.EventType);
        Assert.Equal(Stranger, audit.ActorUserId);
        Assert.Equal("repo-a", audit.RepositoryId);
    }

    [Fact]
    public async Task FullGeneration_ForTwoBranches_QueuesTwoIndependentTasks()
    {
        var first = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-main/generation-tasks/full", Stranger);
        var second = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-dev/generation-tasks/full", Owner);
        var duplicate = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-dev/generation-tasks/full", Stranger);

        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal(HttpStatusCode.Created, second.Status);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
        Assert.Equal("BRANCH_GENERATION_ACTIVE", duplicate.Json.GetProperty("errorCode").GetString());
        await using var context = _database.Open();
        Assert.Equal(2, await context.BranchGenerationTasks.CountAsync());
        Assert.Equal(2, await context.RepositoryGenerationLocks.CountAsync());
    }

    [Fact]
    public async Task FullGeneration_WhenTheBranchBelongsToAnotherRepository_Returns404AndCreatesNothing()
    {
        var response = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/b-main/generation-tasks/full", Stranger);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        await using var context = _database.Open();
        Assert.Empty(await context.BranchGenerationTasks.ToListAsync());
    }

    [Fact]
    public async Task CancelAndRetry_ByAnotherSignedInUser_AreAllowedAndAudited()
    {
        var created = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-main/generation-tasks/full", Owner);
        var taskId = created.Json.GetProperty("taskId").GetString()!;

        var cancel = await _host.SendAsync("POST", $"/api/v1/branch-generation-tasks/{taskId}/cancel", Stranger);
        var retry = await _host.SendAsync("POST", $"/api/v1/branch-generation-tasks/{taskId}/retry", Stranger);

        Assert.Equal(HttpStatusCode.OK, cancel.Status);
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        await using var context = _database.Open();
        var events = await context.GitConnectionAuditEvents.OrderBy(item => item.CreatedAt).ToListAsync();
        Assert.Contains(events, item => item.EventType == GitConnectionAuditEventType.BranchTaskCancelled && item.ActorUserId == Stranger);
        Assert.Contains(events, item => item.EventType == GitConnectionAuditEventType.BranchTaskRetried && item.ActorUserId == Stranger);
    }

    [Fact]
    public async Task TaskReadsAndLists_ForASignedInUser_ReturnOnlyTheRepositoriesTasks()
    {
        var created = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-main/generation-tasks/full", Owner);
        await _host.SendAsync("POST", "/api/v1/repositories/repo-b/branches/b-main/generation-tasks/full", Owner);
        var taskId = created.Json.GetProperty("taskId").GetString()!;

        var single = await _host.SendAsync("GET", $"/api/v1/branch-generation-tasks/{taskId}", Stranger);
        var list = await _host.SendAsync("GET", "/api/v1/repositories/repo-a/branch-generation-tasks", Stranger);
        var missing = await _host.SendAsync("GET", "/api/v1/repositories/missing/branch-generation-tasks", Stranger);

        Assert.Equal(HttpStatusCode.OK, single.Status);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal(1, list.Json.GetArrayLength());
        Assert.Equal("repo-a", list.Json[0].GetProperty("repositoryId").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
    }

    [Fact]
    public async Task IncrementalUpdate_ByASignedInUserWhoDoesNotOwnTheRepository_IsAllowedAndRecordsTheActor()
    {
        var response = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-main/incremental-update", Stranger);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        await using var context = _database.Open();
        var task = await context.IncrementalUpdateTasks.SingleAsync();
        Assert.Equal(Stranger, task.RequestedBy);
        Assert.Equal("a-main", task.BranchId);
        var audit = await context.GitConnectionAuditEvents.SingleAsync();
        Assert.Equal(GitConnectionAuditEventType.BranchSyncRequested, audit.EventType);
        Assert.Equal(Stranger, audit.ActorUserId);
    }

    [Fact]
    public async Task IncrementalUpdate_WhenTheBranchBelongsToAnotherRepository_Returns404AndCreatesNoTask()
    {
        var response = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/b-main/incremental-update", Stranger);

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal("BRANCH_NOT_FOUND", response.Json.GetProperty("errorCode").GetString());
        await using var context = _database.Open();
        Assert.Empty(await context.IncrementalUpdateTasks.ToListAsync());
    }

    [Fact]
    public async Task IncrementalUpdate_WhileTheSameBranchHasAFullGeneration_Returns409WithAStableCode()
    {
        await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-main/generation-tasks/full", Owner);

        var response = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-main/incremental-update", Stranger);

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("BRANCH_GENERATION_ACTIVE", response.Json.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task IncrementalTaskReadsRetriesAndLists_ForASignedInUser_Work()
    {
        var trigger = await _host.SendAsync("POST", "/api/v1/repositories/repo-a/branches/a-main/incremental-update", Owner);
        var taskId = trigger.Json.GetProperty("taskId").GetString()!;
        await using (var context = _database.Open())
        {
            var task = await context.IncrementalUpdateTasks.SingleAsync();
            task.Status = IncrementalUpdateStatus.Failed;
            await context.SaveChangesAsync();
        }

        var read = await _host.SendAsync("GET", $"/api/v1/incremental-updates/{taskId}", Stranger);
        var retry = await _host.SendAsync("POST", $"/api/v1/incremental-updates/{taskId}/retry", Stranger);
        var list = await _host.SendAsync("GET", "/api/v1/repositories/repo-a/incremental-updates", Stranger);

        Assert.Equal(HttpStatusCode.OK, read.Status);
        Assert.Equal(HttpStatusCode.OK, retry.Status);
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Equal(1, list.Json.GetArrayLength());
        await using var verification = _database.Open();
        Assert.Contains(
            await verification.GitConnectionAuditEvents.ToListAsync(),
            item => item.EventType == GitConnectionAuditEventType.BranchTaskRetried && item.ActorUserId == Stranger);
    }

    [Fact]
    public async Task ActionsOnARepositoryWithoutAConnection_StillSucceedWithoutAnAuditRow()
    {
        var response = await _host.SendAsync("POST", "/api/v1/repositories/repo-b/branches/b-main/generation-tasks/full", Stranger);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        await using var context = _database.Open();
        Assert.Empty(await context.GitConnectionAuditEvents.ToListAsync());
        Assert.Equal(Stranger, (await context.BranchGenerationTasks.SingleAsync()).RequestedBy);
    }

    private static Repository Repo(string id, string name, string? connectionId, bool isPublic = true) => new()
    {
        IsPublic = isPublic,
        Id = id,
        OwnerUserId = Owner,
        GitUrl = $"https://github.com/acme/{name}.git",
        OrgName = "acme",
        RepoName = name,
        Status = RepositoryStatus.Completed,
        GitConnectionId = connectionId
    };
}
