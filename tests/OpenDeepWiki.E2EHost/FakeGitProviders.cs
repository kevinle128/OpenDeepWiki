using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenDeepWiki.Services.GitConnections;

namespace OpenDeepWiki.E2EHost;

/// <summary>
/// A failure or delay that the fake provider applies to the calls of one account.
/// </summary>
public sealed record ProviderRule(string Account, string Op, int Status, int DelayMs, int RetryAfterSeconds, bool Timeout);

/// <summary>
/// One call that reached the fake provider. Tokens and bodies are never recorded.
/// </summary>
public sealed record ProviderCall(string Host, string Path, string? Account, int Status);

/// <summary>
/// Name resolution without DNS. Public hosts resolve to a public address, private hosts to a 10.x address.
/// The real address policy still decides what a connection may reach.
/// </summary>
internal sealed class FakeHostResolver : IGitHostResolver
{
    private static readonly Dictionary<string, IPAddress> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gitlab.com"] = IPAddress.Parse("93.184.216.10"),
        ["api.github.com"] = IPAddress.Parse("93.184.216.11"),
        ["git.public-selfhosted.test"] = IPAddress.Parse("93.184.216.12"),
        ["gitlab.corp-allowed.test"] = IPAddress.Parse("10.20.30.40"),
        ["gitlab.corp-blocked.test"] = IPAddress.Parse("10.20.30.41"),
    };

    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return Task.FromResult<IReadOnlyList<IPAddress>>([literal]);
        }

        return Hosts.TryGetValue(host, out var address)
            ? Task.FromResult<IReadOnlyList<IPAddress>>([address])
            : throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound);
    }
}

