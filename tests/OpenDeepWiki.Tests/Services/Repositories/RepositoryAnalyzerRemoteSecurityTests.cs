using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Repositories;
using OpenDeepWiki.Tests.Services.GitConnections;
using Xunit;
using DomainRepository = OpenDeepWiki.Entities.Repository;
using GitRepository = LibGit2Sharp.Repository;

namespace OpenDeepWiki.Tests.Services.Repositories;

/// <summary>
/// Clone, fetch, and remote reference lookups must keep normal TLS validation, send a connection credential only
/// to the connection's own origin, and apply the address policy before any credential is read.
/// </summary>
public sealed class RepositoryAnalyzerRemoteSecurityTests : IDisposable
{
    private const string Pat = "ghp_CANARY_remote_security_0123456789";

    private readonly string _root = Path.Combine(TestPaths.ScratchRoot, $"analyzer-remote-{Guid.NewGuid():N}");

    public RepositoryAnalyzerRemoteSecurityTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- TLS ----

    [Fact]
    public void CloneOptions_DoNotInstallACertificateBypass()
    {
        var options = RepositoryAnalyzer.CreateCloneOptions("main", credentialsHandler: null);

        Assert.Null(options.FetchOptions.CertificateCheck);
        Assert.Equal("main", options.BranchName);
    }

    [Fact]
    public void FetchOptions_DoNotInstallACertificateBypass()
    {
        var options = RepositoryAnalyzer.CreateFetchOptions(credentialsHandler: null);

        Assert.Null(options.CertificateCheck);
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_WhenTheServerCertificateIsNotTrusted_NeverSendsARequest()
    {
        await using var server = SelfSignedHttpsServer.Start();
        var analyzer = CreateAnalyzer(new RecordingResolver());
        var repository = LegacyRepository(server.Url("acme/widgets.git"));

        await Assert.ThrowsAnyAsync<Exception>(() => analyzer.PrepareWorkspaceAsync(repository, "main"));

        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_WhenAFetchSeesAnUntrustedCertificate_NeverSendsARequest()
    {
        await using var server = SelfSignedHttpsServer.Start();
        var analyzer = CreateAnalyzer(new RecordingResolver());
        var repository = LegacyRepository(server.Url("acme/widgets.git"));
        var workspace = Path.Combine(_root, "acme", "widgets", "branches", "main", "tree");
        Directory.CreateDirectory(workspace);
        GitRepository.Init(workspace);
        using (var existing = new GitRepository(workspace))
        {
            existing.Network.Remotes.Add("origin", repository.GitUrl);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => analyzer.PrepareWorkspaceAsync(repository, "main"));

        Assert.Equal(0, server.RequestCount);
    }

    // ---- credential origin ----

    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "https://github.com", true)]
    [InlineData("https://GitHub.com/acme/widgets.git", "https://github.com", true)]
    [InlineData("https://git.corp.example:8443/g/p.git", "https://git.corp.example:8443", true)]
    [InlineData("https://github.com.evil.example/acme/widgets.git", "https://github.com", false)]
    [InlineData("https://evil.example/github.com/acme.git", "https://github.com", false)]
    [InlineData("http://github.com/acme/widgets.git", "https://github.com", false)]
    [InlineData("https://github.com:8443/acme/widgets.git", "https://github.com", false)]
    [InlineData("https://user@github.com/acme/widgets.git", "https://github.com", false)]
    [InlineData("ssh://github.com/acme/widgets.git", "https://github.com", false)]
    [InlineData("not a url", "https://github.com", false)]
    public void IsSameOrigin_ComparesSchemeHostAndPort(string url, string origin, bool expected)
    {
        Assert.Equal(expected, GitRemoteOriginGuard.IsSameOrigin(url, origin));
    }

