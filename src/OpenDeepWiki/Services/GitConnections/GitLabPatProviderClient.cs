using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// GitLab.com and HTTPS self-hosted GitLab client for personal access tokens. It uses numeric project
/// IDs in paths, so subgroup paths and renames do not matter. The named HTTP client carries the
/// address guard that enforces the DNS and redirect policy.
/// </summary>
public sealed class GitLabPatProviderClient : GitProviderClientBase
{
    private const string ApiPrefix = "/api/v4";
    private static readonly IReadOnlySet<string> PageFields = new HashSet<string>
    {
        "page", "id_after", "page_token", "pagination", "order_by", "sort"
    };
    private static readonly IReadOnlySet<string> CatalogPageFields = new HashSet<string> { "page" };

    public GitLabPatProviderClient(
        IHttpClientFactory httpClientFactory,
        ProviderPaginationCursorCodec cursorCodec,
        ILogger<GitLabPatProviderClient> logger)
        : base(httpClientFactory, GitProviderHttpClientNames.GitLab, cursorCodec, logger)
    {
    }

    public override GitProvider Provider => GitProvider.GitLab;

    public override async Task<GitProviderIdentity> ValidateAsync(GitProviderTarget target, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(target, target.ServerUrl, BuildUri(target.ServerUrl, $"{ApiPrefix}/user", []), cancellationToken);
        var user = RequireObject(response.Root);
        return new GitProviderIdentity(RequireId(user, "id"), RequireString(user, "username"));
    }

    public override async Task<RemoteRepository> GetRepositoryAsync(
        GitProviderTarget target, string providerRepositoryId, CancellationToken cancellationToken)
    {
        var id = RequireNumericId(providerRepositoryId);
        var uri = BuildUri(target.ServerUrl, $"{ApiPrefix}/projects/{id}", []);
        using var response = await GetAsync(target, target.ServerUrl, uri, cancellationToken);
        return MapProject(RequireObject(response.Root));
    }

    public override async Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
        GitProviderTarget target, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var path = $"{ApiPrefix}/projects";
        var fields = DecodeCursor(cursor, target, target.ServerUrl, ProviderPaginationCursorCodec.RepositoriesKind, null);
        var size = GitProviderPaging.Normalize(pageSize).ToString(CultureInfo.InvariantCulture);
        var fixedParameters = new List<KeyValuePair<string, string>>
        {
            new("membership", "true"),
            new("order_by", "id"),
            new("sort", "asc"),
            new("per_page", size)
        };

        ProviderResponse response;
        if (fields.Count == 0)
        {
            // Keyset paging is faster and stable. Older servers reject it, so the first page falls back to offset paging.
            var keyset = fixedParameters.Append(new("pagination", "keyset")).ToList();
            try
            {
                response = await GetAsync(target, target.ServerUrl, BuildUri(target.ServerUrl, path, keyset), cancellationToken);
            }
            catch (GitProviderException ex) when (ex.Code == GitProviderErrorCodes.RequestRejected)
            {
                response = await GetAsync(target, target.ServerUrl, BuildUri(target.ServerUrl, path, fixedParameters), cancellationToken);
            }
        }
        else
        {
            var query = MergeQuery(fixedParameters, fields);
            response = await GetAsync(target, target.ServerUrl, BuildUri(target.ServerUrl, path, query), cancellationToken);
        }

