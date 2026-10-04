using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models.GitConnections;
using OpenDeepWiki.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

public sealed class GitConnectionServiceTests : IDisposable
{
    private const string CanaryPat = "ghp_CANARY_service_0123456789abcdef";
    private const string SecondPat = "ghp_SECOND_service_0123456789abcdef";
    private const string CreatorId = "creator";
    private const string OtherId = "other";
    private const string AdminId = "admin";
    private const string PublicIp = "93.184.216.34";

    private readonly string _dbPath = Path.Combine(TestPaths.ScratchRoot, $"git-connection-service-{Guid.NewGuid():N}.db");
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
    private readonly FakeProviderClient _github = new(GitProvider.GitHub);
    private readonly FakeProviderClient _gitlab = new(GitProvider.GitLab);
    private readonly List<ServiceHandle> _handles = [];

    public GitConnectionServiceTests()
    {
        _github.IdentityByToken[CanaryPat] = new GitProviderIdentity("1001", "octocat");
        _github.IdentityByToken[SecondPat] = new GitProviderIdentity("1001", "octocat");
        _gitlab.IdentityByToken[CanaryPat] = new GitProviderIdentity("2002", "tanuki");
        _gitlab.IdentityByToken[SecondPat] = new GitProviderIdentity("2003", "other-tanuki");
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            handle.Context.Dispose();
        }

        SqlitePools.Release(_dbPath);
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // ---- create ----

    [Fact]
    public async Task Create_ValidatesTokenBeforeStoring_AndKeepsOnlyProtectedCiphertext()
    {
        var service = await NewServiceAsync(CreatorId);

        var result = await service.CreateAsync(GitHubRequest(), CancellationToken.None);

        Assert.Equal(GitConnectionCreateOutcomes.Created, result.Outcome);
        Assert.Equal(CanaryPat, Assert.Single(_github.ValidateCalls).Token);
        Assert.Null(_github.ValidateCalls[0].ConnectionId);
        Assert.Equal("https://github.com", _github.ValidateCalls[0].ServerUrl);

        await using var verification = OpenContext();
        var row = await verification.GitConnections.SingleAsync();
        Assert.Equal("1001", row.ExternalAccountId);
        Assert.Equal("octocat", row.AccountName);
        Assert.Equal("https://github.com", row.NormalizedServerUrl);
        Assert.Equal(CreatorId, row.CreatedByUserId);
        Assert.True(row.IsEnabled);
        Assert.NotNull(row.LastValidatedAt);
        Assert.NotEqual(CanaryPat, row.ProtectedToken);
        Assert.DoesNotContain(CanaryPat, row.ProtectedToken);
        Assert.Equal(36, row.Id.Length);
        Assert.True(Guid.TryParse(row.Id, out _));
        Assert.Equal(CanaryPat, Protector().Unprotect(row.ProtectedToken));
    }

    [Fact]
    public async Task Create_ReturnsSafeResponseThatNeverContainsTheToken()
    {
        var service = await NewServiceAsync(CreatorId);

        var result = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        // Close every connection before the file is read, as the model test does.
        foreach (var handle in _handles)
        {
            await handle.Context.DisposeAsync();
        }

        SqlitePools.Release(_dbPath);

        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(CanaryPat, json);
        Assert.DoesNotContain("protectedToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"token\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Connection.HasSecret);
        Assert.True(result.Connection.CanMaintain);
        Assert.Equal("GitHub", result.Connection.Provider);
        Assert.Equal("octocat", result.Connection.AccountLogin);
        Assert.Equal("octocat", result.Connection.DisplayName);
        Assert.Equal("Healthy", result.Connection.State);
        Assert.False(File.ReadAllBytes(_dbPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(CanaryPat)) >= 0);
    }

