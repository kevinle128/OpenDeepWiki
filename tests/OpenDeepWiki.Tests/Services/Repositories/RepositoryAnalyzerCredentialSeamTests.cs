using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Tests.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Repositories;

public sealed class RepositoryAnalyzerCredentialSeamTests : IDisposable
{
    private readonly string _root = Path.Combine(TestPaths.ScratchRoot, $"analyzer-seam-{Guid.NewGuid():N}");

    public RepositoryAnalyzerCredentialSeamTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task GetRemoteBranchHeadCommitAsync_WhenResolverFailsClosed_PropagatesWithoutNetworkAccess()
    {
        var resolver = new RecordingResolver { Failure = new GitCredentialResolutionException(GitCredentialErrorCodes.ConnectionDisabled) };
        var analyzer = CreateAnalyzer(resolver);

        var exception = await Assert.ThrowsAsync<GitCredentialResolutionException>(() =>
            analyzer.GetRemoteBranchHeadCommitAsync(CreateRepository(), "main"));

        Assert.Equal(GitCredentialErrorCodes.ConnectionDisabled, exception.ErrorCode);
        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_WhenResolverFailsClosed_PropagatesBeforeCloneOrPull()
    {
        var resolver = new RecordingResolver { Failure = new GitCredentialResolutionException(GitCredentialErrorCodes.SecretUnreadable) };
        var analyzer = CreateAnalyzer(resolver);

        await Assert.ThrowsAsync<GitCredentialResolutionException>(() =>
            analyzer.PrepareWorkspaceAsync(CreateRepository(), "main"));

        Assert.Equal(1, resolver.Calls);
        Assert.False(Directory.Exists(Path.Combine(_root, "acme", "widgets", "main", ".git")));
    }

    [Fact]
    public async Task GetRemoteBranchHeadCommitAsync_ReadsCredentialsThroughTheResolver()
    {
        var resolver = new RecordingResolver { Credential = new GitCredential("octocat", "pat-value") };
        var analyzer = CreateAnalyzer(resolver);
        var repository = CreateRepository();
        repository.AuthPassword = "legacy-field-must-not-be-read-directly";

        // The unreachable loopback port makes the network call fail after the credential was resolved.
        await Assert.ThrowsAnyAsync<Exception>(() => analyzer.GetRemoteBranchHeadCommitAsync(repository, "main"));

        Assert.Equal(1, resolver.Calls);
        Assert.Same(repository, resolver.LastRepository);
    }

    [Fact]
    public async Task GetRemoteBranchHeadCommitAsync_ForLocalSources_DoesNotResolveCredentials()
    {
        var resolver = new RecordingResolver();
        var analyzer = CreateAnalyzer(resolver);
        var repository = CreateRepository();
        repository.GitUrl = RepositorySource.EncodeLocalDirectoryPath(_root);

        await Record.ExceptionAsync(() => analyzer.GetRemoteBranchHeadCommitAsync(repository, "main"));

        Assert.Equal(0, resolver.Calls);
    }

    private RepositoryAnalyzer CreateAnalyzer(IGitCredentialResolver resolver)
        => new(
            Options.Create(new RepositoryAnalyzerOptions
            {
                RepositoriesDirectory = _root,
                AllowedLocalPathRoots = [_root],
                MaxRetryAttempts = 1,
                RetryDelayMs = 1
            }),
            NullLogger<RepositoryAnalyzer>.Instance,
            resolver,
            // The test server address is loopback, which the operator must allow explicitly.
            GitProviderTestFactory.CreateValidator(new FakeHostResolver(), allowedHosts: ["127.0.0.1"]));

    private static Repository CreateRepository() => new()
    {
        Id = "repo-1",
        OwnerUserId = "user-1",
        GitUrl = "https://127.0.0.1:1/acme/widgets.git",
        OrgName = "acme",
        RepoName = "widgets",
        GitConnectionId = "connection-1",
        Provider = GitProvider.GitLab,
        ProviderBaseUrl = "https://127.0.0.1:1",
        ProviderRepositoryId = "42"
    };

    private sealed class RecordingResolver : IGitCredentialResolver
    {
        public int Calls { get; private set; }
        public Repository? LastRepository { get; private set; }
        public GitCredential? Credential { get; init; }
        public Exception? Failure { get; init; }

        public Task<GitCredential?> ResolveAsync(Repository repository, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRepository = repository;
            return Failure is null ? Task.FromResult(Credential) : Task.FromException<GitCredential?>(Failure);
        }

        public Task<bool> HasUsableCredentialAsync(Repository repository, CancellationToken cancellationToken = default)
            => Task.FromResult(Credential is not null);
    }
}