        using (response)
        {
            var items = RequireArray(response.Root).Select(MapProject).ToList();
            return CreatePage(items, response, target, target.ServerUrl, path,
                ProviderPaginationCursorCodec.RepositoriesKind, null, PageFields, ReadTotal(response));
        }
    }

    public async Task<ProviderPage<RemoteRepository>> ListCatalogAsync(
        GitProviderTarget target, string? cursor, int pageSize, string search, string visibility, string sort,
        CancellationToken cancellationToken)
    {
        var size = GitProviderPaging.Normalize(pageSize);
        var shortSearch = search.Length > 0 && search.EnumerateRunes().Take(3).Count() < 3;
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { search, visibility, sort, size }))));
        const string kind = "gitlab-catalog";
        var fields = DecodeCursor(cursor, target, target.ServerUrl, kind, scope);
        if (fields.Any(field => !CatalogPageFields.Contains(field.Key)))
            throw new GitProviderException(GitProviderErrorCodes.InvalidCursor);

        var path = $"{ApiPrefix}/projects";
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("membership", "true"),
            new("simple", "true"),
            new("per_page", size.ToString(CultureInfo.InvariantCulture)),
            new("order_by", sort == "name" ? "name" : "last_activity_at"),
            new("sort", sort == "updated" ? "desc" : "asc")
        };
        if (search.Length > 0 && !shortSearch)
        {
            parameters.Add(new("search", search));
            parameters.Add(new("search_namespaces", "true"));
        }
        if (visibility != "all")
            parameters.Add(new("visibility", visibility));

        var seenPages = new HashSet<string> { fields.TryGetValue("page", out var firstPage) ? firstPage : "1" };
        // GitLab matches short queries exactly. Filter sorted pages locally and return the first page with matches.
        // ponytail: skip at most 100 empty pages; cache catalog metadata if short searches over larger catalogs become slow.
        for (var page = 0; page < 100; page++)
        {
            using var response = await GetAsync(target, target.ServerUrl,
                BuildUri(target.ServerUrl, path, MergeQuery(parameters, fields)), cancellationToken);
            var items = RequireArray(response.Root).Select(MapProject)
                .Where(item => !shortSearch || $"{item.Name} {item.FullName} {item.Namespace} {item.Description}"
                    .Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var result = CreatePage(items, response, target, target.ServerUrl, path,
                kind, scope, CatalogPageFields, shortSearch ? null : ReadTotal(response));
            if (!shortSearch || items.Count > 0 || result.NextCursor is null)
                return result;

            fields = DecodeCursor(result.NextCursor, target, target.ServerUrl, kind, scope);
            if (!seenPages.Add(fields["page"]))
                throw new GitProviderException(GitProviderErrorCodes.InvalidResponse);
        }

        throw new GitProviderException(GitProviderErrorCodes.RequestRejected);
    }

    public override async Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
        GitProviderTarget target, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var id = RequireNumericId(providerRepositoryId);
        var path = $"{ApiPrefix}/projects/{id}/repository/branches";
        var fields = DecodeCursor(cursor, target, target.ServerUrl, ProviderPaginationCursorCodec.BranchesKind, id);
        var query = MergeQuery(
            [new("per_page", GitProviderPaging.Normalize(pageSize).ToString(CultureInfo.InvariantCulture))], fields);

        using var response = await GetAsync(target, target.ServerUrl, BuildUri(target.ServerUrl, path, query), cancellationToken);
        var items = RequireArray(response.Root).Select(MapBranch).ToList();
        return CreatePage(items, response, target, target.ServerUrl, path,
            ProviderPaginationCursorCodec.BranchesKind, id, PageFields, ReadTotal(response));
    }

    protected override void Authenticate(HttpRequestMessage request, string token)
    {
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("OpenDeepWiki");
    }

    private static int? ReadTotal(ProviderResponse response)
        => int.TryParse(response.TotalHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var total) ? total : null;

    private static RemoteRepository MapProject(JsonElement element)
    {
        var project = RequireObject(element);
        var fullName = RequireString(project, "path_with_namespace");
        var ns = project.TryGetProperty("namespace", out var namespaceElement) && namespaceElement.ValueKind == JsonValueKind.Object
            ? OptionalString(namespaceElement, "full_path")
            : null;
        ns ??= fullName.Contains('/') ? fullName[..fullName.LastIndexOf('/')] : null;

        return new RemoteRepository(
            RequireId(project, "id"),
            RequireString(project, "name"),
            fullName,
            ns,
            OptionalString(project, "description"),
            RequireString(project, "http_url_to_repo"),
            OptionalString(project, "web_url"),
            OptionalString(project, "default_branch"),
            OptionalString(project, "visibility") switch
            {
                "public" => "Public",
                "internal" => "Internal",
                _ => "Private"
            },
            OptionalDate(project, "last_activity_at"));
    }

    private static RemoteBranch MapBranch(JsonElement element)
    {
        var branch = RequireObject(element);
        var sha = branch.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object
            ? OptionalString(commit, "id")
            : null;
        var isDefault = branch.TryGetProperty("default", out var flag) && flag.ValueKind == JsonValueKind.True;
        return new RemoteBranch(RequireString(branch, "name"), isDefault, sha);
    }
}