    [Fact]
    public void OriginBoundCredentials_AreHandedOnlyToTheExpectedOrigin()
    {
        var credentials = new UsernamePasswordCredentials { Username = "octocat", Password = Pat };
        var handler = RepositoryAnalyzer.CreateOriginBoundCredentialsHandler(credentials, "https://github.com");

        var allowed = handler("https://github.com/acme/widgets.git", null, SupportedCredentialTypes.UsernamePassword);
        var exception = Assert.Throws<GitRemoteOriginException>(
            () => handler("https://evil.example/acme/widgets.git", null, SupportedCredentialTypes.UsernamePassword));

        Assert.Same(credentials, allowed);
        Assert.DoesNotContain(Pat, exception.Message);
        Assert.Equal(GitRemoteOriginException.OriginMismatch, exception.ErrorCode);
    }

    [Fact]
    public async Task ListRemoteReferences_WhenTheServerRedirectsToAnotherHost_NeverSendsTheCredentialThere()
    {
        await using var target = HttpChallengeServer.StartChallenging();
        await using var redirecting = HttpChallengeServer.StartRedirectingTo(target.Url("acme/widgets.git/info/refs?service=git-upload-pack"));
        var credentials = new UsernamePasswordCredentials { Username = "octocat", Password = Pat };
        var handler = RepositoryAnalyzer.CreateOriginBoundCredentialsHandler(credentials, redirecting.Origin);

        await Assert.ThrowsAnyAsync<Exception>(() => Task.Run(
            () => GitRepository.ListRemoteReferences(redirecting.Url("acme/widgets.git"), (url, user, types) => handler(url, user, types)).ToList()));

        Assert.True(target.RequestCount > 0, "The redirect was not followed, so the test proves nothing.");
        Assert.Empty(target.AuthorizationHeaders);
    }

    // ---- pre-flight ----

    [Fact]
    public async Task PrepareWorkspaceAsync_WhenTheRemoteIsNotOnTheConnectionOrigin_FailsBeforeReadingTheCredential()
    {
        var resolver = new RecordingResolver { Credential = new GitCredential("octocat", Pat) };
        var analyzer = CreateAnalyzer(resolver);
        var repository = ConnectedRepository("https://evil.example/acme/widgets.git", "https://github.com");

        var exception = await Assert.ThrowsAsync<GitRemoteOriginException>(() => analyzer.PrepareWorkspaceAsync(repository, "main"));

        Assert.Equal(GitRemoteOriginException.OriginMismatch, exception.ErrorCode);
        Assert.Equal(0, resolver.Calls);
        Assert.DoesNotContain(Pat, exception.Message);
    }

