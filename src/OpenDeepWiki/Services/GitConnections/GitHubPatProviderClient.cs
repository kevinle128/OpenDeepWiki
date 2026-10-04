using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// GitHub.com client for personal access tokens. It uses numeric repository IDs, so renames and
/// transfers do not change identity, and it never builds a request from a provider link.
/// </summary>
public sealed class GitHubPatProviderClient : GitProviderClientBase
{
    private const string ApiOrigin = "https://api.github.com";
    private const string ApiVersion = "2022-11-28";
    private static readonly IReadOnlySet<string> PageFields = new HashSet<string> { "page" };

    public GitHubPatProviderClient(
        IHttpClientFactory httpClientFactory,
        ProviderPaginationCursorCodec cursorCodec,
        ILogger<GitHubPatProviderClient> logger)
        : base(httpClientFactory, GitProviderHttpClientNames.GitHub, cursorCodec, logger)
    {
    }

    public override GitProvider Provider => GitProvider.GitHub;

    public override async Task<GitProviderIdentity> ValidateAsync(GitProviderTarget target, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(target, ApiOrigin, BuildUri(ApiOrigin, "/user", []), cancellationToken);
        var user = RequireObject(response.Root);
        return new GitProviderIdentity(RequireId(user, "id"), RequireString(user, "login"));
    }

    public override async Task<RemoteRepository> GetRepositoryAsync(
        GitProviderTarget target, string providerRepositoryId, CancellationToken cancellationToken)
    {
        var id = RequireNumericId(providerRepositoryId);
        using var response = await GetAsync(target, ApiOrigin, BuildUri(ApiOrigin, $"/repositories/{id}", []), cancellationToken);
        return MapRepository(RequireObject(response.Root));
    }

    public override async Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
        GitProviderTarget target, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        const string path = "/user/repos";
        var fields = DecodeCursor(cursor, target, ApiOrigin, ProviderPaginationCursorCodec.RepositoriesKind, null);
        var query = MergeQuery(
        [
            new("visibility", "all"),
            new("affiliation", "owner,collaborator,organization_member"),
            new("sort", "full_name"),
            new("per_page", GitProviderPaging.Normalize(pageSize).ToString(CultureInfo.InvariantCulture))
        ], fields);

        using var response = await GetAsync(target, ApiOrigin, BuildUri(ApiOrigin, path, query), cancellationToken);
        var items = RequireArray(response.Root).Select(MapRepository).ToList();
        return CreatePage(items, response, target, ApiOrigin, path,
            ProviderPaginationCursorCodec.RepositoriesKind, null, PageFields);
    }

    public override async Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
        GitProviderTarget target, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var id = RequireNumericId(providerRepositoryId);
        var path = $"/repositories/{id}/branches";
        var fields = DecodeCursor(cursor, target, ApiOrigin, ProviderPaginationCursorCodec.BranchesKind, id);
        var query = MergeQuery(
            [new("per_page", GitProviderPaging.Normalize(pageSize).ToString(CultureInfo.InvariantCulture))], fields);

        using var response = await GetAsync(target, ApiOrigin, BuildUri(ApiOrigin, path, query), cancellationToken);
        var items = RequireArray(response.Root).Select(MapBranch).ToList();
        return CreatePage(items, response, target, ApiOrigin, path,
            ProviderPaginationCursorCodec.BranchesKind, id, PageFields);
    }

    protected override void Authenticate(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        request.Headers.UserAgent.ParseAdd("OpenDeepWiki");
    }

    private static RemoteRepository MapRepository(JsonElement element)
    {
        var repository = RequireObject(element);
        var ns = repository.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object
            ? OptionalString(owner, "login")
            : null;

        return new RemoteRepository(
            RequireId(repository, "id"),
            RequireString(repository, "name"),
            RequireString(repository, "full_name"),
            ns,
            OptionalString(repository, "description"),
            RequireString(repository, "clone_url"),
            OptionalString(repository, "html_url"),
            OptionalString(repository, "default_branch"),
            MapVisibility(repository),
            OptionalDate(repository, "updated_at"));
    }

    private static string MapVisibility(JsonElement repository) => OptionalString(repository, "visibility") switch
    {
        "public" => "Public",
        "internal" => "Internal",
        "private" => "Private",
        _ => repository.TryGetProperty("private", out var isPrivate) && isPrivate.ValueKind == JsonValueKind.False
            ? "Public"
            : "Private"
    };

    private static RemoteBranch MapBranch(JsonElement element)
    {
        var branch = RequireObject(element);
        var sha = branch.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object
            ? OptionalString(commit, "sha")
            : null;
        // The branch list does not say which branch is the default; the repository metadata does.
        return new RemoteBranch(RequireString(branch, "name"), false, sha);
    }
}