    [Fact]
    public async Task Create_WhenProviderRejectsTheToken_StoresNothing()
    {
        var service = await NewServiceAsync(CreatorId);
        var request = GitHubRequest();
        request.Token = "ghp_unknown_token";

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => service.CreateAsync(request, CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.Unauthorized, exception.Code);
        await using var verification = OpenContext();
        Assert.Empty(await verification.GitConnections.ToListAsync());
        Assert.Empty(await verification.GitConnectionAuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Create_ForGitLabWithoutServerUrl_UsesGitLabCom()
    {
        var service = await NewServiceAsync(CreatorId);

        var result = await service.CreateAsync(new CreateGitConnectionRequest { Provider = "gitlab", Token = CanaryPat }, CancellationToken.None);

        Assert.Equal("https://gitlab.com", result.Connection.ServerUrl);
        Assert.Equal("https://gitlab.com", Assert.Single(_gitlab.ValidateCalls).ServerUrl);
        Assert.Equal("GitLab", result.Connection.Provider);
    }

    [Fact]
    public async Task Create_ForSelfHostedGitLab_StoresNormalizedOriginAndDescriptiveDefaultName()
    {
        var service = await NewServiceAsync(CreatorId);

        var result = await service.CreateAsync(
            new CreateGitConnectionRequest { Provider = "GitLab", ServerUrl = "HTTPS://GitLab.Corp.Example:8443/", Token = CanaryPat },
            CancellationToken.None);

        Assert.Equal("https://gitlab.corp.example:8443", result.Connection.ServerUrl);
        Assert.Equal("tanuki (gitlab.corp.example:8443)", result.Connection.DisplayName);
    }

    [Theory]
    [InlineData("http://gitlab.corp.example")]
    [InlineData("https://user:pw@gitlab.corp.example")]
    [InlineData("https://gitlab.corp.example/sub")]
    [InlineData("gitlab.corp.example")]
    public async Task Create_WhenServerUrlIsNotAnHttpsOrigin_RejectsBeforeAnyProviderCall(string serverUrl)
    {
        var service = await NewServiceAsync(CreatorId);

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => service.CreateAsync(
            new CreateGitConnectionRequest { Provider = "GitLab", ServerUrl = serverUrl, Token = CanaryPat }, CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.ServerUrlInvalid, exception.Code);
        Assert.Empty(_gitlab.ValidateCalls);
    }

    [Fact]
    public async Task Create_ForGitHubWithAnotherServerUrl_Rejects()
    {
        var service = await NewServiceAsync(CreatorId);
        var request = GitHubRequest();
        request.ServerUrl = "https://github.evil.example";

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => service.CreateAsync(request, CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.ServerUrlInvalid, exception.Code);
        Assert.Empty(_github.ValidateCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Gitee")]
    [InlineData("1")]
    [InlineData("99")]
    public async Task Create_WhenProviderIsNotSupported_Rejects(string? provider)
    {
        var service = await NewServiceAsync(CreatorId);

        var error = await Assert.ThrowsAsync<GitConnectionServiceException>(() => service.CreateAsync(
            new CreateGitConnectionRequest { Provider = provider, Token = CanaryPat }, CancellationToken.None));

        Assert.Equal(GitConnectionErrorCodes.InvalidProvider, error.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    public async Task Create_WhenTokenIsMissingOrMalformed_RejectsWithoutProviderCallOrEcho(string? token)
    {
        var service = await NewServiceAsync(CreatorId);

        var error = await Assert.ThrowsAsync<GitConnectionServiceException>(() => service.CreateAsync(
            new CreateGitConnectionRequest { Provider = "GitHub", Token = token }, CancellationToken.None));

        Assert.Equal(GitConnectionErrorCodes.InvalidToken, error.ErrorCode);
        Assert.Empty(_github.ValidateCalls);
    }

    [Fact]
    public async Task Create_WhenTokenIsHuge_Rejects()
    {
        var service = await NewServiceAsync(CreatorId);

        var error = await Assert.ThrowsAsync<GitConnectionServiceException>(() => service.CreateAsync(
            new CreateGitConnectionRequest { Provider = "GitHub", Token = new string('a', 5000) }, CancellationToken.None));

        Assert.Equal(GitConnectionErrorCodes.InvalidToken, error.ErrorCode);
    }

    [Fact]
    public async Task Create_WhenDisplayNameIsTooLong_Rejects()
    {
        var service = await NewServiceAsync(CreatorId);
        var request = GitHubRequest();
        request.DisplayName = new string('n', 201);

        var error = await Assert.ThrowsAsync<GitConnectionServiceException>(() => service.CreateAsync(request, CancellationToken.None));

        Assert.Equal(GitConnectionErrorCodes.InvalidDisplayName, error.ErrorCode);
    }

    [Fact]
    public async Task Create_WhenCallerIsAnonymous_IsUnauthorized()
    {
        var service = await NewServiceAsync(null);

        var error = await Assert.ThrowsAsync<GitConnectionServiceException>(() => service.CreateAsync(GitHubRequest(), CancellationToken.None));

        Assert.Equal(GitConnectionErrorCodes.Unauthorized, error.ErrorCode);
    }

    [Fact]
    public async Task Create_WhenAccountAlreadyHasAConnection_ReturnsItAndKeepsItsCredential()
    {
        var first = await NewServiceAsync(CreatorId);
        var created = await first.CreateAsync(GitHubRequest(), CancellationToken.None);
        var second = await NewServiceAsync(OtherId);

        var again = await second.CreateAsync(
            new CreateGitConnectionRequest { Provider = "GitHub", Token = SecondPat, DisplayName = "Other name" }, CancellationToken.None);

        Assert.Equal(GitConnectionCreateOutcomes.Existing, again.Outcome);
        Assert.Equal(created.Connection.Id, again.Connection.Id);
        Assert.Equal(CreatorId, again.Connection.CreatedByUserId);
        Assert.False(again.Connection.CanMaintain);
        await using var verification = OpenContext();
        var row = await verification.GitConnections.SingleAsync();
        Assert.Equal(CanaryPat, Protector().Unprotect(row.ProtectedToken));
        Assert.Equal("octocat", row.DisplayName);
    }

    [Fact]
    public async Task Create_WhenAccountHasASoftDeletedConnection_RestoresItWithTheNewValidatedCredential()
    {
        var first = await NewServiceAsync(CreatorId);
        var created = await first.CreateAsync(GitHubRequest(), CancellationToken.None);
        await first.SetEnabledAsync(created.Connection.Id, false, CancellationToken.None);
        await first.DeleteAsync(created.Connection.Id, CancellationToken.None);
        var second = await NewServiceAsync(OtherId);

        var restored = await second.CreateAsync(
            new CreateGitConnectionRequest { Provider = "GitHub", Token = SecondPat, DisplayName = "Restored" }, CancellationToken.None);

        Assert.Equal(GitConnectionCreateOutcomes.Restored, restored.Outcome);
        Assert.Equal(created.Connection.Id, restored.Connection.Id);
        Assert.Equal(OtherId, restored.Connection.CreatedByUserId);
        Assert.Equal("Restored", restored.Connection.DisplayName);
        // Policy: a restore never silently re-enables a connection that was disabled.
        Assert.False(restored.Connection.IsEnabled);
        Assert.Equal("Disabled", restored.Connection.State);
        await using var verification = OpenContext();
        var row = await verification.GitConnections.SingleAsync();
        Assert.False(row.IsDeleted);
        Assert.Null(row.DeletedAt);
        Assert.Equal(SecondPat, Protector().Unprotect(row.ProtectedToken));
        Assert.Contains(await verification.GitConnectionAuditEvents.ToListAsync(),
            audit => audit.EventType == GitConnectionAuditEventType.Restored && audit.ActorUserId == OtherId);
    }

    [Fact]
    public async Task Create_WhenTwoCallersCreateTheSameAccountAtOnce_StoresOneRowAndReturnsItToBoth()
    {
        var gate = new TaskCompletionSource();
        var arrived = 0;
        _github.ValidateGate = async () =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                gate.SetResult();
            }

            await gate.Task;
        };
        var first = await NewServiceAsync(CreatorId);
        var second = await NewServiceAsync(OtherId);

        var results = await Task.WhenAll(
            first.CreateAsync(GitHubRequest(), CancellationToken.None),
            second.CreateAsync(new CreateGitConnectionRequest { Provider = "GitHub", Token = SecondPat }, CancellationToken.None));

        Assert.Equal(1, results.Count(result => result.Outcome == GitConnectionCreateOutcomes.Created));
        Assert.Equal(1, results.Count(result => result.Outcome == GitConnectionCreateOutcomes.Existing));
        Assert.Equal(results[0].Connection.Id, results[1].Connection.Id);
        await using var verification = OpenContext();
        Assert.Single(await verification.GitConnections.ToListAsync());
    }

    [Fact]
    public async Task Create_WritesAnAuditEventWithoutSecrets()
    {
        var service = await NewServiceAsync(CreatorId);

        var result = await service.CreateAsync(GitHubRequest(), CancellationToken.None);

        await using var verification = OpenContext();
        var audit = await verification.GitConnectionAuditEvents.SingleAsync();
        Assert.Equal(result.Connection.Id, audit.GitConnectionId);
        Assert.Equal(GitConnectionAuditEventType.Created, audit.EventType);
        Assert.Equal(GitConnectionAuditOutcome.Success, audit.Outcome);
        Assert.Equal(CreatorId, audit.ActorUserId);
        Assert.DoesNotContain(CanaryPat, JsonSerializer.Serialize(audit, new JsonSerializerOptions { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles }));
    }

    // ---- list and get ----

    [Fact]
    public async Task List_ReturnsEveryActiveConnectionWithCapabilityForTheCaller()
    {
        var creator = await NewServiceAsync(CreatorId);
        var github = await creator.CreateAsync(GitHubRequest(), CancellationToken.None);
        var gitlab = await creator.CreateAsync(new CreateGitConnectionRequest { Provider = "GitLab", Token = CanaryPat }, CancellationToken.None);
        await creator.DeleteAsync(gitlab.Connection.Id, CancellationToken.None);

        var asOther = await (await NewServiceAsync(OtherId)).ListAsync(CancellationToken.None);
        var asAdmin = await (await NewServiceAsync(AdminId)).ListAsync(CancellationToken.None);

        Assert.Equal(github.Connection.Id, Assert.Single(asOther).Id);
        Assert.False(asOther[0].CanMaintain);
        Assert.True(Assert.Single(asAdmin).CanMaintain);
    }

    [Fact]
    public async Task List_CountsOnlyActiveRepositoriesOfEachConnection()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        await AddRepositoryAsync("r1", created.Connection.Id);
        await AddRepositoryAsync("r2", created.Connection.Id, deleted: true);

        var list = await service.ListAsync(CancellationToken.None);

        Assert.Equal(1, Assert.Single(list).RepositoryCount);
    }

    [Fact]
    public async Task Get_WhenConnectionDoesNotExistOrIsDeleted_IsNotFound()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        await service.DeleteAsync(created.Connection.Id, CancellationToken.None);

        await AssertError(GitConnectionErrorCodes.NotFound, () => service.GetAsync(created.Connection.Id, CancellationToken.None));
        await AssertError(GitConnectionErrorCodes.NotFound, () => service.GetAsync("missing", CancellationToken.None));
    }

    // ---- update ----

    [Fact]
    public async Task Update_AllowsCreatorAndAdminToRenameButNotOthers()
    {
        var creator = await NewServiceAsync(CreatorId);
        var created = await creator.CreateAsync(GitHubRequest(), CancellationToken.None);

        var renamed = await creator.UpdateAsync(created.Connection.Id, new UpdateGitConnectionRequest { DisplayName = "Team GitHub" }, CancellationToken.None);
        var byAdmin = await (await NewServiceAsync(AdminId)).UpdateAsync(
            created.Connection.Id, new UpdateGitConnectionRequest { DisplayName = "Admin name" }, CancellationToken.None);
        await AssertError(GitConnectionErrorCodes.MaintenanceForbidden, async () =>
            await (await NewServiceAsync(OtherId)).UpdateAsync(
                created.Connection.Id, new UpdateGitConnectionRequest { DisplayName = "Hijacked" }, CancellationToken.None));

        Assert.Equal("Team GitHub", renamed.DisplayName);
        Assert.Equal("Admin name", byAdmin.DisplayName);
        await using var verification = OpenContext();
        Assert.Equal("Admin name", (await verification.GitConnections.SingleAsync()).DisplayName);
        Assert.Equal(2, await verification.GitConnectionAuditEvents.CountAsync(audit => audit.EventType == GitConnectionAuditEventType.Renamed));
    }

    [Fact]
    public async Task Update_WithNewToken_ValidatesBeforeSwappingAndRecordsRotation()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        _github.ValidateCalls.Clear();

        var updated = await service.UpdateAsync(created.Connection.Id, new UpdateGitConnectionRequest { Token = SecondPat }, CancellationToken.None);

        Assert.True(updated.HasSecret);
        Assert.Equal(created.Connection.Id, Assert.Single(_github.ValidateCalls).ConnectionId);
        await using var verification = OpenContext();
        Assert.Equal(SecondPat, Protector().Unprotect((await verification.GitConnections.SingleAsync()).ProtectedToken));
        Assert.Contains(await verification.GitConnectionAuditEvents.ToListAsync(),
            audit => audit.EventType == GitConnectionAuditEventType.CredentialRotated && audit.Outcome == GitConnectionAuditOutcome.Success);
    }

    [Fact]
    public async Task Update_WhenReplacementTokenIsInvalid_KeepsTheActiveCredentialAndRecordsTheFailure()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        string before;
        await using (var snapshot = OpenContext())
        {
            before = (await snapshot.GitConnections.SingleAsync()).ProtectedToken;
        }

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => service.UpdateAsync(
            created.Connection.Id, new UpdateGitConnectionRequest { Token = "ghp_revoked_token" }, CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.Unauthorized, exception.Code);
        await using var verification = OpenContext();
        var row = await verification.GitConnections.SingleAsync();
        Assert.Equal(before, row.ProtectedToken);
        Assert.Null(row.LastValidationErrorCode);
        Assert.Contains(await verification.GitConnectionAuditEvents.ToListAsync(), audit =>
            audit.EventType == GitConnectionAuditEventType.CredentialRotated
            && audit.Outcome == GitConnectionAuditOutcome.Failure
            && audit.ErrorCode == GitProviderErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Update_WhenRenameAndInvalidTokenAreSentTogether_PersistsNeither()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        GitConnection before;
        await using (var snapshot = OpenContext())
        {
            before = await snapshot.GitConnections.AsNoTracking().SingleAsync();
        }

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => service.UpdateAsync(
            created.Connection.Id,
            new UpdateGitConnectionRequest { DisplayName = "Renamed", Token = "ghp_revoked_token" },
            CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.Unauthorized, exception.Code);
        await using var verification = OpenContext();
        var row = await verification.GitConnections.SingleAsync();
        Assert.Equal(before.DisplayName, row.DisplayName);
        Assert.Equal(before.ProtectedToken, row.ProtectedToken);
        Assert.Equal(before.ConcurrencyStamp, row.ConcurrencyStamp);
        Assert.Equal(before.UpdatedAt, row.UpdatedAt);
        var events = await verification.GitConnectionAuditEvents.ToListAsync();
        Assert.DoesNotContain(events, audit => audit.EventType == GitConnectionAuditEventType.Renamed);
        Assert.Equal(1, events.Count(audit => audit.EventType == GitConnectionAuditEventType.CredentialRotated));
    }

    [Fact]
    public async Task Update_WhenReplacementTokenBelongsToAnotherAccount_IsRejectedWithoutChange()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(new CreateGitConnectionRequest { Provider = "GitLab", Token = CanaryPat }, CancellationToken.None);

        await AssertError(GitConnectionErrorCodes.AccountMismatch, () => service.UpdateAsync(
            created.Connection.Id, new UpdateGitConnectionRequest { Token = SecondPat }, CancellationToken.None));

        await using var verification = OpenContext();
        Assert.Equal(CanaryPat, Protector().Unprotect((await verification.GitConnections.SingleAsync()).ProtectedToken));
    }

