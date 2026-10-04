using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Models.GitConnections;
using OpenDeepWiki.Services.Auth;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// A connection request failed for a reason that is not a provider call. The code is stable and safe to return.
/// </summary>
public sealed class GitConnectionServiceException : Exception
{
    public GitConnectionServiceException(string errorCode)
        : base($"Git connection request failed: {errorCode}.")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// Lifecycle and catalog operations of shared Git connections. Every authenticated user can list, create,
/// and use connections; only the creator or an Admin can change them.
/// </summary>
public interface IGitConnectionService
{
    Task<IReadOnlyList<GitConnectionResponse>> ListAsync(CancellationToken cancellationToken);

    Task<GitConnectionResponse> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Validates the token with the provider first. The provider account decides the identity:
    /// an existing connection is returned unchanged and a soft-deleted one is restored.
    /// </summary>
    Task<CreateGitConnectionResult> CreateAsync(CreateGitConnectionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Renames the connection and/or replaces its credential. A replacement is validated before an
    /// atomic swap and must belong to the same provider account.
    /// </summary>
    Task<GitConnectionResponse> UpdateAsync(string id, UpdateGitConnectionRequest request, CancellationToken cancellationToken);

    Task<GitConnectionHealthResponse> TestAsync(string id, CancellationToken cancellationToken);

    Task<GitConnectionResponse> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes the connection. Fails while an active repository still depends on it.
    /// </summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken);

    Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
        string id, string? cursor, int pageSize, CancellationToken cancellationToken);

    Task<ProviderPage<RemoteRepository>> ListCatalogAsync(
        string id, string? cursor, int pageSize, string? query, string? visibility, string? sort, CancellationToken cancellationToken);

    Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
        string id, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<GitConnectionAuditEventResponse>> ListAuditEventsAsync(
        string id, int limit, CancellationToken cancellationToken);
}

public sealed class GitConnectionService : IGitConnectionService
{
    private const string GitHubServerUrl = "https://github.com";
    private const string GitLabComServerUrl = "https://gitlab.com";
    private const int MaxTokenLength = 4096;
    private const int MaxDisplayNameLength = 200;
    private const int MaxAccountNameLength = 200;
    private const int DefaultAuditLimit = 30;
    private const int MaxAuditLimit = 100;

    private readonly IContext _context;
    private readonly IUserContext _userContext;
    private readonly IGitConnectionAuthorizationService _authorization;
    private readonly IGitConnectionSecretProtector _protector;
    private readonly IGitProviderClientResolver _clients;
    private readonly GitLabServerUrlValidator _urlValidator;
    private readonly ILogger<GitConnectionService> _logger;
    private readonly ProviderPaginationCursorCodec _cursors;

    public GitConnectionService(
        IContext context,
        IUserContext userContext,
        IGitConnectionAuthorizationService authorization,
        IGitConnectionSecretProtector protector,
        IGitProviderClientResolver clients,
        GitLabServerUrlValidator urlValidator,
        ILogger<GitConnectionService> logger,
        ProviderPaginationCursorCodec cursors)
    {
        _context = context;
        _userContext = userContext;
        _authorization = authorization;
        _protector = protector;
        _clients = clients;
        _urlValidator = urlValidator;
        _logger = logger;
        _cursors = cursors;
    }

    public async Task<IReadOnlyList<GitConnectionResponse>> ListAsync(CancellationToken cancellationToken)
    {
        RequireUserId();
        var connections = await _context.GitConnections
            .AsNoTracking()
            .Where(connection => !connection.IsDeleted)
            .OrderBy(connection => connection.CreatedAt)
            .ToListAsync(cancellationToken);

        var ids = connections.Select(connection => connection.Id).ToList();
        var counts = await _context.Repositories
            .AsNoTracking()
            .Where(repository => repository.GitConnectionId != null && ids.Contains(repository.GitConnectionId) && !repository.IsDeleted)
            .GroupBy(repository => repository.GitConnectionId!)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken);

