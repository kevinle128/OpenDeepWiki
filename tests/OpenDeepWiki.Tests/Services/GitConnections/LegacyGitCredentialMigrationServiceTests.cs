using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models.ConnectedRepositories;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Repositories;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

/// <summary>
/// Backfill of legacy repository credentials into shared connections on a real SQLite database.
/// The provider is a fake: no test sends a request to a real server.
/// </summary>
public sealed class LegacyGitCredentialMigrationServiceTests : IDisposable
{
    private const string Admin = "admin-1";
    private const string PatA = "ghp_CANARY_legacy_pat_A_0123456789";
    private const string PatB = "ghp_CANARY_legacy_pat_B_0123456789";
    private const string PatC = "glpat-CANARY-legacy-pat-C-0123456789";
    private const string UrlSecret = "ghp_CANARY_in_url_0123456789";

    private static readonly DateTime Day1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteScratchDatabase _database;
    private readonly LegacyFakeProvider _github = new(GitProvider.GitHub);
    private readonly LegacyFakeProvider _gitlab = new(GitProvider.GitLab);
    private readonly IGitConnectionSecretProtector _protector =
        new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider());
    private readonly ListLogger<LegacyGitCredentialMigrationService> _logger = new();

    public LegacyGitCredentialMigrationServiceTests()
    {
        _database = SqliteScratchDatabase.CreateAsync().GetAwaiter().GetResult();
        using var context = _database.Open();
        context.Users.Add(new User { Id = "user-3", Name = "user-3", Email = "user-3@example.com" });
        context.Users.Add(new User { Id = Admin, Name = Admin, Email = "admin@example.com" });
        context.Users.Add(new User { Id = "plain", Name = "plain", Email = "plain@example.com" });
        var role = new Role { Id = "role-admin", Name = "Admin", Description = "Admin", IsActive = true };
        context.Roles.Add(role);
        context.UserRoles.Add(new UserRole { Id = "link-admin", UserId = Admin, RoleId = role.Id });
        context.SaveChanges();

        _github.Identities[PatA] = new GitProviderIdentity("1001", "octocat");
        _github.Identities[PatB] = new GitProviderIdentity("1001", "octocat");
        _gitlab.Identities[PatC] = new GitProviderIdentity("2002", "tanuki");
    }

    public void Dispose() => _database.Dispose();

    // ---- classification ----

    [Fact]
    public async Task MigrateAsync_ForAPublicRepositoryWithoutCredentials_ChangesNothing()
    {
        await SeedAsync("pub", "user-1", "https://github.com/acme/open.git", Day1);

        var result = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(0, result.Processed);
        Assert.Empty(_github.Validated);
        await using var context = _database.Open();
        Assert.Null((await context.Repositories.SingleAsync()).GitConnectionId);
        Assert.Empty(await context.GitCredentialMigrationRecords.ToListAsync());
        Assert.Empty(await context.GitConnections.ToListAsync());
    }

    [Fact]
    public async Task MigrateAsync_ForAGitHubPat_CreatesAProtectedConnectionAssignsItAndVerifiesTheNewPath()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/widgets.git", Day1, password: PatA, account: "octocat");

        var result = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(1, result.Migrated);
        await using var context = _database.Open();
        var connection = await context.GitConnections.SingleAsync();
        Assert.Equal(GitProvider.GitHub, connection.Provider);
        Assert.Equal("https://github.com", connection.NormalizedServerUrl);
        Assert.Equal("1001", connection.ExternalAccountId);
        Assert.Equal("user-1", connection.CreatedByUserId);
        Assert.DoesNotContain(PatA, connection.ProtectedToken);
        Assert.Equal(PatA, _protector.Unprotect(connection.ProtectedToken));

        var repository = await context.Repositories.SingleAsync();
        Assert.Equal(connection.Id, repository.GitConnectionId);
        Assert.Equal(GitProvider.GitHub, repository.Provider);
        Assert.Equal("https://github.com", repository.ProviderBaseUrl);
        // The legacy fields stay until the contract release.
        Assert.Equal(PatA, repository.AuthPassword);
        Assert.Equal("octocat", repository.AuthAccount);

        var record = await context.GitCredentialMigrationRecords.SingleAsync();
        Assert.Equal("r1", record.RepositoryId);
        Assert.Equal(GitCredentialMigrationState.Migrated, record.State);
        Assert.Equal(connection.Id, record.GitConnectionId);
        Assert.Null(record.ErrorCode);
        Assert.Equal(1, record.AttemptCount);
    }

    [Fact]
    public async Task MigrateAsync_ForAGitLabComPat_AssignsAGitLabConnection()
    {
        await SeedAsync("r1", "user-1", "https://gitlab.com/group/sub/proj.git", Day1, password: PatC, account: "tanuki");

        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using var context = _database.Open();
        var connection = await context.GitConnections.SingleAsync();
        Assert.Equal(GitProvider.GitLab, connection.Provider);
        Assert.Equal("https://gitlab.com", connection.NormalizedServerUrl);
        Assert.Equal("2002", connection.ExternalAccountId);
        Assert.Equal(connection.Id, (await context.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task MigratedRepository_CanBeClonedThroughTheNewCredentialPathAndTheOriginGuard()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/widgets.git", Day1, password: PatA);
        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using var context = _database.Open();
        var repository = await context.Repositories.AsNoTracking().SingleAsync();
        var credential = await new GitCredentialResolver(context, _protector, NullLogger<GitCredentialResolver>.Instance)
            .ResolveAsync(repository);
        var guard = new GitRemoteOriginGuard(
            GitProviderTestFactory.CreateValidator(new FakeHostResolver().Add("github.com", "140.82.112.3")));

        Assert.Equal(PatA, credential!.Password);
        await guard.EnsureAllowedAsync(repository, repository.GitUrl, CancellationToken.None);
    }

    [Fact]
    public async Task MigrateAsync_ForRepositoriesOfOneExternalAccount_CreatesOneConnectionAndLinksAll()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        await SeedAsync("r2", "user-2", "https://github.com/acme/two.git", Day1.AddDays(1), password: PatB);
        await SeedAsync("r3", "user-3", "https://github.com/acme/three.git", Day1.AddDays(2), password: PatA);

        var result = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(3, result.Migrated);
        await using var context = _database.Open();
        var connection = await context.GitConnections.SingleAsync();
        Assert.All(await context.Repositories.ToListAsync(), item => Assert.Equal(connection.Id, item.GitConnectionId));
        // A PAT is validated once per run, however many repositories hold it.
        Assert.Equal(2, _github.Validated.Count);
    }

    // ---- creator rules ----

    [Fact]
    public async Task MigrateAsync_WhenTheConnectionExists_KeepsItsCreatorAndItsCredential()
    {
        await SeedConnectionAsync("existing", "1001", createdBy: "user-3");
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);

        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using var context = _database.Open();
        var connection = await context.GitConnections.SingleAsync();
        Assert.Equal("existing", connection.Id);
        Assert.Equal("user-3", connection.CreatedByUserId);
        Assert.Equal("existing-token", _protector.Unprotect(connection.ProtectedToken));
        Assert.Equal("existing", (await context.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task MigrateAsync_ForANewConnection_ChoosesTheOwnerOfTheEarliestRepositoryThenTheLowestId()
    {
        // Inserted in reverse order on purpose: the query order must not decide the creator.
        await SeedAsync("r-c", "user-3", "https://github.com/acme/c.git", Day1.AddDays(5), password: PatA);
        await SeedAsync("r-b", "user-2", "https://github.com/acme/b.git", Day1, password: PatB);
        await SeedAsync("r-a", "user-1", "https://github.com/acme/a.git", Day1, password: PatA);

        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using var context = _database.Open();
        // r-a and r-b share the earliest time, so the lower repository ID r-a wins.
        Assert.Equal("user-1", (await context.GitConnections.SingleAsync()).CreatedByUserId);
    }

    [Fact]
    public async Task MigrateAsync_WhenTheEarliestRepositoryFailedTemporarily_DoesNotLetALaterOwnerCreateTheConnection()
    {
        await SeedAsync("r-a", "user-1", "https://github.com/acme/a.git", Day1, password: PatA);
        await SeedAsync("r-b", "user-2", "https://github.com/acme/b.git", Day1.AddDays(1), password: PatB);
        _github.Failures[PatA] = new GitProviderException(GitProviderErrorCodes.RateLimited);

        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using (var context = _database.Open())
        {
            Assert.Empty(await context.GitConnections.ToListAsync());
            var records = await context.GitCredentialMigrationRecords.ToListAsync();
            Assert.All(records, record => Assert.Equal(GitCredentialMigrationState.Failed, record.State));
        }

        _github.Failures.Clear();
        var retried = await Service().RetryAsync(["r-a", "r-b"], CancellationToken.None);

        Assert.Equal(2, retried.Migrated);
        await using var after = _database.Open();
        Assert.Equal("user-1", (await after.GitConnections.SingleAsync()).CreatedByUserId);
    }

    // ---- idempotency and resume ----

    [Fact]
    public async Task MigrateAsync_RunTwice_ChangesNoRowAuditEventOrRecord()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        await SeedAsync("r2", "user-2", "https://github.com/acme/two.git", Day1.AddDays(1), password: PatB);
        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);
        var before = await SnapshotAsync();

        var second = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(0, second.Processed);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task MigrateAsync_InBatchesOfOne_CoversEveryRepositoryAndReportsWhenMoreRemain()
    {
        for (var index = 1; index <= 3; index++)
        {
            await SeedAsync($"r{index}", "user-1", $"https://github.com/acme/r{index}.git", Day1.AddDays(index), password: PatA);
        }

        var first = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(1), CancellationToken.None);
        var second = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(1), CancellationToken.None);
        var third = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(1), CancellationToken.None);

        Assert.True(first.HasMore);
        Assert.True(second.HasMore);
        Assert.False(third.HasMore);
        await using var context = _database.Open();
        Assert.Equal(3, await context.GitCredentialMigrationRecords.CountAsync(item => item.State == GitCredentialMigrationState.Migrated));
        Assert.Single(await context.GitConnections.ToListAsync());
        Assert.Equal(1, await context.GitConnectionAuditEvents.CountAsync(item => item.EventType == GitConnectionAuditEventType.Created));
    }

    [Fact]
    public async Task MigrateAsync_WhenInterruptedBetweenTheConnectionAndTheAssignment_ResumesWithoutDuplicates()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        await SeedAsync("r2", "user-2", "https://github.com/acme/two.git", Day1.AddDays(1), password: PatA);
        using var cts = new CancellationTokenSource();
        var interrupting = new InterruptingLinker(cts, interruptOnCall: 2);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(linkerWrapper: inner => interrupting.Wrap(inner))
                .MigrateAsync(new LegacyCredentialMigrationRequest(), cts.Token));

        await using (var interrupted = _database.Open())
        {
            Assert.Single(await interrupted.GitConnections.ToListAsync());
            Assert.Equal(1, await interrupted.GitCredentialMigrationRecords.CountAsync());
            Assert.NotNull((await interrupted.Repositories.SingleAsync(item => item.Id == "r1")).GitConnectionId);
            Assert.Null((await interrupted.Repositories.SingleAsync(item => item.Id == "r2")).GitConnectionId);
        }

        var resumed = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(1, resumed.Migrated);
        await using var context = _database.Open();
        Assert.Single(await context.GitConnections.ToListAsync());
        Assert.Equal(2, await context.GitCredentialMigrationRecords.CountAsync(item => item.State == GitCredentialMigrationState.Migrated));
        Assert.Equal(1, await context.GitConnectionAuditEvents.CountAsync(item => item.EventType == GitConnectionAuditEventType.Created));
        Assert.Equal(2, await context.GitConnectionAuditEvents.CountAsync(item => item.EventType == GitConnectionAuditEventType.RepositoryAssigned));
    }

    [Fact]
    public async Task MigrateAsync_WhenAnAssignmentWasCommittedButNotRecorded_RecordsItWithoutANewAuditEvent()
    {
        await SeedConnectionAsync("c1", "1001");
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA, connectionId: "c1",
            provider: GitProvider.GitHub, baseUrl: "https://github.com");

        var result = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(1, result.Migrated);
        Assert.Empty(_github.Validated);
        await using var context = _database.Open();
        Assert.Equal(GitCredentialMigrationState.Migrated, (await context.GitCredentialMigrationRecords.SingleAsync()).State);
        Assert.Empty(await context.GitConnectionAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task MigrateAsync_WhenTheCallerCancels_KeepsTheProgressOfFinishedItems()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        await SeedAsync("r2", "user-2", "https://github.com/acme/two.git", Day1.AddDays(1), password: PatB);
        using var cts = new CancellationTokenSource();
        _github.OnValidate = token =>
        {
            if (token == PatB)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service().MigrateAsync(new LegacyCredentialMigrationRequest(), cts.Token));

        await using var context = _database.Open();
        var records = await context.GitCredentialMigrationRecords.ToListAsync();
        Assert.Equal("r1", Assert.Single(records).RepositoryId);
    }

    // ---- provider failures ----

    [Theory]
    [InlineData(GitProviderErrorCodes.Unauthorized, GitCredentialMigrationState.Blocked)]
    [InlineData(GitProviderErrorCodes.Forbidden, GitCredentialMigrationState.Blocked)]
    [InlineData(GitProviderErrorCodes.RateLimited, GitCredentialMigrationState.Failed)]
    [InlineData(GitProviderErrorCodes.Timeout, GitCredentialMigrationState.Failed)]
    [InlineData(GitProviderErrorCodes.Unavailable, GitCredentialMigrationState.Failed)]
    public async Task MigrateAsync_WhenTheProviderFails_StoresOnlyASafeCodeAndKeepsTheLegacyData(string code, GitCredentialMigrationState expectedState)
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        _github.Failures[PatA] = new GitProviderException(code, TimeSpan.FromSeconds(30));

        var result = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(1, expectedState == GitCredentialMigrationState.Blocked ? result.Blocked : result.Failed);
        await using var context = _database.Open();
        var record = await context.GitCredentialMigrationRecords.SingleAsync();
        Assert.Equal(expectedState, record.State);
        Assert.Equal(code, record.ErrorCode);
        Assert.Null(record.GitConnectionId);
        Assert.Empty(await context.GitConnections.ToListAsync());
        var repository = await context.Repositories.SingleAsync();
        Assert.Null(repository.GitConnectionId);
        Assert.Equal(PatA, repository.AuthPassword);
    }

    [Fact]
    public async Task RetryAsync_AfterTheCauseIsFixed_MigratesTheRepositoryAndCountsTheAttempts()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        _github.Failures[PatA] = new GitProviderException(GitProviderErrorCodes.RateLimited);
        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        var skipped = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);
        _github.Failures.Clear();
        var retried = await Service().RetryAsync(["r1"], CancellationToken.None);

        Assert.Equal(0, skipped.Processed);
        Assert.Equal(1, retried.Migrated);
        await using var context = _database.Open();
        var record = await context.GitCredentialMigrationRecords.SingleAsync();
        Assert.Equal(GitCredentialMigrationState.Migrated, record.State);
        Assert.Null(record.ErrorCode);
        Assert.Equal(2, record.AttemptCount);
    }

    [Fact]
    public async Task RetryAsync_ForAMigratedOrUnknownRepository_ChangesNothing()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);
        var before = await SnapshotAsync();

        var result = await Service().RetryAsync(["r1", "missing"], CancellationToken.None);

        Assert.Equal(0, result.Processed);
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task MigrateAsync_WhenTheIdentityMatchesASoftDeletedConnection_BlocksWithoutRestoringIt()
    {
        await SeedConnectionAsync("gone", "1001", deleted: true);
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);

        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using var context = _database.Open();
        var record = await context.GitCredentialMigrationRecords.SingleAsync();
        Assert.Equal(GitCredentialMigrationState.Blocked, record.State);
        Assert.Equal(LegacyCredentialErrorCodes.ConnectionDeleted, record.ErrorCode);
        var connection = await context.GitConnections.SingleAsync();
        Assert.True(connection.IsDeleted);
        Assert.Null((await context.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task MigrateAsync_WhenTheMatchingConnectionIsDisabled_BlocksAndDoesNotLinkTheRepository()
    {
        await SeedConnectionAsync("off", "1001", enabled: false);
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);

        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using var context = _database.Open();
        var record = await context.GitCredentialMigrationRecords.SingleAsync();
        Assert.Equal(GitCredentialMigrationState.Blocked, record.State);
        Assert.Equal(LegacyCredentialErrorCodes.ConnectionDisabled, record.ErrorCode);
        Assert.Null((await context.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task MigrateAsync_WhenTheMatchingConnectionHasACorruptSecret_BlocksAndDoesNotLinkTheRepository()
    {
        await SeedConnectionAsync("bad", "1001", protectedToken: "not-a-protected-payload");
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);

        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using var context = _database.Open();
        var record = await context.GitCredentialMigrationRecords.SingleAsync();
        Assert.Equal(GitCredentialMigrationState.Blocked, record.State);
        Assert.Equal(LegacyCredentialErrorCodes.ConnectionSecretUnreadable, record.ErrorCode);
        Assert.Null((await context.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task MigrateAsync_WhenAnAdoptedRepositoryPointsAtADisabledConnection_BlocksItForRepair()
    {
        await SeedConnectionAsync("off", "1001", enabled: false);
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA, connectionId: "off",
            provider: GitProvider.GitHub, baseUrl: "https://github.com");

        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using var context = _database.Open();
        var record = await context.GitCredentialMigrationRecords.SingleAsync();
        Assert.Equal(GitCredentialMigrationState.Blocked, record.State);
        Assert.Equal(LegacyCredentialErrorCodes.ConnectionDisabled, record.ErrorCode);
    }

    // ---- unsupported legacy islands and repair queue ----

    [Theory]
    [InlineData("https://gitee.com/acme/widgets.git")]
    [InlineData("https://git.corp.example/acme/widgets.git")]
    [InlineData("http://github.com/acme/widgets.git")]
    [InlineData("git@github.com:acme/widgets.git")]
    public async Task MigrateAsync_ForAnUnsupportedHostWithCredentials_BlocksWithoutAnyProviderCall(string url)
    {
        await SeedAsync("r1", "user-1", url, Day1, password: PatA);

        var result = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(1, result.Blocked);
        Assert.Empty(_github.Validated);
        Assert.Empty(_gitlab.Validated);
        await using var context = _database.Open();
        var record = await context.GitCredentialMigrationRecords.SingleAsync();
        Assert.Equal(GitCredentialMigrationState.Blocked, record.State);
        Assert.Equal(LegacyCredentialErrorCodes.UnsupportedHost, record.ErrorCode);
        var repository = await context.Repositories.SingleAsync();
        Assert.Null(repository.GitConnectionId);
        Assert.Equal(PatA, repository.AuthPassword);
    }

    [Fact]
    public async Task MigrateAsync_ForACredentialInTheUrl_BlocksAndNeverReportsTheUrl()
    {
        await SeedAsync("r1", "user-1", $"https://octocat:{UrlSecret}@github.com/acme/widgets.git", Day1);

        var result = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(1, result.Blocked);
        Assert.Empty(_github.Validated);
        await using var context = _database.Open();
        var record = await context.GitCredentialMigrationRecords.SingleAsync();
        Assert.Equal(LegacyCredentialErrorCodes.UrlContainsUserInfo, record.ErrorCode);
        var status = await Service().GetStatusAsync(CancellationToken.None);
        Assert.DoesNotContain(UrlSecret, JsonSerializer.Serialize(status));
        Assert.Contains(status.RepairQueue, item => item.RepositoryId == "r1" && item.ErrorCode == LegacyCredentialErrorCodes.UrlContainsUserInfo);
    }

    [Fact]
    public async Task MigrateAsync_ForAPasswordWithoutAnAccountOrAnAccountWithoutAPassword_HandlesEachSafely()
    {
        await SeedAsync("only-account", "user-1", "https://github.com/acme/a.git", Day1, account: "octocat");
        await SeedAsync("bad-token", "user-1", "https://github.com/acme/b.git", Day1.AddDays(1), password: "has space token");

        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        await using var context = _database.Open();
        var records = await context.GitCredentialMigrationRecords.OrderBy(item => item.RepositoryId).ToListAsync();
        Assert.Equal(LegacyCredentialErrorCodes.CredentialInvalid, records[0].ErrorCode);
        Assert.Equal(LegacyCredentialErrorCodes.CredentialIncomplete, records[1].ErrorCode);
        Assert.Empty(_github.Validated);
    }

    [Fact]
    public async Task MigrateAsync_IgnoresSoftDeletedRepositoriesAndNonGitSources()
    {
        await SeedAsync("gone", "user-1", "https://github.com/acme/gone.git", Day1, password: PatA, deleted: true);
        await SeedAsync("local", "user-1", RepositorySource.EncodeLocalDirectoryPath("/srv/code"), Day1, password: PatA);

        var result = await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        Assert.Equal(0, result.Processed);
        Assert.Empty(_github.Validated);
    }

    // ---- dry run and status ----

    [Fact]
    public async Task DryRunAsync_ReportsCountsProvidersAndCodesWithoutWritingOrCallingTheProvider()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        await SeedAsync("r2", "user-1", "https://gitlab.com/acme/two.git", Day1, password: PatC);
        await SeedAsync("r3", "user-1", "https://gitee.com/acme/three.git", Day1, password: PatA);
        await SeedAsync("r4", "user-1", $"https://u:{UrlSecret}@github.com/acme/four.git", Day1);
        await SeedAsync("r5", "user-1", "https://github.com/acme/open.git", Day1);
        var before = await SnapshotAsync();

        var report = await Service().DryRunAsync(CancellationToken.None);

        Assert.Equal(before, await SnapshotAsync());
        Assert.Empty(_github.Validated);
        Assert.Equal(1, report.NotRequired);
        Assert.Contains(report.Groups, group => group is { Provider: "GitHub", State: "Pending", ErrorCode: null, Count: 1 });
        Assert.Contains(report.Groups, group => group is { Provider: "GitLab", State: "Pending", Count: 1 });
        Assert.Contains(report.Groups, group => group is { Provider: "Gitee", State: "Blocked", ErrorCode: LegacyCredentialErrorCodes.UnsupportedHost, Count: 1 });
        Assert.Contains(report.Groups, group => group is { State: "Blocked", ErrorCode: LegacyCredentialErrorCodes.UrlContainsUserInfo, Count: 1 });
        Assert.Empty(report.RepairQueue);
        Assert.DoesNotContain(UrlSecret, JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task GetStatusAsync_ListsTheRepairQueueAndKeepsTheContractGateClosedWhileCredentialsRemain()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        await SeedAsync("r2", "user-1", "https://gitee.com/acme/two.git", Day1.AddDays(1), password: PatA);
        _github.Failures[PatA] = new GitProviderException(GitProviderErrorCodes.Unauthorized);
        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        var status = await Service().GetStatusAsync(CancellationToken.None);

        Assert.False(status.ContractGateClear);
        Assert.Equal(["r1", "r2"], status.RepairQueue.Select(item => item.RepositoryId).Order());
        Assert.Contains(status.ContractBlockers, item => item.Code == LegacyCredentialErrorCodes.UnsupportedHost && item.Count == 1);
        Assert.Contains(status.ContractBlockers, item => item.Code == GitProviderErrorCodes.Unauthorized && item.Count == 1);
    }

    [Fact]
    public async Task GetStatusAsync_AfterEveryRepositoryIsMigrated_OpensTheMigrationPartOfTheGate()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);
        await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);

        var status = await Service().GetStatusAsync(CancellationToken.None);

        Assert.True(status.ContractGateClear);
        Assert.Empty(status.ContractBlockers);
        Assert.Empty(status.RepairQueue);
        Assert.Contains(status.Groups, group => group is { Provider: "GitHub", State: "Migrated", Count: 1 });
    }

    // ---- authorization and input ----

    [Fact]
    public async Task EveryOperation_RequiresACurrentDatabaseAdmin()
    {
        await SeedAsync("r1", "user-1", "https://github.com/acme/one.git", Day1, password: PatA);

        foreach (var caller in new string?[] { null, "plain", "user-1", "ghost" })
        {
            var service = Service(caller);
            await AssertAdminRequiredAsync(() => service.DryRunAsync(CancellationToken.None));
            await AssertAdminRequiredAsync(() => service.GetStatusAsync(CancellationToken.None));
            await AssertAdminRequiredAsync(() => service.MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None));
            await AssertAdminRequiredAsync(() => service.RetryAsync(["r1"], CancellationToken.None));
        }

        Assert.Empty(_github.Validated);
        await using var context = _database.Open();
        Assert.Empty(await context.GitConnections.ToListAsync());
    }

    [Fact]
    public async Task MigrateAsync_WhenTheAdminRoleWasRevoked_RefusesEvenWithAnAdminToken()
    {
        await using (var context = _database.Open())
        {
            var link = await context.UserRoles.SingleAsync();
            link.MarkAsDeleted();
            await context.SaveChangesAsync();
        }

        await AssertAdminRequiredAsync(
            () => Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task MigrateAsync_WithAnInvalidBatchSize_Fails(int batchSize)
    {
        var exception = await Assert.ThrowsAsync<LegacyCredentialMigrationException>(
            () => Service().MigrateAsync(new LegacyCredentialMigrationRequest(batchSize), CancellationToken.None));

        Assert.Equal(LegacyCredentialErrorCodes.InvalidRequest, exception.ErrorCode);
    }

    [Fact]
    public async Task RetryAsync_WithTooManyOrNoRepositoryIds_Fails()
    {
        var empty = await Assert.ThrowsAsync<LegacyCredentialMigrationException>(
            () => Service().RetryAsync([], CancellationToken.None));
        var tooMany = await Assert.ThrowsAsync<LegacyCredentialMigrationException>(
            () => Service().RetryAsync(Enumerable.Range(0, 201).Select(index => $"r{index}").ToList(), CancellationToken.None));

        Assert.Equal(LegacyCredentialErrorCodes.InvalidRequest, empty.ErrorCode);
        Assert.Equal(LegacyCredentialErrorCodes.InvalidRequest, tooMany.ErrorCode);
    }

    // ---- secrets ----

    [Fact]
    public async Task CanaryPat_AcrossEveryFailureMode_NeverLeavesTheLegacySourceField()
    {
        await SeedAsync("ok", "user-1", "https://github.com/acme/ok.git", Day1, password: PatA);
        await SeedAsync("denied", "user-1", "https://github.com/acme/denied.git", Day1.AddDays(1), password: PatB);
        await SeedAsync("limited", "user-1", "https://gitlab.com/acme/limited.git", Day1.AddDays(2), password: PatC);
        await SeedAsync("island", "user-1", "https://gitee.com/acme/island.git", Day1.AddDays(3), password: PatA);
        await SeedAsync("url", "user-1", $"https://u:{UrlSecret}@github.com/acme/url.git", Day1.AddDays(4));
        _github.Failures[PatB] = new GitProviderException(GitProviderErrorCodes.Unauthorized);
        _gitlab.Failures[PatC] = new GitProviderException(GitProviderErrorCodes.RateLimited, TimeSpan.FromSeconds(5));
        var exceptions = new List<string>();

        var results = new List<object>();
        results.Add(await Service().DryRunAsync(CancellationToken.None));
        results.Add(await Service().MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None));
        results.Add(await Service().RetryAsync(["denied", "limited"], CancellationToken.None));
        results.Add(await Service().GetStatusAsync(CancellationToken.None));
        try
        {
            await Service("plain").MigrateAsync(new LegacyCredentialMigrationRequest(), CancellationToken.None);
        }
        catch (LegacyCredentialMigrationException ex)
        {
            exceptions.Add(ex.ToString());
        }

        var outputs = string.Join("|", results.Select(item => JsonSerializer.Serialize(item, item.GetType())))
                      + string.Join("|", exceptions) + string.Join("|", _logger.Lines);
        foreach (var canary in new[] { PatA, PatB, PatC, UrlSecret })
        {
            Assert.DoesNotContain(canary, outputs);
        }

        foreach (var canary in new[] { PatA, PatB, PatC })
        {
            var locations = FindTextLocations(_database.Path, canary);
            Assert.All(locations, location => Assert.Equal("Repositories.AuthPassword", location));
        }

        Assert.All(FindTextLocations(_database.Path, UrlSecret), location => Assert.Equal("Repositories.GitUrl", location));
    }

    // ---- helpers ----

    private static async Task AssertAdminRequiredAsync(Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<LegacyCredentialMigrationException>(action);
        Assert.Equal(LegacyCredentialErrorCodes.AdminRequired, exception.ErrorCode);
    }

    private LegacyGitCredentialMigrationService Service(
        string? userId = Admin,
        Func<IConnectedRepositoryService, IConnectedRepositoryService>? linkerWrapper = null)
    {
        var context = _database.Open();
        var user = new StubUserContext(userId);
        var authorization = new GitConnectionAuthorizationService(user, context);
        IConnectedRepositoryService linker = new ConnectedRepositoryService(
            context,
            user,
            authorization,
            _protector,
            new GitProviderClientResolver([_github, _gitlab]),
            new RepositoryGenerationLockService(context),
            new BranchActionAuditor(context, NullLogger<BranchActionAuditor>.Instance),
            NullLogger<ConnectedRepositoryService>.Instance);
        if (linkerWrapper is not null)
        {
            linker = linkerWrapper(linker);
        }

        return new LegacyGitCredentialMigrationService(
            context,
            user,
            authorization,
            _protector,
            new GitProviderClientResolver([_github, _gitlab]),
            new GitCredentialResolver(context, _protector, NullLogger<GitCredentialResolver>.Instance),
            linker,
            _logger);
    }

    private async Task SeedAsync(
        string id, string owner, string gitUrl, DateTime createdAt,
        string? password = null, string? account = null, bool deleted = false,
        string? connectionId = null, GitProvider? provider = null, string? baseUrl = null)
    {
        await using var context = _database.Open();
        var repository = new Repository
        {
            Id = id,
            OwnerUserId = owner,
            GitUrl = gitUrl,
            OrgName = "org-" + id,
            RepoName = "repo-" + id,
            AuthAccount = account,
            AuthPassword = password,
            IsPublic = password is null && account is null,
            Status = RepositoryStatus.Completed,
            CreatedAt = createdAt,
            GitConnectionId = connectionId,
            Provider = provider,
            ProviderBaseUrl = baseUrl
        };
        if (deleted)
        {
            repository.MarkAsDeleted();
        }

        context.Repositories.Add(repository);
        await context.SaveChangesAsync();
    }

    private async Task SeedConnectionAsync(
        string id, string accountId, string createdBy = "user-2", bool enabled = true, bool deleted = false,
        string? protectedToken = null)
    {
        await using var context = _database.Open();
        var connection = new GitConnection
        {
            Id = id,
            Provider = GitProvider.GitHub,
            NormalizedServerUrl = "https://github.com",
            ExternalAccountId = accountId,
            DisplayName = id,
            AccountName = "octocat",
            ProtectedToken = protectedToken ?? _protector.Protect("existing-token"),
            CreatedByUserId = createdBy,
            IsEnabled = enabled
        };
        if (deleted)
        {
            connection.MarkAsDeleted();
            connection.ProtectedToken = string.Empty;
        }

        context.GitConnections.Add(connection);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Everything a rerun must not change: connections, assignments, audit events, and progress records.
    /// </summary>
    private async Task<string> SnapshotAsync()
    {
        await using var context = _database.Open();
        var connections = await context.GitConnections.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => $"{item.Id}|{item.ExternalAccountId}|{item.CreatedByUserId}|{item.ConcurrencyStamp}|{item.IsEnabled}")
            .ToListAsync();
        var repositories = await context.Repositories.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => $"{item.Id}|{item.GitConnectionId}|{item.Provider}|{item.ProviderBaseUrl}")
            .ToListAsync();
        var events = await context.GitConnectionAuditEvents.AsNoTracking().OrderBy(item => item.Id)
            .Select(item => $"{item.Id}|{item.EventType}|{item.RepositoryId}")
            .ToListAsync();
        var records = await context.GitCredentialMigrationRecords.AsNoTracking().OrderBy(item => item.RepositoryId)
            .Select(item => $"{item.RepositoryId}|{item.State}|{item.ErrorCode}|{item.AttemptCount}|{item.GitConnectionId}")
            .ToListAsync();
        return string.Join("\n", connections.Concat(repositories).Concat(events).Concat(records));
    }

    /// <summary>
    /// Finds every text column of every table that holds the value, as Table.Column.
    /// </summary>
    internal static List<string> FindTextLocations(string path, string value)
    {
        var found = new List<string>();
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            var columns = new List<string>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    columns.Add(reader.GetString(0));
                }
            }

            foreach (var column in columns)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE CAST(\"{column}\" AS TEXT) LIKE @value";
                command.Parameters.AddWithValue("@value", $"%{value}%");
                if (Convert.ToInt64(command.ExecuteScalar()) > 0)
                {
                    found.Add($"{table}.{column}");
                }
            }
        }

        return found;
    }

    private sealed class StubUserContext(string? userId) : IUserContext
    {
        public string? UserId => userId;
        public string? UserName => userId;
        public string? Email => null;
        public bool IsAuthenticated => userId is not null;
        public ClaimsPrincipal? User => userId is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, "Admin")], "Test"));
    }

    /// <summary>
    /// Cancels the run on the chosen assignment call, after the connection exists and before the repository is linked.
    /// </summary>
    private sealed class InterruptingLinker(CancellationTokenSource cts, int interruptOnCall)
    {
        private int _calls;

        public IConnectedRepositoryService Wrap(IConnectedRepositoryService inner) => new Wrapper(this, inner);

        private sealed class Wrapper(InterruptingLinker owner, IConnectedRepositoryService inner) : IConnectedRepositoryService
        {
            public Task<ConnectedRepositoryResponse> ConnectAsync(ConnectRepositoryRequest request, CancellationToken cancellationToken)
                => inner.ConnectAsync(request, cancellationToken);

            public Task<ConnectedRepositoryResponse> AddIndexedBranchesAsync(
                string repositoryId, AddIndexedBranchesRequest request, CancellationToken cancellationToken)
                => inner.AddIndexedBranchesAsync(repositoryId, request, cancellationToken);

            public Task<IReadOnlyList<IndexedBranchSummary>> ListIndexedBranchesAsync(string repositoryId, CancellationToken cancellationToken)
                => inner.ListIndexedBranchesAsync(repositoryId, cancellationToken);

            public Task AdoptLegacyRepositoryAsync(string repositoryId, string connectionId, CancellationToken cancellationToken)
            {
                if (++owner._calls == owner.Interrupt)
                {
                    owner.Cancel();
                    throw new OperationCanceledException();
                }

                return inner.AdoptLegacyRepositoryAsync(repositoryId, connectionId, cancellationToken);
            }
        }

        private int Interrupt => interruptOnCall;

        private void Cancel() => cts.Cancel();
    }
}

/// <summary>
/// Provider that knows the account of each token. Every validated token is recorded.
/// </summary>
internal sealed class LegacyFakeProvider(GitProvider provider) : IGitProviderClient
{
    public GitProvider Provider { get; } = provider;

    public Dictionary<string, GitProviderIdentity> Identities { get; } = new();

    public Dictionary<string, Exception> Failures { get; } = new();

    public List<string> Validated { get; } = [];

    public Action<string>? OnValidate { get; set; }

    public Task<GitProviderIdentity> ValidateAsync(GitProviderTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validated.Add(target.Token);
        OnValidate?.Invoke(target.Token);
        cancellationToken.ThrowIfCancellationRequested();
        if (Failures.TryGetValue(target.Token, out var failure))
        {
            throw failure;
        }

        return Identities.TryGetValue(target.Token, out var identity)
            ? Task.FromResult(identity)
            : throw new GitProviderException(GitProviderErrorCodes.Unauthorized);
    }

    public Task<RemoteRepository> GetRepositoryAsync(GitProviderTarget target, string providerRepositoryId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
        GitProviderTarget target, string? cursor, int pageSize, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
        GitProviderTarget target, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
