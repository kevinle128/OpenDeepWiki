using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.Auth;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Services.GitConnections;

public static class LegacyGitCredentialMigrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the backfill service. It is a scoped service that an Admin request starts. No hosted service
    /// or startup task calls it, because the backfill sends tokens to GitHub and GitLab.
    /// </summary>
    public static IServiceCollection AddLegacyGitCredentialMigration(this IServiceCollection services)
        => services.AddScoped<ILegacyGitCredentialMigrationService, LegacyGitCredentialMigrationService>();
}

/// <summary>
/// Moves legacy repository credentials into shared connections. The legacy fields are only the source here:
/// they are never changed, so the older release still works until the contract step.
/// The credential resolver stays the only place that reads them to authenticate a Git operation.
/// </summary>
public sealed class LegacyGitCredentialMigrationService(
    IContext context,
    IUserContext userContext,
    IGitConnectionAuthorizationService authorization,
    IGitConnectionSecretProtector protector,
    IGitProviderClientResolver clients,
    IGitCredentialResolver credentialResolver,
    IConnectedRepositoryService repositoryLinker,
    ILogger<LegacyGitCredentialMigrationService> logger) : ILegacyGitCredentialMigrationService
{
    private const string GitHubServerUrl = "https://github.com";
    private const string GitLabComServerUrl = "https://gitlab.com";
    private const string NoneProvider = "None";
    private const string NotMigratedCode = "NOT_MIGRATED";
    private const int MaxTokenLength = 4096;
    private const int MaxAccountNameLength = 200;
    private const int MaxDisplayNameLength = 200;
    private const int MaxRepairQueueItems = 200;
    private const int ScanPageSize = 200;
    private const string LocalSourcePrefix = "local::";
    private const string ArchiveSourcePrefix = "archive::";

    private static readonly HashSet<string> TransientProviderCodes =
    [
        GitProviderErrorCodes.RateLimited,
        GitProviderErrorCodes.Unavailable,
        GitProviderErrorCodes.Timeout,
        GitProviderErrorCodes.DnsFailure
    ];

    // ---- reports ----

    public async Task<LegacyCredentialReport> DryRunAsync(CancellationToken cancellationToken)
    {
        await RequireAdminAsync(cancellationToken);
        return await BuildReportAsync(includeRepairQueue: false, cancellationToken);
    }

    public async Task<LegacyCredentialReport> GetStatusAsync(CancellationToken cancellationToken)
    {
        await RequireAdminAsync(cancellationToken);
        return await BuildReportAsync(includeRepairQueue: true, cancellationToken);
    }

    private async Task<LegacyCredentialReport> BuildReportAsync(bool includeRepairQueue, CancellationToken cancellationToken)
    {
        var total = await context.Repositories.AsNoTracking().CountAsync(item => !item.IsDeleted, cancellationToken);
        var relevant = await RelevantRepositories()
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var records = (await context.GitCredentialMigrationRecords.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(item => item.RepositoryId);

        var entries = new List<ReportEntry>();
        foreach (var repository in relevant)
        {
            var classification = Classify(repository);
            if (classification.Kind == ItemKind.NotRequired)
            {
                continue;
            }

            records.TryGetValue(repository.Id, out var record);
            entries.Add(ToEntry(repository, classification, record));
        }

        var groups = entries
            .GroupBy(item => (item.Provider, item.State, item.ErrorCode))
            .OrderBy(group => group.Key.Provider, StringComparer.Ordinal)
            .ThenBy(group => group.Key.State, StringComparer.Ordinal)
            .ThenBy(group => group.Key.ErrorCode, StringComparer.Ordinal)
            .Select(group => new LegacyCredentialGroup(group.Key.Provider, group.Key.State, group.Key.ErrorCode, group.Count()))
            .ToList();

        var blockers = entries
            .Where(item => item.State != LegacyCredentialStates.Migrated)
            .GroupBy(item => item.ErrorCode ?? NotMigratedCode)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new LegacyCredentialBlocker(group.Key, group.Count()))
            .ToList();

        var queue = includeRepairQueue
            ? entries
                .Where(item => item.State is LegacyCredentialStates.Failed or LegacyCredentialStates.Blocked)
                .OrderBy(item => item.State, StringComparer.Ordinal)
                .ThenBy(item => item.RepositoryId, StringComparer.Ordinal)
                .Take(MaxRepairQueueItems)
                .Select(item => new LegacyCredentialItem(
                    item.RepositoryId, item.Provider, item.State, item.ErrorCode,
                    item.Record?.GitConnectionId, item.Record?.AttemptCount ?? 0, item.Record?.LastAttemptAt))
                .ToList()
            : [];

        return new LegacyCredentialReport(
            Math.Max(0, total - entries.Count),
            groups,
            queue,
            blockers,
            blockers.Count == 0);
    }

    private static ReportEntry ToEntry(Repository repository, Classification classification, GitCredentialMigrationRecord? record)
    {
        if (record is { State: GitCredentialMigrationState.Migrated } && !string.IsNullOrWhiteSpace(repository.GitConnectionId))
        {
            return new ReportEntry(repository.Id, classification.Provider, LegacyCredentialStates.Migrated, null, record);
        }

        // A structural problem is read from the repository again, so a repaired URL or credential leaves the repair queue.
        if (classification.Kind == ItemKind.Blocked)
        {
            return new ReportEntry(repository.Id, classification.Provider, LegacyCredentialStates.Blocked, classification.ErrorCode, record);
        }

        if (record is not null && record.State != GitCredentialMigrationState.Migrated)
        {
            return new ReportEntry(repository.Id, classification.Provider, record.State.ToString(), record.ErrorCode, record);
        }

        return new ReportEntry(repository.Id, classification.Provider, LegacyCredentialStates.Pending, null, record);
    }

    // ---- migration ----

    public async Task<LegacyCredentialMigrationResult> MigrateAsync(
        LegacyCredentialMigrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actorId = await RequireAdminAsync(cancellationToken);
        if (request.BatchSize is < 1 or > LegacyCredentialMigrationRequest.MaxBatchSize)
        {
            throw new LegacyCredentialMigrationException(LegacyCredentialErrorCodes.InvalidRequest);
        }

        // One global order (earliest repository first) makes the creator of a new connection independent of
        // batch size and query order: the first repository of an external account that is processed is its earliest.
        var batch = await SelectNextBatchAsync(request.BatchSize + 1, cancellationToken);
        var hasMore = batch.Count > request.BatchSize;
        return await ProcessAsync(batch.Take(request.BatchSize).ToList(), actorId, hasMore, cancellationToken);
    }

    public async Task<LegacyCredentialMigrationResult> RetryAsync(
        IReadOnlyList<string> repositoryIds, CancellationToken cancellationToken)
    {
        var actorId = await RequireAdminAsync(cancellationToken);
        var ids = repositoryIds?.Select(id => id?.Trim() ?? string.Empty).Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (ids.Count is < 1 or > LegacyCredentialMigrationRequest.MaxBatchSize
            || ids.Any(id => id.Length is 0 or > 36 || id.Any(char.IsControl)))
        {
            throw new LegacyCredentialMigrationException(LegacyCredentialErrorCodes.InvalidRequest);
        }

        var repositories = await context.Repositories.AsNoTracking()
            .Where(item => !item.IsDeleted && ids.Contains(item.Id))
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var migrated = (await context.GitCredentialMigrationRecords.AsNoTracking()
                .Where(item => ids.Contains(item.RepositoryId) && item.State == GitCredentialMigrationState.Migrated)
                .Select(item => item.RepositoryId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var items = repositories
            .Where(item => !migrated.Contains(item.Id) || string.IsNullOrWhiteSpace(item.GitConnectionId))
            .Select(item => new BatchItem(item, Classify(item)))
            .Where(item => item.Classification.Kind != ItemKind.NotRequired)
            .ToList();
        return await ProcessAsync(items, actorId, hasMore: false, cancellationToken);
    }

    /// <summary>
    /// Repositories that have no stored result yet and need work, earliest first. It reads the repositories page by
    /// page, so a long run of repositories that need nothing cannot hide the ones that do.
    /// </summary>
    private async Task<List<BatchItem>> SelectNextBatchAsync(int take, CancellationToken cancellationToken)
    {
        var selected = new List<BatchItem>();
        for (var skip = 0; selected.Count < take; skip += ScanPageSize)
        {
            var page = await RelevantRepositories()
                .Where(item => !context.GitCredentialMigrationRecords.Any(record => record.RepositoryId == item.Id))
                .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
                .Skip(skip).Take(ScanPageSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var repository in page)
            {
                var classification = Classify(repository);
                if (classification.Kind != ItemKind.NotRequired)
                {
                    selected.Add(new BatchItem(repository, classification));
                    if (selected.Count == take)
                    {
                        break;
                    }
                }
            }

            if (page.Count < ScanPageSize)
            {
                break;
            }
        }

        return selected;
    }

    /// <summary>
    /// Active Git repositories that hold a legacy credential or might hold one in their URL.
    /// </summary>
    private IQueryable<Repository> RelevantRepositories()
        => context.Repositories.AsNoTracking()
            .Where(item => !item.IsDeleted
                           && !item.GitUrl.StartsWith(LocalSourcePrefix)
                           && !item.GitUrl.StartsWith(ArchiveSourcePrefix)
                           && ((item.AuthPassword != null && item.AuthPassword != string.Empty)
                               || (item.AuthAccount != null && item.AuthAccount != string.Empty)
                               || item.GitUrl.Contains("@")));

    private async Task<LegacyCredentialMigrationResult> ProcessAsync(
        IReadOnlyList<BatchItem> items, string actorId, bool hasMore, CancellationToken cancellationToken)
    {
        var run = new RunState(actorId);
        int migrated = 0, failed = 0, blocked = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EfContextTransaction.ClearPendingChanges(context);

            var outcome = await ProcessOneAsync(item, run, cancellationToken);
            switch (outcome.State)
            {
                case GitCredentialMigrationState.Migrated:
                    migrated++;
                    break;
                case GitCredentialMigrationState.Failed:
                    failed++;
                    break;
                default:
                    blocked++;
                    break;
            }
        }

        return new LegacyCredentialMigrationResult(items.Count, migrated, failed, blocked, hasMore);
    }

    private async Task<Outcome> ProcessOneAsync(BatchItem item, RunState run, CancellationToken cancellationToken)
    {
        var repository = item.Repository;
        Outcome outcome;
        try
        {
            outcome = item.Classification.Kind switch
            {
                ItemKind.Blocked => Outcome.Blocked(item.Classification.ErrorCode!),
                ItemKind.Adopted => await VerifyAsync(repository.Id, cancellationToken),
                _ => await MigrateRepositoryAsync(repository, item.Classification, run, cancellationToken)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GitProviderException ex)
        {
            outcome = TransientProviderCodes.Contains(ex.Code) ? Outcome.Failed(ex.Code) : Outcome.Blocked(ex.Code);
        }
        catch (Exception ex)
        {
            // The type is enough to find the cause. The message could carry text that the migration must not copy.
            logger.LogWarning(
                "Legacy credential migration failed unexpectedly. RepositoryId: {RepositoryId}, ExceptionType: {ExceptionType}",
                repository.Id, ex.GetType().Name);
            outcome = Outcome.Failed(LegacyCredentialErrorCodes.UnexpectedError);
        }

        EfContextTransaction.ClearPendingChanges(context);
        await SaveRecordAsync(repository.Id, outcome, cancellationToken);
        logger.LogInformation(
            "Legacy credential migration result. RepositoryId: {RepositoryId}, ConnectionId: {ConnectionId}, State: {State}, ErrorCode: {ErrorCode}",
            repository.Id, outcome.ConnectionId, outcome.State, outcome.ErrorCode);
        return outcome;
    }

    private async Task<Outcome> MigrateRepositoryAsync(
        Repository repository, Classification classification, RunState run, CancellationToken cancellationToken)
    {
        var provider = classification.ProviderKind!.Value;
        var serverUrl = classification.ServerUrl!;
        var token = repository.AuthPassword!;

        var identity = await run.ValidateAsync(
            provider, serverUrl, token,
            () => clients.Resolve(provider).ValidateAsync(new GitProviderTarget(null, provider, serverUrl, token), cancellationToken));

        var connection = await FindOrCreateConnectionAsync(provider, serverUrl, identity, token, repository, run, cancellationToken);
        if (connection.Stop is { } stop)
        {
            return stop;
        }

        try
        {
            await repositoryLinker.AdoptLegacyRepositoryAsync(repository.Id, connection.Connection!.Id, cancellationToken);
        }
        catch (ConnectedRepositoryException ex)
        {
            return MapLinkFailure(ex.ErrorCode);
        }

        return await VerifyAsync(repository.Id, cancellationToken);
    }

    /// <summary>
    /// The connection of an external account. An existing connection is reused as it is: its creator and its
    /// credential stay. A soft-deleted one is not revived here, because restoring a shared credential needs a person.
    /// </summary>
    /// <returns>
    /// A stop outcome when the identity belongs to a soft-deleted connection, or when an earlier repository of the
    /// same provider and server still waits for a retry and so must create the connection first.
    /// </returns>
    private async Task<ConnectionLookup> FindOrCreateConnectionAsync(
        GitProvider provider, string serverUrl, GitProviderIdentity identity, string token, Repository repository,
        RunState run, CancellationToken cancellationToken)
    {
        var ownerUserId = repository.OwnerUserId;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = await context.GitConnections.AsNoTracking().FirstOrDefaultAsync(
                item => item.Provider == provider
                        && item.NormalizedServerUrl == serverUrl
                        && item.ExternalAccountId == identity.ExternalAccountId,
                cancellationToken);
            if (existing is not null)
            {
                return existing.IsDeleted
                    ? new ConnectionLookup(null, Outcome.Blocked(LegacyCredentialErrorCodes.ConnectionDeleted))
                    : new ConnectionLookup(existing, null);
            }

            // The creator of a new connection is the owner of the earliest repository. While that repository
            // can still be retried, a later repository waits, so a temporary error cannot change the creator.
            if (await HasEarlierRetryableRepositoryAsync(repository, provider, serverUrl, cancellationToken))
            {
                return new ConnectionLookup(null, Outcome.Failed(LegacyCredentialErrorCodes.EarlierRepositoryPending));
            }

            var accountName = Truncate(identity.AccountName, MaxAccountNameLength);
            var created = new GitConnection
            {
                // The GUID text fits the 36-character columns that reference this ID.
                Id = Guid.NewGuid().ToString(),
                Provider = provider,
                NormalizedServerUrl = serverUrl,
                ExternalAccountId = identity.ExternalAccountId,
                AccountName = accountName,
                DisplayName = string.IsNullOrWhiteSpace(accountName)
                    ? Truncate($"{provider} account {identity.ExternalAccountId}", MaxDisplayNameLength)
                    : accountName,
                ProtectedToken = protector.Protect(token),
                CreatedByUserId = ownerUserId,
                IsEnabled = true,
                LastValidatedAt = DateTime.UtcNow,
                ConcurrencyStamp = Guid.NewGuid().ToString()
            };
            context.GitConnections.Add(created);
            context.GitConnectionAuditEvents.Add(new GitConnectionAuditEvent
            {
                Id = Guid.NewGuid().ToString(),
                GitConnectionId = created.Id,
                ActorUserId = run.ActorId,
                EventType = GitConnectionAuditEventType.Created,
                Outcome = GitConnectionAuditOutcome.Success,
                CorrelationId = Activity.Current?.TraceId.ToString()
            });

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return new ConnectionLookup(created, null);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // A concurrent run won the unique identity index, so the next attempt reads its connection.
                EfContextTransaction.ClearPendingChanges(context);
            }
        }

        throw new LegacyCredentialMigrationException(LegacyCredentialErrorCodes.ConnectionConflict);
    }

    private async Task<bool> HasEarlierRetryableRepositoryAsync(
        Repository repository, GitProvider provider, string serverUrl, CancellationToken cancellationToken)
    {
        var earlier = await context.Repositories.AsNoTracking()
            .Where(item => !item.IsDeleted
                           && item.Id != repository.Id
                           && (item.CreatedAt < repository.CreatedAt
                               || (item.CreatedAt == repository.CreatedAt && string.Compare(item.Id, repository.Id) < 0))
                           && context.GitCredentialMigrationRecords.Any(record =>
                               record.RepositoryId == item.Id && record.State == GitCredentialMigrationState.Failed))
            .ToListAsync(cancellationToken);
        return earlier.Any(item =>
        {
            var classification = Classify(item);
            return classification.Kind == ItemKind.Candidate
                   && classification.ProviderKind == provider
                   && classification.ServerUrl == serverUrl;
        });
    }

    /// <summary>
    /// Proves that the repository now reads its credential through the connection and may send it to its origin.
    /// The credential resolver is the only reader, and it never falls back to the legacy fields for a repository
    /// that has a connection.
    /// </summary>
    private async Task<Outcome> VerifyAsync(string repositoryId, CancellationToken cancellationToken)
    {
        var repository = await context.Repositories.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == repositoryId && !item.IsDeleted, cancellationToken);
        if (repository is null || string.IsNullOrWhiteSpace(repository.GitConnectionId))
        {
            return Outcome.Failed(LegacyCredentialErrorCodes.VerificationFailed);
        }

        try
        {
            if (await credentialResolver.ResolveAsync(repository, cancellationToken) is null)
            {
                return Outcome.Failed(LegacyCredentialErrorCodes.VerificationFailed);
            }
        }
        catch (GitCredentialResolutionException ex)
        {
            return Outcome.Blocked(MapResolutionFailure(ex.ErrorCode));
        }

        // Clone, fetch, and remote lookup refuse a connection repository without a matching recorded origin.
        if (string.IsNullOrWhiteSpace(repository.ProviderBaseUrl)
            || !GitRemoteOriginGuard.IsSameOrigin(repository.GitUrl, repository.ProviderBaseUrl))
        {
            return Outcome.Blocked(LegacyCredentialErrorCodes.RemoteOriginMismatch);
        }

        return Outcome.Migrated(repository.GitConnectionId);
    }

    private static Outcome MapLinkFailure(string code) => code switch
    {
        ConnectedRepositoryErrorCodes.ConnectionDisabled => Outcome.Blocked(LegacyCredentialErrorCodes.ConnectionDisabled),
        ConnectedRepositoryErrorCodes.ConnectionNotFound => Outcome.Blocked(LegacyCredentialErrorCodes.ConnectionNotFound),
        ConnectedRepositoryErrorCodes.ConnectionSecretUnreadable => Outcome.Blocked(LegacyCredentialErrorCodes.ConnectionSecretUnreadable),
        ConnectedRepositoryErrorCodes.RemoteInvalid => Outcome.Blocked(LegacyCredentialErrorCodes.RemoteOriginMismatch),
        _ => Outcome.Failed(LegacyCredentialErrorCodes.VerificationFailed)
    };

    private static string MapResolutionFailure(string code) => code switch
    {
        GitCredentialErrorCodes.ConnectionDisabled => LegacyCredentialErrorCodes.ConnectionDisabled,
        GitCredentialErrorCodes.ConnectionNotFound => LegacyCredentialErrorCodes.ConnectionNotFound,
        GitCredentialErrorCodes.SecretUnreadable => LegacyCredentialErrorCodes.ConnectionSecretUnreadable,
        _ => LegacyCredentialErrorCodes.VerificationFailed
    };

    // ---- progress records ----

    private async Task SaveRecordAsync(string repositoryId, Outcome outcome, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = DateTime.UtcNow;
            var record = await context.GitCredentialMigrationRecords
                .FirstOrDefaultAsync(item => item.RepositoryId == repositoryId, cancellationToken);
            if (record is null)
            {
                record = new GitCredentialMigrationRecord
                {
                    Id = Guid.NewGuid().ToString(),
                    RepositoryId = repositoryId,
                    CreatedAt = now
                };
                context.GitCredentialMigrationRecords.Add(record);
            }

            record.State = outcome.State;
            record.ErrorCode = outcome.ErrorCode;
            record.GitConnectionId = outcome.ConnectionId ?? record.GitConnectionId;
            record.AttemptCount++;
            record.LastAttemptAt = now;
            record.CompletedAt = outcome.State == GitCredentialMigrationState.Migrated ? now : null;

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // A concurrent run stored the first record of this repository, so the next attempt updates it.
                EfContextTransaction.ClearPendingChanges(context);
            }
        }
    }

    // ---- classification ----

    private enum ItemKind
    {
        NotRequired,
        Candidate,
        Adopted,
        Blocked
    }

    private sealed record Classification(
        ItemKind Kind, string Provider, string? ErrorCode, GitProvider? ProviderKind, string? ServerUrl);

    private sealed record ConnectionLookup(GitConnection? Connection, Outcome? Stop);

    private sealed record BatchItem(Repository Repository, Classification Classification);

    private sealed record ReportEntry(
        string RepositoryId, string Provider, string State, string? ErrorCode, GitCredentialMigrationRecord? Record);

    /// <summary>
    /// Decides what a repository needs. It reads the legacy fields only to find out whether they are filled:
    /// the result never carries their content, and the Git URL is never copied out.
    /// </summary>
    private static Classification Classify(Repository repository)
    {
        if (!RepositorySource.IsGit(repository.GitUrl))
        {
            return new Classification(ItemKind.NotRequired, NoneProvider, null, null, null);
        }

        var parsed = Uri.TryCreate(repository.GitUrl, UriKind.Absolute, out var uri) ? uri : null;
        var label = ProviderLabel(parsed);
        if (!string.IsNullOrEmpty(parsed?.UserInfo))
        {
            return new Classification(ItemKind.Blocked, label, LegacyCredentialErrorCodes.UrlContainsUserInfo, null, null);
        }

        var hasPassword = !string.IsNullOrWhiteSpace(repository.AuthPassword);
        var hasAccount = !string.IsNullOrWhiteSpace(repository.AuthAccount);
        if (!hasPassword && !hasAccount)
        {
            return new Classification(ItemKind.NotRequired, NoneProvider, null, null, null);
        }

        if (!string.IsNullOrWhiteSpace(repository.GitConnectionId))
        {
            return new Classification(ItemKind.Adopted, label, null, null, null);
        }

        var (providerKind, serverUrl) = InferProvider(parsed);
        if (providerKind is null)
        {
            return new Classification(ItemKind.Blocked, label, LegacyCredentialErrorCodes.UnsupportedHost, null, null);
        }

        if (!hasPassword)
        {
            return new Classification(ItemKind.Blocked, label, LegacyCredentialErrorCodes.CredentialIncomplete, providerKind, serverUrl);
        }

        if (!IsValidTokenShape(repository.AuthPassword!))
        {
            return new Classification(ItemKind.Blocked, label, LegacyCredentialErrorCodes.CredentialInvalid, providerKind, serverUrl);
        }

        return new Classification(ItemKind.Candidate, label, null, providerKind, serverUrl);
    }

    /// <summary>
    /// Only the two public SaaS hosts can be inferred from a URL. A token is never sent to a host that is guessed:
    /// that would hand a secret to an unverified server and skip the address policy.
    /// </summary>
    private static (GitProvider? Provider, string? ServerUrl) InferProvider(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
        {
            return (null, null);
        }

        return uri.IdnHost.ToLowerInvariant() switch
        {
            "github.com" => (GitProvider.GitHub, GitHubServerUrl),
            "gitlab.com" => (GitProvider.GitLab, GitLabComServerUrl),
            _ => (null, null)
        };
    }

    private static string ProviderLabel(Uri? uri)
    {
        var host = uri?.IdnHost.ToLowerInvariant();
        return host switch
        {
            "github.com" => "GitHub",
            "gitlab.com" => "GitLab",
            "gitee.com" => "Gitee",
            _ when host is not null && host.EndsWith(".gitee.com", StringComparison.Ordinal) => "Gitee",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Same shape rules as a token that a user types into a new connection.
    /// </summary>
    private static bool IsValidTokenShape(string token)
        => token.Length <= MaxTokenLength && !token.Any(character => char.IsWhiteSpace(character) || char.IsControl(character));

    // ---- helpers ----

    /// <summary>
    /// The caller must be a current database Admin. The route group checks the token claim; this check reads the
    /// database, so an Admin role that was revoked cannot start a backfill with an old token.
    /// </summary>
    private async Task<string> RequireAdminAsync(CancellationToken cancellationToken)
    {
        if (!await authorization.IsAdminAsync(cancellationToken) || string.IsNullOrWhiteSpace(userContext.UserId))
        {
            throw new LegacyCredentialMigrationException(LegacyCredentialErrorCodes.AdminRequired);
        }

        return userContext.UserId;
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private readonly record struct Outcome(GitCredentialMigrationState State, string? ErrorCode, string? ConnectionId)
    {
        public static Outcome Migrated(string connectionId) => new(GitCredentialMigrationState.Migrated, null, connectionId);

        public static Outcome Failed(string code) => new(GitCredentialMigrationState.Failed, code, null);

        public static Outcome Blocked(string code) => new(GitCredentialMigrationState.Blocked, code, null);
    }

    /// <summary>
    /// Memory of one run: each token is validated once, and a failure is remembered too, so a rate-limited
    /// provider is not asked again for every repository that holds the same token.
    /// </summary>
    private sealed class RunState(string actorId)
    {
        private readonly Dictionary<(GitProvider, string, string), (GitProviderIdentity? Identity, GitProviderException? Failure)> _validated = new();

        public string ActorId { get; } = actorId;

        public async Task<GitProviderIdentity> ValidateAsync(
            GitProvider provider, string serverUrl, string token, Func<Task<GitProviderIdentity>> validate)
        {
            var key = (provider, serverUrl, token);
            if (!_validated.TryGetValue(key, out var result))
            {
                try
                {
                    result = (await validate(), null);
                }
                catch (GitProviderException ex)
                {
                    result = (null, ex);
                }

                _validated[key] = result;
            }

            return result.Identity ?? throw result.Failure!;
        }
    }
}