/// <summary>
/// In-process GitHub and GitLab REST fake. It replaces the primary HTTP handler of the two provider clients,
/// so the real clients, cursor codec and address guard run on top of it. A request to an unknown host fails closed.
///
/// A token is accepted when it has the form <c>e2e-{account}-{suffix}</c> and does not contain "revoked".
/// The account decides the identity and the data, so each test can use its own account and the host never
/// has to know or log a token.
/// </summary>
internal sealed partial class FakeGitProviderHandler : HttpMessageHandler
{
    private const int DefaultRepositoryCount = 8;
    private const int LargeRepositoryCount = 120;
    private static readonly DateTime Epoch = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Branches = ["main", "develop", "release/1.0", "feature/hold", "feature/fail"];
    private static readonly HashSet<string> GitLabHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "gitlab.com", "git.public-selfhosted.test", "gitlab.corp-allowed.test", "gitlab.corp-blocked.test"
    };

    private readonly ConcurrentQueue<ProviderCall> _calls = new();
    private readonly List<ProviderRule> _rules = [];
    private readonly object _ruleLock = new();
    private int _unfaked;

    public IReadOnlyList<ProviderCall> Calls => _calls.ToArray();

    public int UnfakedCalls => Volatile.Read(ref _unfaked);

    public void SetRules(IEnumerable<ProviderRule> rules)
    {
        lock (_ruleLock)
        {
            _rules.Clear();
            _rules.AddRange(rules);
        }
    }

    private ProviderRule? FindRule(string account, string op)
    {
        lock (_ruleLock)
        {
            return _rules.FirstOrDefault(rule =>
                string.Equals(rule.Account, account, StringComparison.OrdinalIgnoreCase)
                && (rule.Op == "*" || rule.Op == op));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new HttpRequestException("Missing URI.");
        var host = uri.IdnHost;
        var isGitHub = string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase);
        var isGitLab = GitLabHosts.Contains(host) && uri.AbsolutePath.StartsWith("/api/v4/", StringComparison.Ordinal);
        if (!isGitHub && !isGitLab)
        {
            Interlocked.Increment(ref _unfaked);
            _calls.Enqueue(new ProviderCall(host, uri.AbsolutePath, null, -1));
            throw new HttpRequestException("The fake provider does not serve this host.");
        }

        var token = isGitHub
            ? request.Headers.Authorization?.Parameter
            : request.Headers.TryGetValues("PRIVATE-TOKEN", out var values) ? values.FirstOrDefault() : null;
        var account = ReadAccount(token);
        var path = isGitHub ? uri.AbsolutePath : uri.AbsolutePath["/api/v4".Length..];
        var op = path switch
        {
            "/user" => "user",
            "/user/repos" or "/projects" => "repos",
            _ when path.EndsWith("/branches", StringComparison.Ordinal) => "branches",
            _ => "repo"
        };

        HttpResponseMessage response;
        try
        {
            response = account is null
                ? Status(HttpStatusCode.Unauthorized)
                : await ApplyRuleOrServeAsync(request, uri, isGitHub, host, account, op, path, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _calls.Enqueue(new ProviderCall(host, uri.AbsolutePath, account, 0));
            throw;
        }

        _calls.Enqueue(new ProviderCall(host, uri.AbsolutePath, account, (int)response.StatusCode));
        return response;
    }

    private async Task<HttpResponseMessage> ApplyRuleOrServeAsync(
        HttpRequestMessage request, Uri uri, bool isGitHub, string host, string account, string op, string path,
        CancellationToken cancellationToken)
    {
        var rule = FindRule(account, op);
        if (rule is not null)
        {
            if (rule.DelayMs > 0)
            {
                await Task.Delay(rule.DelayMs, cancellationToken);
            }

            if (rule.Timeout)
            {
                throw new TaskCanceledException("Simulated provider timeout.");
            }

            if (rule.Status != 0)
            {
                var failure = Status((HttpStatusCode)rule.Status);
                if (rule.RetryAfterSeconds > 0)
                {
                    failure.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(rule.RetryAfterSeconds));
                }

                return failure;
            }
        }

        return isGitHub ? ServeGitHub(uri, account, path) : ServeGitLab(uri, host, account, path);
    }

    private static HttpResponseMessage ServeGitHub(Uri uri, string account, string path)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var perPage = ReadInt(query["per_page"], 30, 1, 100);
        var page = ReadInt(query["page"], 1, 1, 10_000);
        var index = AccountIndex(account);

        switch (path)
        {
            case "/user":
                return Json(new { id = AccountId(index), login = account });
            case "/user/repos":
            {
                var all = RepositoryNumbers(account);
                var slice = all.Skip((page - 1) * perPage).Take(perPage).Select(number => GitHubRepository(account, index, number)).ToList();
                var response = Json(slice);
                if (page * perPage < all.Count)
                {
                    response.Headers.TryAddWithoutValidation(
                        "Link", $"<https://api.github.com/user/repos?page={page + 1}&per_page={perPage}>; rel=\"next\"");
                }

                return response;
            }
        }

        var match = RepositoryRoute().Match(path);
        if (!match.Success || !OwnsRepository(account, index, long.Parse(match.Groups["id"].Value)))
        {
            return Status(HttpStatusCode.NotFound);
        }

        var number = RepositoryNumber(long.Parse(match.Groups["id"].Value));
        if (match.Groups["rest"].Value == string.Empty)
        {
            return Json(GitHubRepository(account, index, number));
        }

        var branches = Branches.Skip((page - 1) * perPage).Take(perPage)
            .Select(name => new { name, commit = new { sha = Sha(account, number, name) } }).ToList();
        var branchResponse = Json(branches);
        if (page * perPage < Branches.Length)
        {
            branchResponse.Headers.TryAddWithoutValidation(
                "Link", $"<https://api.github.com{path}?page={page + 1}&per_page={perPage}>; rel=\"next\"");
        }

        return branchResponse;
    }

    private static HttpResponseMessage ServeGitLab(Uri uri, string host, string account, string path)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var perPage = ReadInt(query["per_page"], 20, 1, 100);
        var page = ReadInt(query["page"], 1, 1, 10_000);
        var index = AccountIndex(account);

        switch (path)
        {
            case "/user":
                return Json(new { id = AccountId(index), username = account });
            case "/projects":
            {
                // Older servers reject keyset paging, so the client has to fall back to offset paging.
                if (query["pagination"] == "keyset")
                {
                    return Status(HttpStatusCode.BadRequest);
                }

                var all = RepositoryNumbers(account);
                var slice = all.Skip((page - 1) * perPage).Take(perPage).Select(number => GitLabProject(host, account, index, number)).ToList();
                var response = Json(slice);
                response.Headers.TryAddWithoutValidation("X-Total", all.Count.ToString());
                if (page * perPage < all.Count)
                {
                    response.Headers.TryAddWithoutValidation("X-Next-Page", (page + 1).ToString());
                }

                return response;
            }
        }

        var match = ProjectRoute().Match(path);
        if (!match.Success || !OwnsRepository(account, index, long.Parse(match.Groups["id"].Value)))
        {
            return Status(HttpStatusCode.NotFound);
        }

        var number = RepositoryNumber(long.Parse(match.Groups["id"].Value));
        if (match.Groups["rest"].Value == string.Empty)
        {
            return Json(GitLabProject(host, account, index, number));
        }

        var branches = Branches.Skip((page - 1) * perPage).Take(perPage)
            .Select(name => new { name, @default = name == "main", commit = new { id = Sha(account, number, name) } }).ToList();
        var branchResponse = Json(branches);
        branchResponse.Headers.TryAddWithoutValidation("X-Total", Branches.Length.ToString());
        if (page * perPage < Branches.Length)
        {
            branchResponse.Headers.TryAddWithoutValidation("X-Next-Page", (page + 1).ToString());
        }

        return branchResponse;
    }

    private static object GitHubRepository(string account, int index, int number) => new
    {
        id = RepositoryId(index, number),
        name = RepositoryName(number),
        full_name = $"{account}/{RepositoryName(number)}",
        owner = new { login = account },
        description = $"Fake repository {number} of {account}",
        clone_url = $"https://github.com/{account}/{RepositoryName(number)}.git",
        html_url = $"https://github.com/{account}/{RepositoryName(number)}",
        default_branch = "main",
        visibility = number % 2 == 0 ? "private" : "public",
        @private = number % 2 == 0,
        updated_at = Epoch.AddDays(-number).ToString("o")
    };

    private static object GitLabProject(string host, string account, int index, int number) => new
    {
        id = RepositoryId(index, number),
        name = RepositoryName(number),
        path_with_namespace = $"{account}/{RepositoryName(number)}",
        @namespace = new { full_path = account },
        description = $"Fake project {number} of {account}",
        http_url_to_repo = $"https://{host}/{account}/{RepositoryName(number)}.git",
        web_url = $"https://{host}/{account}/{RepositoryName(number)}",
        default_branch = "main",
        visibility = number % 2 == 0 ? "private" : "public",
        last_activity_at = Epoch.AddDays(-number).ToString("o")
    };

    private static string? ReadAccount(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Contains("revoked", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = TokenShape().Match(token);
        return match.Success ? match.Groups["account"].Value : null;
    }

    private static List<int> RepositoryNumbers(string account)
        => Enumerable.Range(1, account.StartsWith("many", StringComparison.OrdinalIgnoreCase) ? LargeRepositoryCount : DefaultRepositoryCount).ToList();

    private static int AccountIndex(string account)
    {
        // A stable index that does not depend on the process, so ids stay the same across restarts.
        var hash = 17;
        foreach (var character in account)
        {
            hash = unchecked(hash * 31 + character);
        }

        return 100 + (int)((uint)hash % 800);
    }

    private static long AccountId(int index) => 7_000_000L + index;

    private static long RepositoryId(int index, int number) => index * 1000L + number;

    private static int RepositoryNumber(long repositoryId) => (int)(repositoryId % 1000);

    private static bool OwnsRepository(string account, int index, long repositoryId)
        => repositoryId / 1000 == index && RepositoryNumbers(account).Contains(RepositoryNumber(repositoryId));

    private static string RepositoryName(int number) => $"svc-{number:000}";

    private static string Sha(string account, int number, string branch)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes($"{account}/{number}/{branch}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int ReadInt(string? value, int fallback, int min, int max)
        => int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private static HttpResponseMessage Status(HttpStatusCode status)
        => new(status) { Content = new StringContent("{\"message\":\"fake provider body\"}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Json(object value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    [GeneratedRegex("^e2e-(?<account>[a-z0-9]+)-[A-Za-z0-9_-]{4,}$")]
    private static partial Regex TokenShape();

    [GeneratedRegex("^/repositories/(?<id>\\d+)(?<rest>/branches)?$")]
    private static partial Regex RepositoryRoute();

    [GeneratedRegex("^/projects/(?<id>\\d+)(?<rest>/repository/branches)?$")]
    private static partial Regex ProjectRoute();
}
