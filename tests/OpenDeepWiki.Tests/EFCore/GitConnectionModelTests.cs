using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.EFCore;

public sealed class GitConnectionModelTests : IDisposable
{
    private const string CanaryPat = "ghp_CANARY_model_test_0123456789";

    private readonly string _dbPath = Path.Combine(TestPaths.ScratchRoot, $"git-connection-model-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqlitePools.Release(_dbPath);
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Insert_WhenIdentityAlreadyExists_ViolatesTheGlobalUniqueKey()
    {
        await using var context = await CreateContextAsync();
        context.GitConnections.Add(NewConnection("c1", "1001"));
        await context.SaveChangesAsync();

        context.GitConnections.Add(NewConnection("c2", "1001", createdBy: "user-2"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Insert_WhenExistingIdentityIsSoftDeleted_StillViolatesTheGlobalUniqueKey()
    {
        await using var context = await CreateContextAsync();
        var existing = NewConnection("c1", "1001");
        existing.MarkAsDeleted();
        context.GitConnections.Add(existing);
        await context.SaveChangesAsync();

        context.GitConnections.Add(NewConnection("c2", "1001"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Restore_WhenIdentityMatchesSoftDeletedRow_KeepsOneRowAndPreservesDisabledState()
    {
        await using var context = await CreateContextAsync();
        var existing = NewConnection("c1", "1001");
        existing.IsEnabled = false;
        existing.MarkAsDeleted();
        context.GitConnections.Add(existing);
        await context.SaveChangesAsync();

        var match = await context.GitConnections.SingleAsync(connection =>
            connection.Provider == GitProvider.GitHub
            && connection.NormalizedServerUrl == "https://github.com"
            && connection.ExternalAccountId == "1001");
        match.Restore();
        await context.SaveChangesAsync();

        await using var verification = CreateContext();
        var rows = await verification.GitConnections.ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("c1", row.Id);
        Assert.False(row.IsDeleted);
        Assert.Null(row.DeletedAt);
        Assert.NotNull(row.UpdatedAt);
        Assert.False(row.IsEnabled);
    }

    [Theory]
    [InlineData(GitProvider.GitLab, "https://github.com", "1001")]
    [InlineData(GitProvider.GitHub, "https://gitlab.example.com", "1001")]
    [InlineData(GitProvider.GitHub, "https://github.com", "1002")]
    public async Task Insert_WhenAnyIdentityPartDiffers_IsAllowed(
        GitProvider provider,
        string serverUrl,
        string externalAccountId)
    {
        await using var context = await CreateContextAsync();
        context.GitConnections.Add(NewConnection("c1", "1001"));
        await context.SaveChangesAsync();

        var other = NewConnection("c2", externalAccountId);
        other.Provider = provider;
        other.NormalizedServerUrl = serverUrl;
        context.GitConnections.Add(other);

        await context.SaveChangesAsync();
        Assert.Equal(2, await context.GitConnections.CountAsync());
    }

    [Fact]
    public async Task DeleteConnection_WhenRepositoryReferencesIt_IsRestrictedByTheDatabase()
    {
        await using (var seed = await CreateContextAsync())
        {
            seed.GitConnections.Add(NewConnection("c1", "1001"));
            seed.Repositories.Add(NewRepository("r1", "c1"));
            await seed.SaveChangesAsync();
        }

        // The repository is not tracked here, so only the database foreign key can protect it.
        await using var context = CreateContext();
        context.GitConnections.Remove(await context.GitConnections.SingleAsync());

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        await using var verification = CreateContext();
        var repository = await verification.Repositories.SingleAsync();
        Assert.Equal("c1", repository.GitConnectionId);
    }

    [Fact]
    public async Task DeleteUser_WhenConnectionWasCreatedByThem_IsRestricted()
    {
        await using var context = await CreateContextAsync();
        context.GitConnections.Add(NewConnection("c1", "1001"));
        await context.SaveChangesAsync();

        context.Users.Remove(await context.Users.SingleAsync(user => user.Id == "user-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Repository_WhenNoConnectionIsAssigned_StaysNull()
    {
        await using var context = await CreateContextAsync();
        context.Repositories.Add(NewRepository("r1", connectionId: null));
        await context.SaveChangesAsync();

        await using var verification = CreateContext();
        Assert.Null((await verification.Repositories.SingleAsync()).GitConnectionId);
    }

    [Fact]
    public async Task Insert_WhenRepositoryReferencesMissingConnection_ViolatesTheForeignKey()
    {
        await using var context = await CreateContextAsync();
        context.Repositories.Add(NewRepository("r1", "missing"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task ProtectedToken_RoundTripsLargePayloadsWithoutTruncation()
    {
        await using var context = await CreateContextAsync();
        var largePayload = new string('x', 16 * 1024);
        var connection = NewConnection("c1", "1001");
        connection.ProtectedToken = largePayload;
        context.GitConnections.Add(connection);
        await context.SaveChangesAsync();

        await using var verification = CreateContext();
        Assert.Equal(largePayload, (await verification.GitConnections.SingleAsync()).ProtectedToken);
    }

    [Fact]
    public async Task Model_DeclaresRestrictRelationsAndUnboundedProtectedToken()
    {
        await using var context = await CreateContextAsync();
        var connectionType = context.Model.FindEntityType(typeof(GitConnection))!;
        var auditType = context.Model.FindEntityType(typeof(GitConnectionAuditEvent))!;
        var repositoryType = context.Model.FindEntityType(typeof(Repository))!;

        Assert.Null(connectionType.FindProperty(nameof(GitConnection.ProtectedToken))!.GetMaxLength());
        Assert.False(connectionType.FindProperty(nameof(GitConnection.ProtectedToken))!.IsNullable);

        var repositoryForeignKey = Assert.Single(repositoryType.GetForeignKeys(),
            key => key.PrincipalEntityType == connectionType);
        Assert.Equal(DeleteBehavior.Restrict, repositoryForeignKey.DeleteBehavior);
        Assert.False(repositoryForeignKey.IsRequired);

        var creatorForeignKey = Assert.Single(connectionType.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, creatorForeignKey.DeleteBehavior);

        var auditForeignKey = Assert.Single(auditType.GetForeignKeys());
        Assert.Equal(DeleteBehavior.Restrict, auditForeignKey.DeleteBehavior);

        var identityIndex = Assert.Single(connectionType.GetIndexes(), index =>
            index.Properties.Select(property => property.Name).SequenceEqual(
                [nameof(GitConnection.Provider), nameof(GitConnection.NormalizedServerUrl), nameof(GitConnection.ExternalAccountId)]));
        Assert.True(identityIndex.IsUnique);
        Assert.Null(identityIndex.GetFilter());
    }

    [Fact]
    public async Task AuditEvent_RoundTripsOnlySafeFields()
    {
        await using var context = await CreateContextAsync();
        context.GitConnections.Add(NewConnection("c1", "1001"));
        context.GitConnectionAuditEvents.Add(NewAuditEvent("e1"));
        await context.SaveChangesAsync();

        await using var verification = CreateContext();
        var audit = await verification.GitConnectionAuditEvents.SingleAsync();
        Assert.Equal("c1", audit.GitConnectionId);
        Assert.Equal(GitConnectionAuditEventType.UseDenied, audit.EventType);
        Assert.Equal(GitConnectionAuditOutcome.Failure, audit.Outcome);
        Assert.Equal("connection_disabled", audit.ErrorCode);

        var safeProperties = typeof(GitConnectionAuditEvent).GetProperties().Select(property => property.Name).ToHashSet();
        Assert.Subset(
            new HashSet<string>
            {
                "Id", "GitConnectionId", "ActorUserId", "EventType", "Outcome",
                "RepositoryId", "CorrelationId", "ErrorCode", "CreatedAt", "GitConnection"
            },
            safeProperties);
    }

    [Fact]
    public async Task AuditEvent_WhenModifiedAfterInsert_IsRejected()
    {
        await using var context = await CreateContextAsync();
        context.GitConnections.Add(NewConnection("c1", "1001"));
        context.GitConnectionAuditEvents.Add(NewAuditEvent("e1"));
        await context.SaveChangesAsync();

        var audit = await context.GitConnectionAuditEvents.SingleAsync();
        audit.ErrorCode = "tampered";

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditEvent_WhenDeleted_IsRejected()
    {
        await using var context = await CreateContextAsync();
        context.GitConnections.Add(NewConnection("c1", "1001"));
        context.GitConnectionAuditEvents.Add(NewAuditEvent("e1"));
        await context.SaveChangesAsync();

        context.GitConnectionAuditEvents.Remove(await context.GitConnectionAuditEvents.SingleAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditEvent_RemainsReadableAfterConnectionIsSoftDeleted()
    {
        await using var context = await CreateContextAsync();
        var connection = NewConnection("c1", "1001");
        context.GitConnections.Add(connection);
        context.GitConnectionAuditEvents.Add(NewAuditEvent("e1"));
        await context.SaveChangesAsync();

        connection.MarkAsDeleted();
        await context.SaveChangesAsync();

        await using var verification = CreateContext();
        Assert.Equal(1, await verification.GitConnectionAuditEvents.CountAsync(audit => audit.GitConnectionId == "c1"));
    }

    [Fact]
    public async Task DatabaseFile_NeverContainsThePlainPat()
    {
        await using (var context = await CreateContextAsync())
        {
            var connection = NewConnection("c1", "1001");
            connection.ProtectedToken = new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider())
                .Protect(CanaryPat);
            context.GitConnections.Add(connection);
            context.GitConnectionAuditEvents.Add(NewAuditEvent("e1"));
            await context.SaveChangesAsync();
        }

        SqlitePools.Release(_dbPath);
        var bytes = await File.ReadAllBytesAsync(_dbPath);
        Assert.DoesNotContain(CanaryPat, System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Version_ConcurrentUpdatesBehaveAsDocumentedForSqlite()
    {
        await using var seed = await CreateContextAsync();
        seed.GitConnections.Add(NewConnection("c1", "1001"));
        await seed.SaveChangesAsync();

        await using var first = CreateContext();
        await using var second = CreateContext();
        var firstCopy = await first.GitConnections.SingleAsync();
        var secondCopy = await second.GitConnections.SingleAsync();
        firstCopy.AccountName = "first";
        secondCopy.AccountName = "second";
        await first.SaveChangesAsync();

        // SQLite does not generate row versions, so the second write is expected to win silently.
        // Maintenance code must therefore not rely on Version alone for conflict detection.
        await second.SaveChangesAsync();
        await using var verification = CreateContext();
        Assert.Equal("second", (await verification.GitConnections.SingleAsync()).AccountName);
    }

    [Fact]
    public async Task Model_DeclaresConcurrencyStampAsRequiredTokenAndDisplayNameWithLimit()
    {
        await using var context = await CreateContextAsync();
        var connectionType = context.Model.FindEntityType(typeof(GitConnection))!;

        var stamp = connectionType.FindProperty(nameof(GitConnection.ConcurrencyStamp))!;
        Assert.True(stamp.IsConcurrencyToken);
        Assert.False(stamp.IsNullable);
        Assert.Equal(36, stamp.GetMaxLength());

        var displayName = connectionType.FindProperty(nameof(GitConnection.DisplayName))!;
        Assert.False(displayName.IsNullable);
        Assert.Equal(200, displayName.GetMaxLength());
    }

    [Fact]
    public async Task ConcurrencyStamp_WhenAnotherWriterChangedTheRow_RejectsTheSecondUpdate()
    {
        await using var seed = await CreateContextAsync();
        seed.GitConnections.Add(NewConnection("c1", "1001"));
        await seed.SaveChangesAsync();

        await using var first = CreateContext();
        await using var second = CreateContext();
        var firstCopy = await first.GitConnections.SingleAsync();
        var secondCopy = await second.GitConnections.SingleAsync();
        firstCopy.DisplayName = "first";
        firstCopy.ConcurrencyStamp = Guid.NewGuid().ToString();
        secondCopy.DisplayName = "second";
        secondCopy.ConcurrencyStamp = Guid.NewGuid().ToString();
        await first.SaveChangesAsync();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
        await using var verification = CreateContext();
        Assert.Equal("first", (await verification.GitConnections.SingleAsync()).DisplayName);
    }

    private GitConnectionTestContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GitConnectionTestContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        return new GitConnectionTestContext(options);
    }

    private async Task<GitConnectionTestContext> CreateContextAsync()
    {
        var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.Users.AddRange(
            new User { Id = "user-1", Name = "one", Email = "one@example.com" },
            new User { Id = "user-2", Name = "two", Email = "two@example.com" });
        await context.SaveChangesAsync();
        return context;
    }

    private static GitConnection NewConnection(string id, string externalAccountId, string createdBy = "user-1") => new()
    {
        Id = id,
        Provider = GitProvider.GitHub,
        NormalizedServerUrl = "https://github.com",
        ExternalAccountId = externalAccountId,
        AccountName = "octocat",
        ProtectedToken = "protected-payload",
        CreatedByUserId = createdBy
    };

    private static Repository NewRepository(string id, string? connectionId) => new()
    {
        Id = id,
        OwnerUserId = "user-1",
        GitUrl = $"https://github.com/acme/{id}",
        OrgName = "acme",
        RepoName = id,
        GitConnectionId = connectionId
    };

    private static GitConnectionAuditEvent NewAuditEvent(string id) => new()
    {
        Id = id,
        GitConnectionId = "c1",
        ActorUserId = "user-2",
        EventType = GitConnectionAuditEventType.UseDenied,
        Outcome = GitConnectionAuditOutcome.Failure,
        ErrorCode = "connection_disabled",
        CorrelationId = "corr-1"
    };

    private sealed class GitConnectionTestContext(DbContextOptions<GitConnectionTestContext> options)
        : MasterDbContext(options);
}
