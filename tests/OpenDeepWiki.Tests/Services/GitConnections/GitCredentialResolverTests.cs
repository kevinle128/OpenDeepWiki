using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Tests.Chat.Sessions;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

public class GitCredentialResolverTests
{
    private const string CanaryPat = "glpat-CANARY-0123456789abcdef";

    [Fact]
    public async Task ResolveAsync_WhenConnectionIsEnabled_ReturnsUnprotectedCredential()
    {
        var fixture = await CreateFixtureAsync();

        var credential = await fixture.Resolver.ResolveAsync(fixture.Repository("connection-1"));

        Assert.NotNull(credential);
        Assert.Equal("octocat", credential.Username);
        Assert.Equal(CanaryPat, credential.Password);
    }

    [Fact]
    public async Task ResolveAsync_WhenConnectionHasNoAccountName_UsesNeutralUsername()
    {
        var fixture = await CreateFixtureAsync(accountName: null);

        var credential = await fixture.Resolver.ResolveAsync(fixture.Repository("connection-1"));

        Assert.NotNull(credential);
        Assert.False(string.IsNullOrWhiteSpace(credential.Username));
        Assert.Equal(CanaryPat, credential.Password);
    }

    [Fact]
    public async Task ResolveAsync_WhenConnectionIsDisabled_FailsWithoutLegacyFallback()
    {
        var fixture = await CreateFixtureAsync(isEnabled: false);

        var exception = await Assert.ThrowsAsync<GitCredentialResolutionException>(() =>
            fixture.Resolver.ResolveAsync(fixture.Repository("connection-1", legacyPassword: "legacy-secret")));

        Assert.Equal(GitCredentialErrorCodes.ConnectionDisabled, exception.ErrorCode);
    }

