using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Creates and reads opaque pagination cursors. A cursor holds only a small set of validated
/// pagination fields, never a URL. It is protected with Data Protection, so a client cannot read,
/// change, or forge it, and it is bound to one provider, connection, API origin, list kind, and scope.
/// </summary>
public sealed partial class ProviderPaginationCursorCodec
{
    /// <summary>
    /// Purpose string that isolates cursors from other protected data.
    /// </summary>
    public const string Purpose = "OpenDeepWiki.GitConnections.Cursor.v1";

    public const string RepositoriesKind = "repositories";
    public const string BranchesKind = "branches";

    private const int Version = 1;
    private const int MaxCursorLength = 2048;
    private const int MaxFieldValueLength = 512;

    private readonly IDataProtector _protector;

    public ProviderPaginationCursorCodec(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    /// <summary>
    /// True when the field name is a pagination field and the value has the expected shape.
    /// </summary>
    public static bool IsAllowedField(string name, string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxFieldValueLength || value.Any(char.IsControl))
        {
            return false;
        }

        return name switch
        {
            "page" => DigitsOnly().IsMatch(value) && value.Length <= 7,
            "id_after" => DigitsOnly().IsMatch(value) && value.Length <= 19,
            "page_token" => true,
            "pagination" => value == "keyset",
            "order_by" => OrderByValue().IsMatch(value),
            "sort" => value is "asc" or "desc",
            _ => false
        };
    }

    public string Encode(
        GitProviderTarget target,
        string origin,
        string kind,
        string? scope,
        IReadOnlyDictionary<string, string> fields)
    {
        if (string.IsNullOrWhiteSpace(target.ConnectionId) || fields.Any(field => !IsAllowedField(field.Key, field.Value)))
        {
            throw new GitProviderException(GitProviderErrorCodes.InvalidCursor);
        }

        var payload = new CursorPayload(Version, (int)target.Provider, target.ConnectionId, origin, kind, scope, new Dictionary<string, string>(fields));
        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    /// <summary>
    /// Returns the pagination fields, or throws <see cref="GitProviderErrorCodes.InvalidCursor"/>
    /// when the cursor is not valid for this target. No request is sent before this check passes.
    /// </summary>
    public IReadOnlyDictionary<string, string> Decode(
        string cursor,
        GitProviderTarget target,
        string origin,
        string kind,
        string? scope)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaxCursorLength)
        {
            throw Invalid();
        }

        CursorPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CursorPayload>(_protector.Unprotect(cursor));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            throw Invalid();
        }

        if (payload is null
            || payload.V != Version
            || payload.P != (int)target.Provider
            || !string.Equals(payload.C, target.ConnectionId, StringComparison.Ordinal)
            || !string.Equals(payload.O, origin, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(payload.K, kind, StringComparison.Ordinal)
            || !string.Equals(payload.S, scope, StringComparison.Ordinal)
            || payload.F is null
            || payload.F.Any(field => !IsAllowedField(field.Key, field.Value)))
        {
            throw Invalid();
        }

        return payload.F;
    }

    private static GitProviderException Invalid() => new(GitProviderErrorCodes.InvalidCursor);

    private sealed record CursorPayload(int V, int P, string C, string O, string K, string? S, Dictionary<string, string> F);

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex DigitsOnly();

    [GeneratedRegex("^[a-z_]{1,32}$")]
    private static partial Regex OrderByValue();
}
