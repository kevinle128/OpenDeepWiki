using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Shared HTTP behavior of the PAT provider clients: header-only authentication, no redirects,
/// stable error mapping, bounded response size, and validated pagination links.
/// </summary>
public abstract partial class GitProviderClientBase : IGitProviderClient
{
    private const long MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan MaxReportedWait = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _httpClientName;
    private readonly ProviderPaginationCursorCodec _cursorCodec;
    private readonly ILogger _logger;

    protected GitProviderClientBase(
        IHttpClientFactory httpClientFactory,
        string httpClientName,
        ProviderPaginationCursorCodec cursorCodec,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _httpClientName = httpClientName;
        _cursorCodec = cursorCodec;
        _logger = logger;
    }

    public abstract GitProvider Provider { get; }

    public abstract Task<GitProviderIdentity> ValidateAsync(GitProviderTarget target, CancellationToken cancellationToken);

    public abstract Task<RemoteRepository> GetRepositoryAsync(
        GitProviderTarget target, string providerRepositoryId, CancellationToken cancellationToken);

    public abstract Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
        GitProviderTarget target, string? cursor, int pageSize, CancellationToken cancellationToken);

    public abstract Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
        GitProviderTarget target, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Adds provider headers. The token goes into a header only, never into a URL.
    /// </summary>
    protected abstract void Authenticate(HttpRequestMessage request, string token);

    /// <summary>
    /// A provider repository ID is a number. Accepting anything else would let a caller change the request path.
    /// </summary>
    protected static string RequireNumericId(string providerRepositoryId)
    {
        if (string.IsNullOrEmpty(providerRepositoryId)
            || providerRepositoryId.Length > 19
            || !providerRepositoryId.All(char.IsAsciiDigit))
        {
            throw new GitProviderException(GitProviderErrorCodes.InvalidRepositoryId);
        }

        return providerRepositoryId;
    }

    protected static Uri BuildUri(string origin, string path, IEnumerable<KeyValuePair<string, string>> query)
    {
        var pairs = query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}").ToList();
        var text = origin + path + (pairs.Count == 0 ? string.Empty : "?" + string.Join("&", pairs));
        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            ? uri
            : throw new GitProviderException(GitProviderErrorCodes.ServerUrlInvalid);
    }

    /// <summary>
    /// Fixed parameters first, then cursor fields. A cursor field replaces a fixed parameter of the same name.
    /// </summary>
    protected static List<KeyValuePair<string, string>> MergeQuery(
        IEnumerable<KeyValuePair<string, string>> fixedParameters,
        IReadOnlyDictionary<string, string> cursorFields)
    {
        var merged = fixedParameters.Where(pair => !cursorFields.ContainsKey(pair.Key)).ToList();
        merged.AddRange(cursorFields);
        return merged;
    }

    protected IReadOnlyDictionary<string, string> DecodeCursor(
        string? cursor, GitProviderTarget target, string origin, string kind, string? scope)
        => string.IsNullOrEmpty(cursor)
            ? new Dictionary<string, string>()
            : _cursorCodec.Decode(cursor, target, origin, kind, scope);

    protected ProviderPage<T> CreatePage<T>(
        IReadOnlyList<T> items,
        ProviderResponse response,
        GitProviderTarget target,
        string origin,
        string expectedPath,
        string kind,
        string? scope,
        IReadOnlySet<string> allowedFields,
        int? totalCount = null)
    {
        var next = ReadNextFields(response, origin, expectedPath, allowedFields);
        var cursor = next is null ? null : _cursorCodec.Encode(target, origin, kind, scope, next);
        return new ProviderPage<T>(items, cursor, totalCount);
    }

    /// <summary>
    /// Sends one GET request. A failure always becomes a <see cref="GitProviderException"/> with a stable code.
    /// </summary>
    protected async Task<ProviderResponse> GetAsync(GitProviderTarget target, string origin, Uri uri, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Options.Set(GitProviderGuardHandler.AllowedOriginKey, origin);
            Authenticate(request, target.Token);

            var client = _httpClientFactory.CreateClient(_httpClientName);
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            var failure = MapStatus(response);
            if (failure is not null)
            {
                throw failure;
            }

            return await ReadAsync(response, cancellationToken);
        }
        catch (GitProviderException ex)
        {
            response?.Dispose();
            throw LogFailure(target, ex, response?.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw LogFailure(target, Translate(ex), response?.StatusCode);
        }
    }

    private static async Task<ProviderResponse> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var links = response.Headers.TryGetValues("Link", out var values) ? values.ToList() : [];
        var nextPage = FirstHeader(response, "X-Next-Page");
        var total = FirstHeader(response, "X-Total");

        try
        {
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return new ProviderResponse(document, links, nextPage, total);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
            // The body can be large or hostile; only the stable code leaves this method.
            throw new GitProviderException(GitProviderErrorCodes.InvalidResponse);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static string? FirstHeader(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static GitProviderException? MapStatus(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and < 300)
        {
            return null;
        }

        var code = status switch
        {
            >= 300 and < 400 => GitProviderErrorCodes.RedirectBlocked,
            401 => GitProviderErrorCodes.Unauthorized,
            403 when IsRateLimited(response) => GitProviderErrorCodes.RateLimited,
            403 => GitProviderErrorCodes.Forbidden,
            404 => GitProviderErrorCodes.NotFound,
            429 => GitProviderErrorCodes.RateLimited,
            >= 500 => GitProviderErrorCodes.Unavailable,
            _ => GitProviderErrorCodes.RequestRejected
        };

        return new GitProviderException(code, code == GitProviderErrorCodes.RateLimited ? ReadWait(response) : null);
    }

    private static bool IsRateLimited(HttpResponseMessage response)
        => response.Headers.RetryAfter is not null
           || FirstHeader(response, "X-RateLimit-Remaining") == "0";

    private static TimeSpan? ReadWait(HttpResponseMessage response)
    {
        TimeSpan? wait = null;
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            wait = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            wait = date - DateTimeOffset.UtcNow;
        }
        else if (long.TryParse(FirstHeader(response, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var reset)
                 && reset > 0 && reset < DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            wait = DateTimeOffset.FromUnixTimeSeconds(reset) - DateTimeOffset.UtcNow;
        }

        return wait is null ? null : TimeSpan.FromSeconds(Math.Clamp(wait.Value.TotalSeconds, 0, MaxReportedWait.TotalSeconds));
    }

    /// <summary>
    /// Maps transport failures to stable codes. A <see cref="GitProviderException"/> raised inside the
    /// connection callback is unwrapped, so its code is kept.
    /// </summary>
    private static GitProviderException Translate(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case GitProviderException known:
                    return known;
                case AuthenticationException:
                    return new GitProviderException(GitProviderErrorCodes.TlsFailure);
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData }:
                    return new GitProviderException(GitProviderErrorCodes.DnsFailure);
            }
        }

        return exception is OperationCanceledException
            ? new GitProviderException(GitProviderErrorCodes.Timeout)
            : new GitProviderException(GitProviderErrorCodes.Unavailable);
    }

    private GitProviderException LogFailure(GitProviderTarget target, GitProviderException exception, HttpStatusCode? status)
    {
        // Only identifiers and stable codes are logged: never the token, URL, headers, or response body.
        _logger.LogWarning(
            "Git provider request failed. Provider: {Provider}, ConnectionId: {ConnectionId}, ErrorCode: {ErrorCode}, StatusCode: {StatusCode}",
            Provider, target.ConnectionId, exception.Code, status is null ? (int?)null : (int)status);
        return exception;
    }

    /// <summary>
    /// Reads the next-page link. The link is checked (HTTPS, same origin, same path), and only the
    /// allowed pagination fields are taken from it. The link itself is never followed or stored.
    /// </summary>
    private static Dictionary<string, string>? ReadNextFields(
        ProviderResponse response, string origin, string expectedPath, IReadOnlySet<string> allowedFields)
    {
        foreach (var header in response.LinkHeaders)
        {
            foreach (Match match in LinkEntry().Matches(header))
            {
                if (!HasNextRelation(match.Groups["params"].Value))
                {
                    continue;
                }

                return ReadFieldsFromLink(match.Groups["url"].Value, origin, expectedPath, allowedFields);
            }
        }

        if (allowedFields.Contains("page")
            && !string.IsNullOrWhiteSpace(response.NextPageHeader))
        {
            return ProviderPaginationCursorCodec.IsAllowedField("page", response.NextPageHeader.Trim())
                ? new Dictionary<string, string> { ["page"] = response.NextPageHeader.Trim() }
                : throw new GitProviderException(GitProviderErrorCodes.InvalidResponse);
        }

        return null;
    }

    private static Dictionary<string, string> ReadFieldsFromLink(
        string link, string origin, string expectedPath, IReadOnlySet<string> allowedFields)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal))
        {
            throw new GitProviderException(GitProviderErrorCodes.InvalidResponse);
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        var fields = new Dictionary<string, string>();
        foreach (var name in allowedFields)
        {
            if (!query.TryGetValue(name, out var values) || values.Count == 0)
            {
                continue;
            }

            var value = values[0] ?? string.Empty;
            if (!ProviderPaginationCursorCodec.IsAllowedField(name, value))
            {
                throw new GitProviderException(GitProviderErrorCodes.InvalidResponse);
            }

            fields[name] = value;
        }

        // A keyset position replaces the page number.
        if (fields.ContainsKey("id_after") || fields.ContainsKey("page_token"))
        {
            fields.Remove("page");
        }

        if (!fields.ContainsKey("page") && !fields.ContainsKey("id_after") && !fields.ContainsKey("page_token"))
        {
            throw new GitProviderException(GitProviderErrorCodes.InvalidResponse);
        }

        return fields;
    }

    private static bool HasNextRelation(string parameters)
    {
        var relation = RelationValue().Match(parameters);
        if (!relation.Success)
        {
            return false;
        }

        var value = relation.Groups[1].Success ? relation.Groups[1].Value : relation.Groups[2].Value;
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => string.Equals(token, "next", StringComparison.OrdinalIgnoreCase));
    }

    // ---- JSON helpers: any shape problem becomes a stable invalid-response error ----

    protected static JsonElement RequireObject(JsonElement element)
        => element.ValueKind == JsonValueKind.Object ? element : throw InvalidResponse();

    protected static IEnumerable<JsonElement> RequireArray(JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : throw InvalidResponse();

    protected static string RequireString(JsonElement element, string name)
        => OptionalString(element, name) is { Length: > 0 } value ? value : throw InvalidResponse();

    protected static string? OptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    protected static string RequireId(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var id)
            && id > 0)
        {
            return id.ToString(CultureInfo.InvariantCulture);
        }

        throw InvalidResponse();
    }

    protected static DateTimeOffset? OptionalDate(JsonElement element, string name)
        => DateTimeOffset.TryParse(
            OptionalString(element, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    protected static GitProviderException InvalidResponse() => new(GitProviderErrorCodes.InvalidResponse);

    [GeneratedRegex("<(?<url>[^>]*)>(?<params>[^<]*)")]
    private static partial Regex LinkEntry();

    [GeneratedRegex("rel\\s*=\\s*(?:\"([^\"]*)\"|([^\\s;,]+))", RegexOptions.IgnoreCase)]
    private static partial Regex RelationValue();

    /// <summary>
    /// Parsed provider answer. Dispose it after reading the JSON.
    /// </summary>
    protected sealed class ProviderResponse : IDisposable
    {
        private readonly JsonDocument _document;

        public ProviderResponse(JsonDocument document, IReadOnlyList<string> linkHeaders, string? nextPageHeader, string? totalHeader)
        {
            _document = document;
            LinkHeaders = linkHeaders;
            NextPageHeader = nextPageHeader;
            TotalHeader = totalHeader;
        }

        public JsonElement Root => _document.RootElement;

        public IReadOnlyList<string> LinkHeaders { get; }

        public string? NextPageHeader { get; }

        public string? TotalHeader { get; }

        public void Dispose() => _document.Dispose();
    }
}