    [Fact]
    public async Task GetRemoteBranchHeadCommitAsync_WhenTheRemoteIsNotOnTheConnectionOrigin_FailsBeforeReadingTheCredential()
    {
        var resolver = new RecordingResolver { Credential = new GitCredential("octocat", Pat) };
        var analyzer = CreateAnalyzer(resolver);
        var repository = ConnectedRepository("https://evil.example/acme/widgets.git", "https://github.com");

        await Assert.ThrowsAsync<GitRemoteOriginException>(() => analyzer.GetRemoteBranchHeadCommitAsync(repository, "main"));

        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_WhenTheConnectionHasNoRecordedOrigin_FailsClosed()
    {
        var resolver = new RecordingResolver { Credential = new GitCredential("octocat", Pat) };
        var repository = ConnectedRepository("https://github.com/acme/widgets.git", providerBaseUrl: null);

        await Assert.ThrowsAsync<GitRemoteOriginException>(() => CreateAnalyzer(resolver).PrepareWorkspaceAsync(repository, "main"));

        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_WhenTheHostResolvesToAPrivateAddress_FailsBeforeReadingTheCredential()
    {
        var resolver = new RecordingResolver { Credential = new GitCredential("octocat", Pat) };
        var hosts = new FakeHostResolver().Add("git.corp.example", "10.0.0.7");
        var analyzer = CreateAnalyzer(resolver, GitProviderTestFactory.CreateValidator(hosts));
        var repository = ConnectedRepository("https://git.corp.example/acme/widgets.git", "https://git.corp.example");

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => analyzer.PrepareWorkspaceAsync(repository, "main"));

        Assert.Equal(GitProviderErrorCodes.ServerUrlBlocked, exception.Code);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_WhenThePrivateHostIsAllowlisted_ReadsTheCredentialAndGoesOn()
    {
        var resolver = new RecordingResolver { Credential = new GitCredential("octocat", Pat) };
        var hosts = new FakeHostResolver().Add("git.corp.example", "10.0.0.7");
        var analyzer = CreateAnalyzer(resolver, GitProviderTestFactory.CreateValidator(hosts, allowedHosts: ["git.corp.example"]));
        var repository = ConnectedRepository("https://git.corp.example:1/acme/widgets.git", "https://git.corp.example:1");

        // The server address is unreachable, so the clone fails after the checks passed.
        await Assert.ThrowsAnyAsync<Exception>(() => analyzer.PrepareWorkspaceAsync(repository, "main"));

        Assert.Equal(1, resolver.Calls);
    }

    [Fact]
    public async Task PrepareWorkspaceAsync_WhenAnExistingWorkspacePointsAtAnotherRemote_ClonesAgainInsteadOfFetching()
    {
        var resolver = new RecordingResolver { Credential = new GitCredential("octocat", Pat) };
        var hosts = new FakeHostResolver().Add("git.corp.example", "203.0.113.9");
        var analyzer = CreateAnalyzer(resolver, GitProviderTestFactory.CreateValidator(hosts));
        var repository = ConnectedRepository("https://git.corp.example:1/acme/widgets.git", "https://git.corp.example:1");
        var workspace = Path.Combine(_root, "acme", "widgets", "branches", "main", "tree");
        Directory.CreateDirectory(workspace);
        GitRepository.Init(workspace);
        using (var existing = new GitRepository(workspace))
        {
            existing.Network.Remotes.Add("origin", "https://other-project.example/acme/other.git");
        }

        File.WriteAllText(Path.Combine(workspace, "marker.txt"), "old checkout");

        await Assert.ThrowsAnyAsync<Exception>(() => analyzer.PrepareWorkspaceAsync(repository, "main"));

        Assert.False(File.Exists(Path.Combine(workspace, "marker.txt")), "The checkout of another remote was reused.");
    }

    // ---- deleted files ----

    [Fact]
    public async Task GetDeletedFilesAsync_ReportsDeletedFilesAndTheOldPathOfRenamedFiles()
    {
        var workspace = Path.Combine(_root, "history");
        Directory.CreateDirectory(workspace);
        GitRepository.Init(workspace);
        using var repo = new GitRepository(workspace);
        var signature = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(workspace, "kept.txt"), "kept content that stays the same\n");
        File.WriteAllText(Path.Combine(workspace, "deleted.txt"), "this file disappears in the next commit\n");
        File.WriteAllText(Path.Combine(workspace, "old-name.txt"), "a file that is renamed without changing its text at all\n");
        Commands.Stage(repo, "*");
        var first = repo.Commit("first", signature, signature);

        File.Delete(Path.Combine(workspace, "deleted.txt"));
        File.Move(Path.Combine(workspace, "old-name.txt"), Path.Combine(workspace, "new-name.txt"));
        File.WriteAllText(Path.Combine(workspace, "added.txt"), "a new file\n");
        Commands.Stage(repo, "*");
        var second = repo.Commit("second", signature, signature);

        var analyzer = CreateAnalyzer(new RecordingResolver());
        var deleted = await analyzer.GetDeletedFilesAsync(
            new RepositoryWorkspace { WorkingDirectory = workspace, SupportsIncrementalUpdates = true },
            first.Sha,
            second.Sha);

        Assert.Equal(["deleted.txt", "old-name.txt"], deleted.Order().ToArray());
    }

    [Fact]
    public async Task GetDeletedFilesAsync_WithoutAPreviousCommitOrIncrementalSupport_ReportsNothing()
    {
        var analyzer = CreateAnalyzer(new RecordingResolver());

        var withoutPrevious = await analyzer.GetDeletedFilesAsync(
            new RepositoryWorkspace { WorkingDirectory = _root, SupportsIncrementalUpdates = true }, null, "abc");
        var unsupported = await analyzer.GetDeletedFilesAsync(
            new RepositoryWorkspace { WorkingDirectory = _root, SupportsIncrementalUpdates = false }, "abc", "def");

        Assert.Empty(withoutPrevious);
        Assert.Empty(unsupported);
    }

    // ---- helpers ----

    private RepositoryAnalyzer CreateAnalyzer(IGitCredentialResolver resolver, GitLabServerUrlValidator? validator = null)
        => new(
            Options.Create(new RepositoryAnalyzerOptions
            {
                RepositoriesDirectory = _root,
                MaxRetryAttempts = 1,
                RetryDelayMs = 1
            }),
            NullLogger<RepositoryAnalyzer>.Instance,
            resolver,
            validator ?? GitProviderTestFactory.CreateValidator(new FakeHostResolver().Add("github.com", "140.82.112.3")));

    private static DomainRepository LegacyRepository(string gitUrl) => new()
    {
        Id = "repo-1",
        OwnerUserId = "user-1",
        GitUrl = gitUrl,
        OrgName = "acme",
        RepoName = "widgets"
    };

    private static DomainRepository ConnectedRepository(string gitUrl, string? providerBaseUrl)
    {
        var repository = LegacyRepository(gitUrl);
        repository.GitConnectionId = "connection-1";
        repository.Provider = GitProvider.GitHub;
        repository.ProviderBaseUrl = providerBaseUrl;
        repository.ProviderRepositoryId = "42";
        return repository;
    }

    private sealed class RecordingResolver : IGitCredentialResolver
    {
        public int Calls { get; private set; }
        public GitCredential? Credential { get; init; }

        public Task<GitCredential?> ResolveAsync(DomainRepository repository, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Credential);
        }

        public Task<bool> HasUsableCredentialAsync(DomainRepository repository, CancellationToken cancellationToken = default)
            => Task.FromResult(Credential is not null);
    }

