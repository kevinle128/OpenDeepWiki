namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Stable codes of the legacy credential backfill. They are safe to return to clients and to store:
/// they never contain tokens, URLs, or provider response text. Provider failures keep their
/// <see cref="GitProviderErrorCodes"/> value.
/// </summary>
public static class LegacyCredentialErrorCodes
{
    public const string AdminRequired = "ADMIN_REQUIRED";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string UrlContainsUserInfo = "URL_CONTAINS_USERINFO";
    public const string UnsupportedHost = "UNSUPPORTED_HOST";
    public const string CredentialIncomplete = "LEGACY_CREDENTIAL_INCOMPLETE";
    public const string CredentialInvalid = "LEGACY_CREDENTIAL_INVALID";
    public const string ConnectionDeleted = "CONNECTION_DELETED";
    public const string ConnectionDisabled = "CONNECTION_DISABLED";
    public const string ConnectionNotFound = "CONNECTION_NOT_FOUND";
    public const string ConnectionSecretUnreadable = "CONNECTION_SECRET_UNREADABLE";
    public const string RemoteOriginMismatch = "REMOTE_ORIGIN_MISMATCH";
    public const string VerificationFailed = "VERIFICATION_FAILED";
    public const string ConnectionConflict = "CONNECTION_CONFLICT";
    public const string EarlierRepositoryPending = "EARLIER_REPOSITORY_PENDING";
    public const string UnexpectedError = "UNEXPECTED_ERROR";
}

/// <summary>
/// Labels of the states that reports use. <see cref="Pending"/> has no stored record yet.
/// </summary>
public static class LegacyCredentialStates
{
    public const string Pending = "Pending";
    public const string Migrated = "Migrated";
    public const string Failed = "Failed";
    public const string Blocked = "Blocked";
}

/// <summary>
/// A migration operation failed before any repository was touched. The code is stable and safe to return.
/// </summary>
public sealed class LegacyCredentialMigrationException : Exception
{
    public LegacyCredentialMigrationException(string errorCode)
        : base($"Legacy credential migration request failed: {errorCode}.")
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

/// <summary>
/// Number of repositories with the same provider, state, and error code. A repository appears in one group.
/// </summary>
public sealed record LegacyCredentialGroup(string Provider, string State, string? ErrorCode, int Count);

/// <summary>
/// One repository that needs attention. It carries identifiers and codes only: never a Git URL or a credential.
/// </summary>
public sealed record LegacyCredentialItem(
    string RepositoryId,
    string Provider,
    string State,
    string? ErrorCode,
    string? GitConnectionId,
    int AttemptCount,
    DateTime? LastAttemptAt);

/// <summary>
/// Reason why the contract release must wait, with the number of repositories behind it.
/// </summary>
public sealed record LegacyCredentialBlocker(string Code, int Count);

/// <summary>
/// Counts, repair queue, and the migration part of the contract gate. The gate does not cover the operator steps:
/// verified backups, the key ring backup, and one full update cycle.
/// </summary>
public sealed record LegacyCredentialReport(
    int NotRequired,
    IReadOnlyList<LegacyCredentialGroup> Groups,
    IReadOnlyList<LegacyCredentialItem> RepairQueue,
    IReadOnlyList<LegacyCredentialBlocker> ContractBlockers,
    bool ContractGateClear);

/// <summary>
/// Number of repositories that one batch handles. The value must be between 1 and 200.
/// </summary>
public sealed record LegacyCredentialMigrationRequest(int BatchSize = LegacyCredentialMigrationRequest.DefaultBatchSize)
{
    public const int DefaultBatchSize = 50;
    public const int MaxBatchSize = 200;
}

public sealed record LegacyCredentialMigrationResult(int Processed, int Migrated, int Failed, int Blocked, bool HasMore);

/// <summary>
/// Moves the plaintext credentials of legacy repositories into shared, protected Git connections.
/// Every operation needs a current database Admin. Nothing runs at application startup.
/// </summary>
public interface ILegacyGitCredentialMigrationService
{
    /// <summary>
    /// Classifies every repository and reports counts, providers, states, and error codes. It writes nothing and
    /// calls no provider.
    /// </summary>
    Task<LegacyCredentialReport> DryRunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Same counts as the dry run, with stored progress and the repair queue.
    /// </summary>
    Task<LegacyCredentialReport> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Migrates the next repositories that have no stored result, in a fixed order. Repeated calls continue
    /// where the last one stopped and never repeat finished work.
    /// </summary>
    Task<LegacyCredentialMigrationResult> MigrateAsync(LegacyCredentialMigrationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the named repositories again, except those that are already migrated.
    /// </summary>
    Task<LegacyCredentialMigrationResult> RetryAsync(IReadOnlyList<string> repositoryIds, CancellationToken cancellationToken);
}