    [Fact]
    public async Task Update_WhenNonMaintainerSendsToken_NeverCallsTheProvider()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(GitHubRequest(), CancellationToken.None);
        _github.ValidateCalls.Clear();

        await AssertError(GitConnectionErrorCodes.MaintenanceForbidden, async () =>
            await (await NewServiceAsync(OtherId)).UpdateAsync(
                created.Connection.Id, new UpdateGitConnectionRequest { Token = SecondPat }, CancellationToken.None));

        Assert.Empty(_github.ValidateCalls);
    }

    [Fact]
    public async Task Update_WhenAnotherWriterChangesTheRowDuringValidation_ReturnsConflict()
    {
        var first = await NewServiceAsync(CreatorId);
        var created = await first.CreateAsync(GitHubRequest(), CancellationToken.None);
        var release = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        _github.ValidateGate = async () =>
        {
            reached.TrySetResult();
            await release.Task;
        };

        var slow = first.UpdateAsync(created.Connection.Id, new UpdateGitConnectionRequest { Token = SecondPat }, CancellationToken.None);
        await reached.Task;
        _github.ValidateGate = null;
        var second = await NewServiceAsync(CreatorId);
        await second.UpdateAsync(created.Connection.Id, new UpdateGitConnectionRequest { DisplayName = "Won the race" }, CancellationToken.None);
        release.SetResult();

        var error = await Assert.ThrowsAsync<GitConnectionServiceException>(() => slow);

        Assert.Equal(GitConnectionErrorCodes.Conflict, error.ErrorCode);
        await using var verification = OpenContext();
        var row = await verification.GitConnections.SingleAsync();
        Assert.Equal("Won the race", row.DisplayName);
        Assert.Equal(CanaryPat, Protector().Unprotect(row.ProtectedToken));
    }

    [Fact]
    public async Task Update_WhenTwoRenamesRaceOnTheSameRow_OneWinsAndOneGetsConflict()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(GitHubRequest(), CancellationToken.None);
        var contextA = OpenContext();
        var contextB = OpenContext();
        var rowA = await contextA.GitConnections.SingleAsync();
        var rowB = await contextB.GitConnections.SingleAsync();
        rowA.DisplayName = "A";
        rowA.ConcurrencyStamp = Guid.NewGuid().ToString();
        rowB.DisplayName = "B";
        rowB.ConcurrencyStamp = Guid.NewGuid().ToString();
        await contextA.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => contextB.SaveChangesAsync());
        Assert.Equal(created.Connection.Id, rowA.Id);
        await contextA.DisposeAsync();
        await contextB.DisposeAsync();
    }

    // ---- enable and disable ----

    [Fact]
    public async Task Disable_AllowsMaintainersOnlyAndEnableRestoresUse()
    {
        var creator = await NewServiceAsync(CreatorId);
        var created = await creator.CreateAsync(GitHubRequest(), CancellationToken.None);

        await AssertError(GitConnectionErrorCodes.MaintenanceForbidden, async () =>
            await (await NewServiceAsync(OtherId)).SetEnabledAsync(created.Connection.Id, false, CancellationToken.None));
        var disabled = await creator.SetEnabledAsync(created.Connection.Id, false, CancellationToken.None);
        var enabled = await (await NewServiceAsync(AdminId)).SetEnabledAsync(created.Connection.Id, true, CancellationToken.None);

        Assert.False(disabled.IsEnabled);
        Assert.Equal("Disabled", disabled.State);
        Assert.True(enabled.IsEnabled);
        await using var verification = OpenContext();
        var events = (await verification.GitConnectionAuditEvents.ToListAsync()).Select(audit => audit.EventType).ToList();
        Assert.Contains(GitConnectionAuditEventType.Disabled, events);
        Assert.Contains(GitConnectionAuditEventType.Enabled, events);
    }

    [Fact]
    public async Task Disable_WhenAlreadyDisabled_IsIdempotentAndWritesNoSecondAudit()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);

        await service.SetEnabledAsync(created.Connection.Id, false, CancellationToken.None);
        await service.SetEnabledAsync(created.Connection.Id, false, CancellationToken.None);

        await using var verification = OpenContext();
        Assert.Equal(1, await verification.GitConnectionAuditEvents.CountAsync(audit => audit.EventType == GitConnectionAuditEventType.Disabled));
    }

    [Fact]
    public async Task Disable_KeepsRepositoriesButBlocksDiscoveryWithStableCode()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        await AddRepositoryAsync("r1", created.Connection.Id);
        await service.SetEnabledAsync(created.Connection.Id, false, CancellationToken.None);

        await AssertError(GitConnectionErrorCodes.Disabled,
            () => service.ListRepositoriesAsync(created.Connection.Id, null, 50, CancellationToken.None));
        await AssertError(GitConnectionErrorCodes.Disabled,
            () => service.ListBranchesAsync(created.Connection.Id, "42", null, 50, CancellationToken.None));

        Assert.Empty(_github.CatalogCalls);
        await using var verification = OpenContext();
        var repository = await verification.Repositories.SingleAsync();
        Assert.False(repository.IsDeleted);
        Assert.Equal(created.Connection.Id, repository.GitConnectionId);
        Assert.Contains(await verification.GitConnectionAuditEvents.ToListAsync(), audit =>
            audit.EventType == GitConnectionAuditEventType.UseDenied && audit.ErrorCode == GitConnectionErrorCodes.Disabled);
    }

    // ---- health check ----

    [Fact]
    public async Task Test_OnSuccess_RefreshesValidationTimeAndReportsHealthy()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        await Task.Delay(20);

        var health = await service.TestAsync(created.Connection.Id, CancellationToken.None);

        Assert.True(health.Ok);
        Assert.Equal("Healthy", health.State);
        Assert.Null(health.ErrorCode);
        Assert.True(health.LatencyMs >= 0);
        await using var verification = OpenContext();
        var row = await verification.GitConnections.SingleAsync();
        Assert.True(row.LastValidatedAt >= health.CheckedAt.AddSeconds(-5));
        Assert.Null(row.LastValidationErrorCode);
    }

    [Fact]
    public async Task Test_WhenProviderRejectsTheStoredToken_ReportsWarningWithStableCode()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        _github.ValidateFailure = new GitProviderException(GitProviderErrorCodes.Unauthorized);

        var health = await service.TestAsync(created.Connection.Id, CancellationToken.None);

        Assert.False(health.Ok);
        Assert.Equal("Warning", health.State);
        Assert.Equal(GitProviderErrorCodes.Unauthorized, health.ErrorCode);
        var fresh = await service.GetAsync(created.Connection.Id, CancellationToken.None);
        Assert.Equal("Warning", fresh.State);
        Assert.Equal(GitProviderErrorCodes.Unauthorized, fresh.LastValidationErrorCode);
    }

    [Fact]
    public async Task Test_WhenTokenNowBelongsToAnotherAccount_ReportsMismatch()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        _github.IdentityByToken[CanaryPat] = new GitProviderIdentity("9999", "someone-else");

        var health = await service.TestAsync(created.Connection.Id, CancellationToken.None);

        Assert.False(health.Ok);
        Assert.Equal(GitConnectionErrorCodes.AccountMismatch, health.ErrorCode);
    }

    [Fact]
    public async Task Test_ByNonMaintainer_IsForbiddenWithoutProviderCall()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(GitHubRequest(), CancellationToken.None);
        _github.ValidateCalls.Clear();

        await AssertError(GitConnectionErrorCodes.MaintenanceForbidden, async () =>
            await (await NewServiceAsync(OtherId)).TestAsync(created.Connection.Id, CancellationToken.None));

        Assert.Empty(_github.ValidateCalls);
    }

    // ---- delete ----

    [Fact]
    public async Task Delete_WhenActiveRepositoryDependsOnTheConnection_ReturnsConflictAndKeepsIt()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        await AddRepositoryAsync("r1", created.Connection.Id);

        await AssertError(GitConnectionErrorCodes.InUse, () => service.DeleteAsync(created.Connection.Id, CancellationToken.None));

        await using var verification = OpenContext();
        var row = await verification.GitConnections.SingleAsync();
        Assert.False(row.IsDeleted);
        Assert.NotEmpty(row.ProtectedToken);
    }

    [Fact]
    public async Task Delete_SoftDeletesScrubsTheCredentialAndKeepsTheRowAndAudit()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        await AddRepositoryAsync("r1", created.Connection.Id, deleted: true);

        await service.DeleteAsync(created.Connection.Id, CancellationToken.None);

        await using var verification = OpenContext();
        var row = await verification.GitConnections.SingleAsync();
        Assert.True(row.IsDeleted);
        Assert.NotNull(row.DeletedAt);
        Assert.Equal(string.Empty, row.ProtectedToken);
        Assert.Equal(2, await verification.GitConnectionAuditEvents.CountAsync(audit => audit.GitConnectionId == row.Id));
        Assert.Contains(await verification.GitConnectionAuditEvents.ToListAsync(), audit => audit.EventType == GitConnectionAuditEventType.Deleted);
        await AssertError(GitConnectionErrorCodes.NotFound, () => service.GetAsync(created.Connection.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_ByNonMaintainer_IsForbiddenAndAdminMayDelete()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(GitHubRequest(), CancellationToken.None);

        await AssertError(GitConnectionErrorCodes.MaintenanceForbidden, async () =>
            await (await NewServiceAsync(OtherId)).DeleteAsync(created.Connection.Id, CancellationToken.None));
        await (await NewServiceAsync(AdminId)).DeleteAsync(created.Connection.Id, CancellationToken.None);

        await using var verification = OpenContext();
        Assert.True((await verification.GitConnections.SingleAsync()).IsDeleted);
    }

    // ---- catalog ----

    [Fact]
    public async Task Catalog_WithGitLabClient_UsesProviderSearchWithoutReadingTheFullCatalog()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(
            new CreateGitConnectionRequest { Provider = "GitLab", Token = CanaryPat }, CancellationToken.None);
        var handler = new FakeProviderHandler();
        handler.EnqueueJson("""
            [{"id":196,"name":"target","path_with_namespace":"acme/target",
              "http_url_to_repo":"https://gitlab.com/acme/target.git","visibility":"private"}]
            """, link: "<https://gitlab.com/api/v4/projects?page=2>; rel=\"next\"");
        var validator = GitProviderTestFactory.CreateValidator(new FakeHostResolver().Add("gitlab.com", PublicIp));
        var client = new GitLabPatProviderClient(
            new FakeHttpClientFactory(_ => GitProviderTestFactory.CreateGuardedClient(handler, validator)),
            new ProviderPaginationCursorCodec(_dataProtection), new ListLogger<GitLabPatProviderClient>());
        var service = await NewServiceAsync(OtherId, gitlab: client);

        var result = await service.ListCatalogAsync(created.Connection.Id, null, 50, " TARGET ", "all", "updated", CancellationToken.None);

        Assert.Equal("196", Assert.Single(result.Items).ProviderRepositoryId);
        Assert.NotNull(result.NextCursor);
        Assert.Null(result.TotalCount);
        var request = Assert.Single(handler.Requests);
        Assert.Contains("search=target", request.Uri.Query);
        Assert.Contains("per_page=50", request.Uri.Query);
    }

    [Theory]
    [InlineData("GitHub")]
    [InlineData("GitLab")]
    public async Task Catalog_SearchesEveryProviderPage_AndFiltersBeforePagination(string provider)
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(new CreateGitConnectionRequest { Provider = provider, Token = CanaryPat }, CancellationToken.None);
        var client = provider == "GitHub" ? _github : _gitlab;
        client.RepositoryPages = cursor => cursor is null
            ? new ProviderPage<RemoteRepository>([CatalogRepository("1", "first", "Public")], "later")
            : new ProviderPage<RemoteRepository>([CatalogRepository("2", "target", "Private")], null);

        var result = await service.ListCatalogAsync(created.Connection.Id, null, 50, "TARGET", "private", "name", CancellationToken.None);

        Assert.Equal("2", Assert.Single(result.Items).ProviderRepositoryId);
        Assert.Equal(1, result.TotalCount);
        Assert.Null(result.NextCursor);
        Assert.Equal([null, "later"], client.CatalogCalls.Select(call => call.Cursor));
    }

    [Fact]
    public async Task Catalog_SortsAllPages_AndBindsCursorToFiltersAndConnection()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        _github.RepositoryPages = cursor => cursor is null
            ? new ProviderPage<RemoteRepository>([CatalogRepository("1", "zeta", "Public")], "later")
            : new ProviderPage<RemoteRepository>([CatalogRepository("2", "alpha", "Private")], null);
        var first = await service.ListCatalogAsync(created.Connection.Id, null, 1, "", "all", "name", CancellationToken.None);
        var second = await service.ListCatalogAsync(created.Connection.Id, first.NextCursor, 1, "", "all", "name", CancellationToken.None);
        Assert.Equal("alpha", Assert.Single(first.Items).Name);
        Assert.Equal("zeta", Assert.Single(second.Items).Name);
        Assert.Null(second.NextCursor);
        var invalid = await Assert.ThrowsAsync<GitProviderException>(() => service.ListCatalogAsync(
            created.Connection.Id, first.NextCursor, 1, "alpha", "all", "name", CancellationToken.None));
        Assert.Equal(GitProviderErrorCodes.InvalidCursor, invalid.Code);
    }

    [Fact]
    public async Task Catalog_RejectsRepeatedProviderCursor_InsteadOfReturningPartialResults()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        var invalid = await Assert.ThrowsAsync<GitProviderException>(() => service.ListCatalogAsync(
            created.Connection.Id, null, 50, "", "all", "name", CancellationToken.None));
        Assert.Equal(GitProviderErrorCodes.InvalidResponse, invalid.Code);
    }

    private static RemoteRepository CatalogRepository(string id, string name, string visibility) => new(
        id, name, $"acme/{name}", "acme", null, $"https://github.com/acme/{name}.git", null, "main", visibility, null);

    [Fact]
    public async Task ListRepositories_LetsAnyAuthenticatedUserUseTheSharedConnection()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(GitHubRequest(), CancellationToken.None);
        var other = await NewServiceAsync(OtherId);

        var page = await other.ListRepositoriesAsync(created.Connection.Id, "cursor-in", 25, CancellationToken.None);

        Assert.Equal("42", Assert.Single(page.Items).ProviderRepositoryId);
        Assert.Equal("next-cursor", page.NextCursor);
        var call = Assert.Single(_github.CatalogCalls);
        Assert.Equal(created.Connection.Id, call.ConnectionId);
        Assert.Equal(CanaryPat, call.Token);
        Assert.Equal("cursor-in", call.Cursor);
        Assert.Equal(25, call.PageSize);
        Assert.DoesNotContain(CanaryPat, JsonSerializer.Serialize(page));
    }

    [Fact]
    public async Task ListBranches_PassesTheStableRepositoryIdToTheProvider()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(GitHubRequest(), CancellationToken.None);

        var page = await (await NewServiceAsync(OtherId)).ListBranchesAsync(created.Connection.Id, "42", null, 50, CancellationToken.None);

        Assert.Equal("main", Assert.Single(page.Items).Name);
        Assert.Equal("42", Assert.Single(_github.CatalogCalls).RepositoryId);
    }

    [Fact]
    public async Task ListRepositories_WhenConnectionIsMissingOrDeleted_IsNotFound()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        await service.DeleteAsync(created.Connection.Id, CancellationToken.None);

        await AssertError(GitConnectionErrorCodes.NotFound, () => service.ListRepositoriesAsync(created.Connection.Id, null, 50, CancellationToken.None));
        await AssertError(GitConnectionErrorCodes.NotFound, () => service.ListRepositoriesAsync("missing", null, 50, CancellationToken.None));
    }

    [Fact]
    public async Task ListRepositories_WhenCallerIsAnonymous_IsUnauthorized()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(GitHubRequest(), CancellationToken.None);

        await AssertError(GitConnectionErrorCodes.Unauthorized, async () =>
            await (await NewServiceAsync(null)).ListRepositoriesAsync(created.Connection.Id, null, 50, CancellationToken.None));
    }

    [Fact]
    public async Task ListRepositories_WhenProviderFails_PropagatesTheStableCode()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(GitHubRequest(), CancellationToken.None);
        _github.CatalogFailure = new GitProviderException(GitProviderErrorCodes.RateLimited, TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAsync<GitProviderException>(
            async () => await (await NewServiceAsync(OtherId)).ListRepositoriesAsync(created.Connection.Id, null, 50, CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.RateLimited, exception.Code);
        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
    }

    [Fact]
    public async Task ListRepositories_WhenStoredSecretCannotBeRead_ReportsStableCode()
    {
        var created = await (await NewServiceAsync(CreatorId)).CreateAsync(GitHubRequest(), CancellationToken.None);
        await using (var context = OpenContext())
        {
            (await context.GitConnections.SingleAsync()).ProtectedToken = "not-protected-data";
            await context.SaveChangesAsync();
        }

        await AssertError(GitConnectionErrorCodes.SecretUnreadable,
            async () => await (await NewServiceAsync(OtherId)).ListRepositoriesAsync(created.Connection.Id, null, 50, CancellationToken.None));
        Assert.Empty(_github.CatalogCalls);
    }

    // ---- audit ----

    [Fact]
    public async Task ListAuditEvents_AfterRestore_HidesEarlierHistoryFromTheNewCreatorButNotFromAdmin()
    {
        var first = await NewServiceAsync(CreatorId);
        var created = await first.CreateAsync(GitHubRequest(), CancellationToken.None);
        await first.SetEnabledAsync(created.Connection.Id, false, CancellationToken.None);
        await first.DeleteAsync(created.Connection.Id, CancellationToken.None);
        var second = await NewServiceAsync(OtherId);
        await second.CreateAsync(
            new CreateGitConnectionRequest { Provider = "GitHub", Token = SecondPat, DisplayName = "Restored" }, CancellationToken.None);
        await second.SetEnabledAsync(created.Connection.Id, true, CancellationToken.None);

        var asNewCreator = await second.ListAuditEventsAsync(created.Connection.Id, 50, CancellationToken.None);
        var asAdmin = await (await NewServiceAsync(AdminId)).ListAuditEventsAsync(created.Connection.Id, 50, CancellationToken.None);

        Assert.Equal(["Enabled", "Restored"], asNewCreator.Select(item => item.EventType));
        Assert.All(asNewCreator, item => Assert.Equal(OtherId, item.ActorUserId));
        Assert.Equal(["Enabled", "Restored", "Deleted", "Disabled", "Created"], asAdmin.Select(item => item.EventType));
    }

    [Fact]
    public async Task ListAuditEvents_ReturnsNewestFirstForMaintainersOnly()
    {
        var service = await NewServiceAsync(CreatorId);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        await service.SetEnabledAsync(created.Connection.Id, false, CancellationToken.None);
        await service.SetEnabledAsync(created.Connection.Id, true, CancellationToken.None);

        var events = await service.ListAuditEventsAsync(created.Connection.Id, 2, CancellationToken.None);

        Assert.Equal(["Enabled", "Disabled"], events.Select(item => item.EventType));
        Assert.All(events, item => Assert.Equal(CreatorId, item.ActorUserId));
        Assert.DoesNotContain(CanaryPat, JsonSerializer.Serialize(events));
        await AssertError(GitConnectionErrorCodes.MaintenanceForbidden, async () =>
            await (await NewServiceAsync(OtherId)).ListAuditEventsAsync(created.Connection.Id, 10, CancellationToken.None));
    }

    // ---- secrets everywhere ----

    [Fact]
    public async Task Failures_NeverLeakTheTokenThroughExceptionsOrLogs()
    {
        var logger = new ListLogger<GitConnectionService>();
        var service = await NewServiceAsync(CreatorId, logger);
        var created = await service.CreateAsync(GitHubRequest(), CancellationToken.None);
        _github.ValidateFailure = new GitProviderException(GitProviderErrorCodes.Unauthorized);

        var rotation = await Assert.ThrowsAsync<GitProviderException>(() => service.UpdateAsync(
            created.Connection.Id, new UpdateGitConnectionRequest { Token = SecondPat }, CancellationToken.None));
        await service.TestAsync(created.Connection.Id, CancellationToken.None);

        Assert.DoesNotContain(SecondPat, rotation.ToString());
        Assert.All(logger.Lines, line => Assert.DoesNotContain(CanaryPat, line));
        Assert.All(logger.Lines, line => Assert.DoesNotContain(SecondPat, line));
        await using var verification = OpenContext();
        var audit = JsonSerializer.Serialize(
            (await verification.GitConnectionAuditEvents.ToListAsync()).Select(item => new { item.EventType, item.ErrorCode, item.CorrelationId }));
        Assert.DoesNotContain(CanaryPat, audit);
        Assert.DoesNotContain(SecondPat, audit);
    }

    // ---- helpers ----

    private static CreateGitConnectionRequest GitHubRequest() => new() { Provider = "GitHub", Token = CanaryPat };

    private IGitConnectionSecretProtector Protector() => new DataProtectionGitConnectionSecretProtector(_dataProtection);

    private ServiceTestContext OpenContext()
    {
        var context = new ServiceTestContext(new DbContextOptionsBuilder<ServiceTestContext>().UseSqlite($"Data Source={_dbPath}").Options);
        return context;
    }

    private async Task<IGitConnectionService> NewServiceAsync(
        string? userId, ListLogger<GitConnectionService>? logger = null, IGitProviderClient? gitlab = null)
    {
        var context = OpenContext();
        await context.Database.EnsureCreatedAsync();
        foreach (var id in new[] { CreatorId, OtherId, AdminId })
        {
            if (!await context.Users.AnyAsync(user => user.Id == id))
            {
                context.Users.Add(new User { Id = id, Name = id, Email = $"{id}@example.com" });
            }
        }

        if (!await context.Roles.AnyAsync(role => role.Name == "Admin"))
        {
            var role = new Role { Id = "role-admin", Name = "Admin", Description = "Admin", IsActive = true };
            context.Roles.Add(role);
            context.UserRoles.Add(new UserRole { Id = "link-admin", UserId = AdminId, RoleId = role.Id });
        }

        await context.SaveChangesAsync();
        _handles.Add(new ServiceHandle(context));

        var userContext = new TestUserContext(userId, userId is not null);
        var validator = GitProviderTestFactory.CreateValidator(new FakeHostResolver().Add("gitlab.corp.example", PublicIp));
        return new GitConnectionService(
            context,
            userContext,
            new GitConnectionAuthorizationService(userContext, context),
            Protector(),
            new GitProviderClientResolver([_github, gitlab ?? _gitlab]),
            validator,
            logger ?? new ListLogger<GitConnectionService>(),
            new ProviderPaginationCursorCodec(_dataProtection));
    }

    private async Task AddRepositoryAsync(string id, string connectionId, bool deleted = false)
    {
        await using var context = OpenContext();
        var repository = new Repository
        {
            Id = id,
            OwnerUserId = CreatorId,
            GitUrl = $"https://github.com/acme/{id}",
            OrgName = "acme",
            RepoName = id,
            GitConnectionId = connectionId
        };
        if (deleted)
        {
            repository.MarkAsDeleted();
        }

        context.Repositories.Add(repository);
        await context.SaveChangesAsync();
    }

    private static async Task AssertError(string expected, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<GitConnectionServiceException>(action);
        Assert.Equal(expected, error.ErrorCode);
    }

    private sealed record ServiceHandle(ServiceTestContext Context);

    private sealed class ServiceTestContext(DbContextOptions<ServiceTestContext> options) : MasterDbContext(options);
}
