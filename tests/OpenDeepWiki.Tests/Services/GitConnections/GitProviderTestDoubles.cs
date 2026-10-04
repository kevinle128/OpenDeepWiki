using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using OpenDeepWiki.Services.GitConnections;

namespace OpenDeepWiki.Tests.Services.GitConnections;

/// <summary>
/// Resolver that returns configured addresses and counts the calls. No test uses real DNS.
/// </summary>
internal sealed class FakeHostResolver : IGitHostResolver
{
    private readonly Dictionary<string, Queue<IPAddress[]>> _answers = new(StringComparer.OrdinalIgnoreCase);

    public int CallCount { get; private set; }

    public FakeHostResolver Add(string host, params string[] addresses)
    {
        if (!_answers.TryGetValue(host, out var queue))
        {
            _answers[host] = queue = new Queue<IPAddress[]>();
        }

        queue.Enqueue(addresses.Select(IPAddress.Parse).ToArray());
        return this;
    }

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        CallCount++;
        if (!_answers.TryGetValue(host, out var queue))
        {
            throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
        }

        // The last answer repeats, so one registration serves any number of requests.
        var answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        return Task.FromResult<IReadOnlyList<IPAddress>>(answer);
    }
}

internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    string? Authorization,
    string? PrivateToken,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<IPAddress>? PinnedAddresses);

/// <summary>
/// Fake provider: returns queued responses in order and records what the client sent.
/// </summary>
internal sealed class FakeProviderHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<CapturedRequest> Requests { get; } = [];

    public FakeProviderHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public FakeProviderHandler EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK, string? link = null)
        => Enqueue(_ => Json(json, status, link));

    public FakeProviderHandler EnqueueStatus(HttpStatusCode status, params (string Name, string Value)[] headers)
        => Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("{\"message\":\"RAW_PROVIDER_BODY\"}") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });

    public FakeProviderHandler EnqueueThrow(Exception exception)
        => Enqueue(_ => throw exception);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, string? link = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (link is not null)
        {
            response.Headers.TryAddWithoutValidation("Link", link);
        }

        return response;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Options.TryGetValue(GitProviderGuardHandler.PinnedAddressesKey, out var pinned);
        Requests.Add(new CapturedRequest(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("PRIVATE-TOKEN", out var token) ? token.Single() : null,
            request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
            pinned));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}

internal sealed class FakeHttpClientFactory(Func<string, HttpClient> create) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => create(name);
}

internal static class GitProviderTestFactory
{
    public static GitLabServerUrlValidator CreateValidator(
        FakeHostResolver resolver,
        IEnumerable<string>? allowedHosts = null,
        IEnumerable<string>? allowedCidrs = null)
        => new(
            Options.Create(new GitProviderOptions
            {
                AllowedPrivateHosts = allowedHosts?.ToList() ?? [],
                AllowedPrivateCidrs = allowedCidrs?.ToList() ?? []
            }),
            resolver);

    /// <summary>
    /// GitLab client path: the fake provider sits under the same guard handler that production uses.
    /// </summary>
    public static HttpClient CreateGuardedClient(HttpMessageHandler fake, GitLabServerUrlValidator validator)
        => new(new GitProviderGuardHandler(validator) { InnerHandler = fake });
}

/// <summary>
/// Collects log lines so tests can assert that no secret reaches the logs.
/// </summary>
internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Lines.Add(formatter(state, exception));
        if (exception is not null)
        {
            Lines.Add(exception.ToString());
        }
    }
}

internal sealed class TestUserContext(string? userId, bool isAuthenticated = true) : OpenDeepWiki.Services.Auth.IUserContext
{
    public string? UserId { get; } = userId;
    public string? UserName => UserId;
    public string? Email => null;
    public bool IsAuthenticated { get; } = isAuthenticated;

    public System.Security.Claims.ClaimsPrincipal? User { get; } = isAuthenticated && userId is not null
        ? new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId)], "test"))
        : new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
}

/// <summary>
/// Provider client double. Identity is looked up by token, so two tokens can resolve to one account.
/// </summary>
internal sealed class FakeProviderClient(OpenDeepWiki.Entities.GitProvider provider) : IGitProviderClient
{
    public OpenDeepWiki.Entities.GitProvider Provider { get; } = provider;

    public Dictionary<string, GitProviderIdentity> IdentityByToken { get; } = new();

    public Exception? ValidateFailure { get; set; }

    /// <summary>
    /// When set, validation waits for this task. Tests use it to interleave two writers.
    /// </summary>
    public Func<Task>? ValidateGate { get; set; }

    public List<(string? ConnectionId, string ServerUrl, string Token)> ValidateCalls { get; } = [];

    public List<(string ConnectionId, string Token, string? Cursor, int PageSize, string? RepositoryId)> CatalogCalls { get; } = [];

    public Exception? CatalogFailure { get; set; }

    public Func<string?, ProviderPage<RemoteRepository>>? RepositoryPages { get; set; }

    public async Task<GitProviderIdentity> ValidateAsync(GitProviderTarget target, CancellationToken cancellationToken)
    {
        ValidateCalls.Add((target.ConnectionId, target.ServerUrl, target.Token));
        if (ValidateGate is not null)
        {
            await ValidateGate();
        }

        if (ValidateFailure is not null)
        {
            throw ValidateFailure;
        }

        return IdentityByToken.TryGetValue(target.Token, out var identity)
            ? identity
            : throw new GitProviderException(GitProviderErrorCodes.Unauthorized);
    }

    public Task<RemoteRepository> GetRepositoryAsync(GitProviderTarget target, string providerRepositoryId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
        GitProviderTarget target, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        CatalogCalls.Add((target.ConnectionId!, target.Token, cursor, pageSize, null));
        if (CatalogFailure is not null)
        {
            throw CatalogFailure;
        }

        if (RepositoryPages is not null)
            return Task.FromResult(RepositoryPages(cursor));

        return Task.FromResult(new ProviderPage<RemoteRepository>(
            [new RemoteRepository("42", "widgets", "acme/widgets", "acme", null, "https://github.com/acme/widgets.git", null, "main", "Private", null)],
            "next-cursor"));
    }

    public Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
        GitProviderTarget target, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        CatalogCalls.Add((target.ConnectionId!, target.Token, cursor, pageSize, providerRepositoryId));
        return Task.FromResult(new ProviderPage<RemoteBranch>([new RemoteBranch("main", true, "abc")], null));
    }
}