    [Fact]
    public async Task ResolveAsync_WhenConnectionIsSoftDeleted_FailsWithoutLegacyFallback()
    {
        var fixture = await CreateFixtureAsync(isDeleted: true);

        var exception = await Assert.ThrowsAsync<GitCredentialResolutionException>(() =>
            fixture.Resolver.ResolveAsync(fixture.Repository("connection-1", legacyPassword: "legacy-secret")));

        Assert.Equal(GitCredentialErrorCodes.ConnectionNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task ResolveAsync_WhenConnectionDoesNotExist_FailsWithoutLegacyFallback()
    {
        var fixture = await CreateFixtureAsync();

        var exception = await Assert.ThrowsAsync<GitCredentialResolutionException>(() =>
            fixture.Resolver.ResolveAsync(fixture.Repository("missing", legacyPassword: "legacy-secret")));

        Assert.Equal(GitCredentialErrorCodes.ConnectionNotFound, exception.ErrorCode);
    }

    [Fact]
    public async Task ResolveAsync_WhenProtectedPayloadIsCorrupt_FailsClosedWithoutLeakingSecrets()
    {
        var fixture = await CreateFixtureAsync(corruptPayload: true);

        var exception = await Assert.ThrowsAsync<GitCredentialResolutionException>(() =>
            fixture.Resolver.ResolveAsync(fixture.Repository("connection-1", legacyPassword: "legacy-secret")));

        Assert.Equal(GitCredentialErrorCodes.SecretUnreadable, exception.ErrorCode);
        AssertNoSecretLeak(fixture, exception);
    }

    [Fact]
    public async Task ResolveAsync_WhenFailurePathsRun_NeverLogsOrThrowsSecretValues()
    {
        var disabled = await CreateFixtureAsync(isEnabled: false);
        var deleted = await CreateFixtureAsync(isDeleted: true);
        var corrupt = await CreateFixtureAsync(corruptPayload: true);

        foreach (var fixture in new[] { disabled, deleted, corrupt })
        {
            var exception = await Assert.ThrowsAsync<GitCredentialResolutionException>(() =>
                fixture.Resolver.ResolveAsync(fixture.Repository("connection-1")));
            AssertNoSecretLeak(fixture, exception);
        }
    }

    [Fact]
    public async Task ResolveAsync_WhenRepositoryHasNoConnection_UsesLegacyFields()
    {
        var fixture = await CreateFixtureAsync();

        var credential = await fixture.Resolver.ResolveAsync(
            fixture.Repository(connectionId: null, legacyAccount: "legacy-user", legacyPassword: "legacy-secret"));

        Assert.NotNull(credential);
        Assert.Equal("legacy-user", credential.Username);
        Assert.Equal("legacy-secret", credential.Password);
    }

    [Fact]
    public async Task ResolveAsync_WhenRepositoryHasNeitherConnectionNorLegacyFields_ReturnsNull()
    {
        var fixture = await CreateFixtureAsync();

        Assert.Null(await fixture.Resolver.ResolveAsync(fixture.Repository(connectionId: null)));
    }

    [Fact]
    public async Task ResolveAsync_WhenTheLegacyPathIsUsed_LogsASafeDiagnosticWithTheRepositoryIdOnly()
    {
        var fixture = await CreateFixtureAsync();

        await fixture.Resolver.ResolveAsync(
            fixture.Repository(connectionId: null, legacyAccount: "legacy-user", legacyPassword: "legacy-secret"));

        var entry = Assert.Single(fixture.Logger.Entries, line => line.Contains("Legacy repository credential"));
        Assert.Contains("repo-1", entry);
        Assert.DoesNotContain("legacy-secret", entry);
        Assert.DoesNotContain("legacy-user", entry);
    }

    [Fact]
    public async Task ResolveAsync_WhenTheConnectionPathIsUsed_DoesNotLogTheLegacyDiagnostic()
    {
        var fixture = await CreateFixtureAsync();

        await fixture.Resolver.ResolveAsync(fixture.Repository("connection-1", legacyPassword: "legacy-secret"));

        Assert.DoesNotContain(fixture.Logger.Entries, line => line.Contains("Legacy repository credential"));
    }

    [Fact]
    public async Task HasUsableCredentialAsync_UsesTheConnectionWhenThereIsOneAndTheLegacyFieldsOnlyWhenThereIsNone()
    {
        var enabled = await CreateFixtureAsync();
        var disabled = await CreateFixtureAsync(isEnabled: false);
        var deleted = await CreateFixtureAsync(isDeleted: true);
        var corrupt = await CreateFixtureAsync(corruptPayload: true);

        Assert.True(await enabled.Resolver.HasUsableCredentialAsync(enabled.Repository("connection-1")));
        Assert.False(await enabled.Resolver.HasUsableCredentialAsync(enabled.Repository("missing", legacyPassword: "legacy-secret")));
        Assert.False(await disabled.Resolver.HasUsableCredentialAsync(disabled.Repository("connection-1", legacyPassword: "legacy-secret")));
        Assert.False(await deleted.Resolver.HasUsableCredentialAsync(deleted.Repository("connection-1", legacyPassword: "legacy-secret")));
        Assert.False(await corrupt.Resolver.HasUsableCredentialAsync(corrupt.Repository("connection-1", legacyPassword: "legacy-secret")));
        Assert.True(await enabled.Resolver.HasUsableCredentialAsync(enabled.Repository(null, legacyPassword: "legacy-secret")));
        Assert.False(await enabled.Resolver.HasUsableCredentialAsync(enabled.Repository(null)));
        Assert.False(await enabled.Resolver.HasUsableCredentialAsync(enabled.Repository(null, legacyAccount: "only-an-account", legacyPassword: "  ")));
    }

    [Fact]
    public async Task HasUsableCredentialAsync_NeverLogsOrThrowsSecretValues()
    {
        var corrupt = await CreateFixtureAsync(corruptPayload: true);

        await corrupt.Resolver.HasUsableCredentialAsync(corrupt.Repository("connection-1", legacyPassword: "legacy-secret"));

        AssertNoSecretLeak(corrupt, new InvalidOperationException());
    }

    [Theory]
    [InlineData("connection-1", null, true)]
    [InlineData(null, "legacy-secret", true)]
    [InlineData(null, "   ", false)]
    [InlineData(null, null, false)]
    public async Task HasStoredCredential_ReportsAConnectionOrALegacyPasswordWithoutReadingAnySecret(
        string? connectionId, string? legacyPassword, bool expected)
    {
        var fixture = await CreateFixtureAsync();

        Assert.Equal(expected, GitCredentialResolver.HasStoredCredential(fixture.Repository(connectionId, legacyPassword: legacyPassword)));
    }

    [Fact]
    public void GitCredential_ToString_DoesNotExposeThePassword()
    {
        var credential = new GitCredential("octocat", CanaryPat);

        Assert.DoesNotContain(CanaryPat, credential.ToString());
    }

    private static void AssertNoSecretLeak(Fixture fixture, Exception exception)
    {
        Assert.DoesNotContain(CanaryPat, exception.ToString());
        Assert.DoesNotContain("legacy-secret", exception.ToString());
        Assert.DoesNotContain(fixture.ProtectedToken, exception.ToString());
        foreach (var entry in fixture.Logger.Entries)
        {
            Assert.DoesNotContain(CanaryPat, entry);
            Assert.DoesNotContain("legacy-secret", entry);
            Assert.DoesNotContain(fixture.ProtectedToken, entry);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(
        bool isEnabled = true,
        bool isDeleted = false,
        bool corruptPayload = false,
        string? accountName = "octocat")
    {
        var context = TestDbContext.Create();
        var protector = new DataProtectionGitConnectionSecretProtector(new EphemeralDataProtectionProvider());
        var protectedToken = corruptPayload ? "corrupt-payload-value" : protector.Protect(CanaryPat);
        var connection = new GitConnection
        {
            Id = "connection-1",
            Provider = GitProvider.GitHub,
            NormalizedServerUrl = "https://github.com",
            ExternalAccountId = "1001",
            AccountName = accountName,
            ProtectedToken = protectedToken,
            CreatedByUserId = "creator",
            IsEnabled = isEnabled
        };
        if (isDeleted)
        {
            connection.MarkAsDeleted();
        }

        context.GitConnections.Add(connection);
        await context.SaveChangesAsync();

        var logger = new CapturingLogger<GitCredentialResolver>();
        return new Fixture(new GitCredentialResolver(context, protector, logger), logger, protectedToken);
    }

    private sealed record Fixture(
        GitCredentialResolver Resolver,
        CapturingLogger<GitCredentialResolver> Logger,
        string ProtectedToken)
    {
        public Repository Repository(
            string? connectionId,
            string? legacyAccount = null,
            string? legacyPassword = null) => new()
        {
            Id = "repo-1",
            OwnerUserId = "creator",
            GitUrl = "https://github.com/acme/widgets",
            OrgName = "acme",
            RepoName = "widgets",
            GitConnectionId = connectionId,
            AuthAccount = legacyAccount,
            AuthPassword = legacyPassword
        };
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add($"{formatter(state, exception)} {exception}");
    }
}
