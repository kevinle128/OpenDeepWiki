using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// Outcome of the last attempt to move the legacy credential of one repository into a shared connection.
/// </summary>
public enum GitCredentialMigrationState
{
    /// <summary>
    /// The repository uses a verified connection.
    /// </summary>
    Migrated = 1,

    /// <summary>
    /// A transient problem stopped the attempt, for example a rate limit or a timeout. A retry can succeed.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// An Admin must repair the repository before it can move, for example a revoked token or an unsupported host.
    /// </summary>
    Blocked = 3
}

/// <summary>
/// Durable progress of the legacy credential backfill. One row per repository.
/// It stores identifiers, counters, and stable error codes only: never tokens, protected payloads,
/// Git URLs, or provider response text.
/// </summary>
public class GitCredentialMigrationRecord
{
    [Key]
    [StringLength(36)]
    public string Id { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string RepositoryId { get; set; } = string.Empty;

    public GitCredentialMigrationState State { get; set; }

    /// <summary>
    /// Stable code of the last failure. Null when the repository is migrated.
    /// </summary>
    [StringLength(64)]
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Connection that the repository was assigned to. Not a foreign key: it is a diagnostic value.
    /// </summary>
    [StringLength(36)]
    public string? GitConnectionId { get; set; }

    public int AttemptCount { get; set; }

    public DateTime LastAttemptAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(RepositoryId))]
    public virtual Repository? Repository { get; set; }
}