    /// <summary>
    /// TLS server with a self-signed certificate. A client that validates certificates stops during the handshake
    /// and never sends an HTTP request; a client that skips validation sends one.
    /// </summary>
    private sealed class SelfSignedHttpsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _stop = new();
        private int _requestCount;

        private SelfSignedHttpsServer()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public static SelfSignedHttpsServer Start()
        {
            var server = new SelfSignedHttpsServer();
            server._listener.Start();
            _ = server.AcceptLoopAsync();
            return server;
        }

        public string Url(string path) => $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{path}";

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsServerAsync(_certificate);
                    var buffer = new byte[4096];
                    var read = await ssl.ReadAsync(buffer);
                    if (read > 0)
                    {
                        Interlocked.Increment(ref _requestCount);
                        await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                    }
                }
                catch
                {
                    // A failed handshake is the expected outcome for a client that validates certificates.
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            _certificate.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Plain HTTP server on loopback that either redirects every request or answers 401 with a Basic challenge,
    /// and records every Authorization header it receives.
    /// </summary>
    private sealed class HttpChallengeServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string? _redirectTo;
        private int _requestCount;

        private HttpChallengeServer(string? redirectTo)
        {
            _redirectTo = redirectTo;
            var port = ((IPEndPoint)FindFreePort()).Port;
            Origin = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Origin + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Origin { get; }

        public List<string> AuthorizationHeaders { get; } = [];

        public int RequestCount => Volatile.Read(ref _requestCount);

        public static HttpChallengeServer StartChallenging() => new(null);

        public static HttpChallengeServer StartRedirectingTo(string target) => new(target);

        public string Url(string path) => $"{Origin}/{path}";

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch
                {
                    return;
                }

                Interlocked.Increment(ref _requestCount);
                var authorization = context.Request.Headers["Authorization"];
                if (authorization is not null)
                {
                    lock (AuthorizationHeaders)
                    {
                        AuthorizationHeaders.Add(authorization);
                    }
                }

                if (_redirectTo is not null)
                {
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = _redirectTo;
                }
                else
                {
                    context.Response.StatusCode = 401;
                    context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"git\"");
                }

                context.Response.Close();
            }
        }

        private static EndPoint FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var endpoint = probe.LocalEndpoint;
            probe.Stop();
            return endpoint;
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }
}