        var responses = new List<GitConnectionResponse>(connections.Count);
        foreach (var connection in connections)
        {
            responses.Add(await ToResponseAsync(connection, counts.GetValueOrDefault(connection.Id), cancellationToken));
        }

        return responses;
    }

    public async Task<GitConnectionResponse> GetAsync(string id, CancellationToken cancellationToken)
    {
        RequireUserId();
        var connection = await FindActiveAsync(id, track: false, cancellationToken);
        return await ToResponseAsync(connection, await CountRepositoriesAsync(id, cancellationToken), cancellationToken);
    }

    public async Task<CreateGitConnectionResult> CreateAsync(CreateGitConnectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = RequireUserId();
        var provider = ParseProvider(request.Provider);
        var token = ValidateToken(request.Token);
        var displayName = ValidateDisplayName(request.DisplayName);
        var serverUrl = ResolveServerUrl(provider, request.ServerUrl);

        if (!await _context.Users.AnyAsync(user => user.Id == userId && !user.IsDeleted, cancellationToken))
        {
            throw new GitConnectionServiceException(GitConnectionErrorCodes.Unauthorized);
        }

        // The provider decides who the token belongs to. Nothing is protected or stored before this succeeds.
        var identity = await _clients.Resolve(provider)
            .ValidateAsync(new GitProviderTarget(null, provider, serverUrl, token), cancellationToken);
        var accountName = Truncate(identity.AccountName, MaxAccountNameLength);
        var name = displayName ?? DefaultDisplayName(accountName, serverUrl);

        // A concurrent creator can win the unique identity index, so a lost race re-reads once.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await FindByIdentityAsync(provider, serverUrl, identity.ExternalAccountId, cancellationToken);
            if (existing is { IsDeleted: false })
            {
                return new CreateGitConnectionResult(
                    await ToResponseAsync(existing, await CountRepositoriesAsync(existing.Id, cancellationToken), cancellationToken),
                    GitConnectionCreateOutcomes.Existing);
            }

            if (existing is not null)
            {
                return await RestoreAsync(existing, userId, token, name, accountName, cancellationToken);
            }

            var created = NewConnection(provider, serverUrl, identity, accountName, name, userId, token);
            var audit = NewAudit(created.Id, userId, GitConnectionAuditEventType.Created, GitConnectionAuditOutcome.Success, null);
            _context.GitConnections.Add(created);
            _context.GitConnectionAuditEvents.Add(audit);
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return new CreateGitConnectionResult(
                    await ToResponseAsync(created, 0, cancellationToken),
                    GitConnectionCreateOutcomes.Created);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Added entities become detached, so the next attempt starts clean.
                _context.GitConnectionAuditEvents.Remove(audit);
                _context.GitConnections.Remove(created);
            }
        }

        throw new GitConnectionServiceException(GitConnectionErrorCodes.Conflict);
    }

    public async Task<GitConnectionResponse> UpdateAsync(string id, UpdateGitConnectionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = RequireUserId();
        var connection = await FindActiveAsync(id, track: true, cancellationToken);
        await RequireMaintainerAsync(connection, cancellationToken);

        var displayName = ValidateDisplayName(request.DisplayName);
        var token = string.IsNullOrEmpty(request.Token) ? null : ValidateToken(request.Token);
        var events = new List<GitConnectionAuditEvent>();

        // The provider call comes first. A rejected replacement must not leave a half-applied rename behind.
        var identity = token is null
            ? null
            : await ValidateReplacementAsync(connection, userId, token, cancellationToken);

        if (displayName is not null && displayName != connection.DisplayName)
        {
            connection.DisplayName = displayName;
            events.Add(NewAudit(connection.Id, userId, GitConnectionAuditEventType.Renamed, GitConnectionAuditOutcome.Success, null));
        }

        if (token is not null && identity is not null)
        {
            connection.ProtectedToken = _protector.Protect(token);
            connection.AccountName = Truncate(identity.AccountName, MaxAccountNameLength);
            connection.LastValidatedAt = DateTime.UtcNow;
            connection.LastValidationErrorCode = null;
            events.Add(NewAudit(connection.Id, userId, GitConnectionAuditEventType.CredentialRotated, GitConnectionAuditOutcome.Success, null));
        }

        if (events.Count > 0)
        {
            Touch(connection);
            _context.GitConnectionAuditEvents.AddRange(events);
            await SaveAsync(cancellationToken);
        }

        return await ToResponseAsync(connection, await CountRepositoriesAsync(id, cancellationToken), cancellationToken);
    }

    public async Task<GitConnectionHealthResponse> TestAsync(string id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var connection = await FindActiveAsync(id, track: true, cancellationToken);
        await RequireMaintainerAsync(connection, cancellationToken);
        var token = ReadToken(connection);

        string? errorCode = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var identity = await _clients.Resolve(connection.Provider).ValidateAsync(
                new GitProviderTarget(connection.Id, connection.Provider, connection.NormalizedServerUrl, token), cancellationToken);
            if (!string.Equals(identity.ExternalAccountId, connection.ExternalAccountId, StringComparison.Ordinal))
            {
                errorCode = GitConnectionErrorCodes.AccountMismatch;
            }
        }
        catch (GitProviderException ex)
        {
            errorCode = ex.Code;
        }

        stopwatch.Stop();
        var checkedAt = DateTime.UtcNow;
        connection.LastValidationErrorCode = errorCode;
        if (errorCode is null)
        {
            connection.LastValidatedAt = checkedAt;
        }

        Touch(connection);
        _context.GitConnectionAuditEvents.Add(NewAudit(
            connection.Id, userId, GitConnectionAuditEventType.Validated,
            errorCode is null ? GitConnectionAuditOutcome.Success : GitConnectionAuditOutcome.Failure, errorCode));
        await SaveAsync(cancellationToken);

        return new GitConnectionHealthResponse(errorCode is null, StateOf(connection), errorCode, stopwatch.ElapsedMilliseconds, checkedAt);
    }

    public async Task<GitConnectionResponse> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var connection = await FindActiveAsync(id, track: true, cancellationToken);
        await RequireMaintainerAsync(connection, cancellationToken);

        if (connection.IsEnabled != enabled)
        {
            connection.IsEnabled = enabled;
            Touch(connection);
            _context.GitConnectionAuditEvents.Add(NewAudit(
                connection.Id, userId,
                enabled ? GitConnectionAuditEventType.Enabled : GitConnectionAuditEventType.Disabled,
                GitConnectionAuditOutcome.Success, null));
            await SaveAsync(cancellationToken);
        }

        return await ToResponseAsync(connection, await CountRepositoriesAsync(id, cancellationToken), cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var connection = await FindActiveAsync(id, track: true, cancellationToken);
        await RequireMaintainerAsync(connection, cancellationToken);

        if (await CountRepositoriesAsync(id, cancellationToken) > 0)
        {
            throw new GitConnectionServiceException(GitConnectionErrorCodes.InUse);
        }

        // Soft delete only: the identity row stays so the same account is restored, not duplicated.
        // The credential is scrubbed because a deleted connection must not keep a usable secret.
        connection.MarkAsDeleted();
        connection.ProtectedToken = string.Empty;
        connection.ConcurrencyStamp = NewStamp();
        _context.GitConnectionAuditEvents.Add(NewAudit(
            connection.Id, userId, GitConnectionAuditEventType.Deleted, GitConnectionAuditOutcome.Success, null));
        await SaveAsync(cancellationToken);
    }

    public async Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(
        string id, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var (connection, target) = await OpenForUseAsync(id, cancellationToken);
        return await _clients.Resolve(connection.Provider).ListRepositoriesAsync(target, cursor, pageSize, cancellationToken);
    }

    public async Task<ProviderPage<RemoteRepository>> ListCatalogAsync(
        string id, string? cursor, int pageSize, string? query, string? visibility, string? sort, CancellationToken cancellationToken)
    {
        var search = (query ?? string.Empty).Trim().ToLowerInvariant();
        var access = visibility ?? "all";
        var order = sort ?? "updated";
        if (search.Length > 200 || search.Any(char.IsControl)
            || access is not ("all" or "public" or "private")
            || order is not ("updated" or "updatedAsc" or "name"))
            throw new GitProviderException(GitProviderErrorCodes.RequestRejected);

        var (connection, target) = await OpenForUseAsync(id, cancellationToken);
        var client = _clients.Resolve(connection.Provider);
        if (client is GitLabPatProviderClient gitlab)
            return await gitlab.ListCatalogAsync(target, cursor, pageSize, search, access, order, cancellationToken);

        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { search, access, order }))));
        const string kind = "catalog";
        var offset = 0;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var fields = _cursors.Decode(cursor, target, connection.NormalizedServerUrl, kind, scope);
            if (fields.Count != 1 || !fields.TryGetValue("page", out var value)
                || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
                throw new GitProviderException(GitProviderErrorCodes.InvalidCursor);
        }

        var repositories = new Dictionary<string, RemoteRepository>(StringComparer.Ordinal);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? providerCursor = null;
        // Read the complete accessible catalog before sorting or filtering. Never return a partial search as complete.
        // ponytail: scan at most 100 provider pages per request; use a catalog cache if provider latency becomes a limit.
        for (var page = 0; ; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page == 100)
                throw new GitProviderException(GitProviderErrorCodes.RequestRejected);
            var result = await client.ListRepositoriesAsync(target, providerCursor, GitProviderPaging.MaxPageSize, cancellationToken);
            foreach (var item in result.Items)
                repositories[item.ProviderRepositoryId] = item;
            if (string.IsNullOrWhiteSpace(result.NextCursor)) break;
            if (!seenCursors.Add(result.NextCursor))
                throw new GitProviderException(GitProviderErrorCodes.InvalidResponse);
            providerCursor = result.NextCursor;
        }

        var matches = repositories.Values.Where(item =>
            (access == "all" || (access == "public"
                ? item.Visibility.Equals("Public", StringComparison.OrdinalIgnoreCase)
                : !item.Visibility.Equals("Public", StringComparison.OrdinalIgnoreCase)))
            && (search.Length == 0 || $"{item.Name} {item.FullName} {item.Namespace}".Contains(search, StringComparison.OrdinalIgnoreCase)));
        var ordered = order switch
        {
            "name" => matches.OrderBy(item => item.FullName, StringComparer.OrdinalIgnoreCase),
            "updatedAsc" => matches.OrderBy(item => item.UpdatedAt ?? DateTimeOffset.MinValue),
            _ => matches.OrderByDescending(item => item.UpdatedAt ?? DateTimeOffset.MinValue)
        };
        var all = ordered.ThenBy(item => item.ProviderRepositoryId, StringComparer.Ordinal).ToList();
        var size = GitProviderPaging.Normalize(pageSize);
        var items = all.Skip(offset).Take(size).ToList();
        var next = offset + items.Count;
        var nextCursor = next < all.Count
            ? _cursors.Encode(target, connection.NormalizedServerUrl, kind, scope,
                new Dictionary<string, string> { ["page"] = next.ToString(CultureInfo.InvariantCulture) })
            : null;
        return new ProviderPage<RemoteRepository>(items, nextCursor, all.Count);
    }

    public async Task<ProviderPage<RemoteBranch>> ListBranchesAsync(
        string id, string providerRepositoryId, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var (connection, target) = await OpenForUseAsync(id, cancellationToken);
        return await _clients.Resolve(connection.Provider)
            .ListBranchesAsync(target, providerRepositoryId, cursor, pageSize, cancellationToken);
    }

    public async Task<IReadOnlyList<GitConnectionAuditEventResponse>> ListAuditEventsAsync(
        string id, int limit, CancellationToken cancellationToken)
    {
        RequireUserId();
        var connection = await FindActiveAsync(id, track: false, cancellationToken);
        await RequireMaintainerAsync(connection, cancellationToken);

        var take = limit < 1 ? DefaultAuditLimit : Math.Min(limit, MaxAuditLimit);
        var query = _context.GitConnectionAuditEvents
            .AsNoTracking()
            .Where(audit => audit.GitConnectionId == id);

        // A restore hands the connection to a new creator. That creator sees only events from the latest
        // restore onward. Admin keeps the full history. The Restored event is written in the same save as
        // the restore, so its time is the restore moment.
        if (!await _authorization.IsAdminAsync(cancellationToken))
        {
            var restoredAt = await _context.GitConnectionAuditEvents
                .Where(audit => audit.GitConnectionId == id && audit.EventType == GitConnectionAuditEventType.Restored)
                .MaxAsync(audit => (DateTime?)audit.CreatedAt, cancellationToken);
            if (restoredAt is not null)
            {
                query = query.Where(audit => audit.CreatedAt >= restoredAt);
            }
        }

        var events = await query
            .OrderByDescending(audit => audit.CreatedAt)
            .ThenByDescending(audit => audit.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        return events
            .Select(audit => new GitConnectionAuditEventResponse(
                audit.Id, audit.EventType.ToString(), audit.Outcome.ToString(),
                audit.ActorUserId, audit.RepositoryId, audit.ErrorCode, audit.CreatedAt))
            .ToList();
    }

    // ---- create helpers ----

    private async Task<CreateGitConnectionResult> RestoreAsync(
        GitConnection existing, string userId, string token, string displayName, string accountName, CancellationToken cancellationToken)
    {
        // The deleted row kept no credential, so the freshly validated token replaces it and the caller owns
        // the restored connection. A disabled connection stays disabled: a restore never re-enables use.
        existing.Restore();
        existing.ProtectedToken = _protector.Protect(token);
        existing.CreatedByUserId = userId;
        existing.DisplayName = displayName;
        existing.AccountName = accountName;
        existing.LastValidatedAt = DateTime.UtcNow;
        existing.LastValidationErrorCode = null;
        existing.ConcurrencyStamp = NewStamp();
        _context.GitConnectionAuditEvents.Add(NewAudit(
            existing.Id, userId, GitConnectionAuditEventType.Restored, GitConnectionAuditOutcome.Success, null));
        await SaveAsync(cancellationToken);

        return new CreateGitConnectionResult(
            await ToResponseAsync(existing, 0, cancellationToken),
            GitConnectionCreateOutcomes.Restored);
    }

    private GitConnection NewConnection(
        GitProvider provider, string serverUrl, GitProviderIdentity identity, string accountName,
        string displayName, string userId, string token)
        => new()
        {
            // The GUID text fits the 36-character columns that reference this ID.
            Id = Guid.NewGuid().ToString(),
            Provider = provider,
            NormalizedServerUrl = serverUrl,
            ExternalAccountId = identity.ExternalAccountId,
            AccountName = accountName,
            DisplayName = displayName,
            ProtectedToken = _protector.Protect(token),
            CreatedByUserId = userId,
            IsEnabled = true,
            LastValidatedAt = DateTime.UtcNow,
            ConcurrencyStamp = NewStamp()
        };

    private Task<GitConnection?> FindByIdentityAsync(
        GitProvider provider, string serverUrl, string externalAccountId, CancellationToken cancellationToken)
        => _context.GitConnections.FirstOrDefaultAsync(
            connection => connection.Provider == provider
                          && connection.NormalizedServerUrl == serverUrl
                          && connection.ExternalAccountId == externalAccountId,
            cancellationToken);

    private async Task<GitProviderIdentity> ValidateReplacementAsync(
        GitConnection connection, string userId, string token, CancellationToken cancellationToken)
    {
        GitProviderIdentity identity;
        try
        {
            identity = await _clients.Resolve(connection.Provider).ValidateAsync(
                new GitProviderTarget(connection.Id, connection.Provider, connection.NormalizedServerUrl, token), cancellationToken);
        }
        catch (GitProviderException ex)
        {
            // An invalid replacement leaves the active credential and its health untouched.
            await RecordFailureAsync(connection.Id, userId, GitConnectionAuditEventType.CredentialRotated, ex.Code);
            throw;
        }

        if (!string.Equals(identity.ExternalAccountId, connection.ExternalAccountId, StringComparison.Ordinal))
        {
            await RecordFailureAsync(connection.Id, userId, GitConnectionAuditEventType.CredentialRotated, GitConnectionErrorCodes.AccountMismatch);
            throw new GitConnectionServiceException(GitConnectionErrorCodes.AccountMismatch);
        }

        return identity;
    }

    // ---- shared helpers ----

    private async Task<(GitConnection Connection, GitProviderTarget Target)> OpenForUseAsync(string id, CancellationToken cancellationToken)
    {
        var userId = RequireUserId();
        var connection = await FindActiveAsync(id, track: false, cancellationToken);
        if (!connection.IsEnabled)
        {
            await RecordFailureAsync(connection.Id, userId, GitConnectionAuditEventType.UseDenied, GitConnectionErrorCodes.Disabled);
            throw new GitConnectionServiceException(GitConnectionErrorCodes.Disabled);
        }

        if (!_authorization.CanUse(connection))
        {
            throw new GitConnectionServiceException(GitConnectionErrorCodes.Unauthorized);
        }

        var target = new GitProviderTarget(connection.Id, connection.Provider, connection.NormalizedServerUrl, ReadToken(connection));
        return (connection, target);
    }

    private string ReadToken(GitConnection connection)
    {
        try
        {
            return _protector.Unprotect(connection.ProtectedToken);
        }
        catch (GitConnectionSecretException)
        {
            _logger.LogWarning(
                "Git connection secret cannot be read. ConnectionId: {ConnectionId}", connection.Id);
            throw new GitConnectionServiceException(GitConnectionErrorCodes.SecretUnreadable);
        }
    }

    private async Task<GitConnection> FindActiveAsync(string id, bool track, CancellationToken cancellationToken)
    {
        var query = track ? _context.GitConnections.AsTracking() : _context.GitConnections.AsNoTracking();
        return await query.FirstOrDefaultAsync(connection => connection.Id == id && !connection.IsDeleted, cancellationToken)
               ?? throw new GitConnectionServiceException(GitConnectionErrorCodes.NotFound);
    }

    private async Task RequireMaintainerAsync(GitConnection connection, CancellationToken cancellationToken)
    {
        if (!await _authorization.CanMaintainAsync(connection, cancellationToken))
        {
            throw new GitConnectionServiceException(GitConnectionErrorCodes.MaintenanceForbidden);
        }
    }

    private Task<int> CountRepositoriesAsync(string connectionId, CancellationToken cancellationToken)
        => _context.Repositories.CountAsync(
            repository => repository.GitConnectionId == connectionId && !repository.IsDeleted, cancellationToken);

    /// <summary>
    /// Saves pending changes. A second writer that changed the row first makes the stamp check fail,
    /// which becomes a 409 instead of a silent overwrite.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new GitConnectionServiceException(GitConnectionErrorCodes.Conflict);
        }
    }

    /// <summary>
    /// Records a failed action. It must not hide the original error, so a write problem is only logged.
    /// </summary>
    private async Task RecordFailureAsync(string connectionId, string userId, GitConnectionAuditEventType type, string errorCode)
    {
        var audit = NewAudit(connectionId, userId, type, GitConnectionAuditOutcome.Failure, errorCode);
        _context.GitConnectionAuditEvents.Add(audit);
        try
        {
            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            _context.GitConnectionAuditEvents.Remove(audit);
            _logger.LogWarning(ex, "Git connection audit event could not be written. ConnectionId: {ConnectionId}", connectionId);
        }
    }

    private async Task<GitConnectionResponse> ToResponseAsync(GitConnection connection, int repositoryCount, CancellationToken cancellationToken)
        => new(
            connection.Id,
            connection.Provider.ToString(),
            connection.DisplayName,
            connection.NormalizedServerUrl,
            connection.AccountName ?? string.Empty,
            repositoryCount,
            StateOf(connection),
            connection.IsEnabled,
            connection.LastValidatedAt,
            connection.LastValidationErrorCode,
            connection.CreatedAt,
            connection.CreatedByUserId,
            await _authorization.CanMaintainAsync(connection, cancellationToken),
            !string.IsNullOrEmpty(connection.ProtectedToken));

    private static string StateOf(GitConnection connection)
        => !connection.IsEnabled ? "Disabled" : connection.LastValidationErrorCode is null ? "Healthy" : "Warning";

    private static void Touch(GitConnection connection)
    {
        connection.UpdateTimestamp();
        connection.ConcurrencyStamp = NewStamp();
    }

    private static string NewStamp() => Guid.NewGuid().ToString();

    private GitConnectionAuditEvent NewAudit(
        string connectionId, string? userId, GitConnectionAuditEventType type, GitConnectionAuditOutcome outcome, string? errorCode)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            GitConnectionId = connectionId,
            ActorUserId = userId,
            EventType = type,
            Outcome = outcome,
            ErrorCode = errorCode,
            CorrelationId = Activity.Current?.TraceId.ToString()
        };

    private string RequireUserId()
    {
        var userId = _userContext.UserId;
        return _userContext.IsAuthenticated && !string.IsNullOrWhiteSpace(userId)
            ? userId
            : throw new GitConnectionServiceException(GitConnectionErrorCodes.Unauthorized);
    }

    // ---- input validation ----

    private static GitProvider ParseProvider(string? value)
    {
        // Digits are refused so a numeric enum value cannot select a provider.
        if (!string.IsNullOrWhiteSpace(value)
            && !value.Any(char.IsDigit)
            && Enum.TryParse<GitProvider>(value.Trim(), ignoreCase: true, out var provider)
            && Enum.IsDefined(provider))
        {
            return provider;
        }

        throw new GitConnectionServiceException(GitConnectionErrorCodes.InvalidProvider);
    }

    private static string ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.Length > MaxTokenLength
            || token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw new GitConnectionServiceException(GitConnectionErrorCodes.InvalidToken);
        }

        return token;
    }

    private static string? ValidateDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var trimmed = displayName.Trim();
        return trimmed.Length > MaxDisplayNameLength || trimmed.Any(char.IsControl)
            ? throw new GitConnectionServiceException(GitConnectionErrorCodes.InvalidDisplayName)
            : trimmed;
    }

    private string ResolveServerUrl(GitProvider provider, string? serverUrl)
    {
        var blank = string.IsNullOrWhiteSpace(serverUrl);
        return provider switch
        {
            GitProvider.GitHub => blank || _urlValidator.Normalize(serverUrl) == GitHubServerUrl
                ? GitHubServerUrl
                : throw new GitProviderException(GitProviderErrorCodes.ServerUrlInvalid),
            GitProvider.GitLab => blank ? GitLabComServerUrl : _urlValidator.Normalize(serverUrl),
            _ => throw new GitProviderException(GitProviderErrorCodes.UnsupportedProvider)
        };
    }

    private static string DefaultDisplayName(string accountName, string serverUrl)
    {
        var host = new Uri(serverUrl).Authority;
        var isSaaS = serverUrl is GitHubServerUrl or GitLabComServerUrl;
        return Truncate(isSaaS ? accountName : $"{accountName} ({host})", MaxDisplayNameLength);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
