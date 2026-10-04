using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// Kind of action recorded for a Git connection.
/// </summary>
public enum GitConnectionAuditEventType
{
    Created = 1,
    Validated = 2,
    CredentialRotated = 3,
    Enabled = 4,
    Disabled = 5,
    RepositoryAssigned = 6,
    RepositoryUnassigned = 7,
    Used = 8,
    UseDenied = 9,
    Deleted = 10,
    Restored = 11,
    Renamed = 12,
    BranchAdded = 13,
    BranchRebuildRequested = 14,
    BranchSyncRequested = 15,
    BranchTaskRetried = 16,
    BranchTaskCancelled = 17,
    BranchRemoved = 18
}

/// <summary>
/// Result of the recorded action.
/// </summary>
public enum GitConnectionAuditOutcome
{
    Success = 1,
    Failure = 2
}

/// <summary>
/// Append-only security record for a Git connection.
/// It stores identifiers and stable codes only: never tokens, protected payloads,
/// headers, request bodies, or provider response bodies.
/// </summary>
public class GitConnectionAuditEvent
{
    [Key]
    [StringLength(36)]
    public string Id { get; set; } = string.Empty;

    [Required]
    [StringLength(36)]
    public string GitConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// User who caused the event. Null for background work.
    /// </summary>
    [StringLength(36)]
    public string? ActorUserId { get; set; }

    public GitConnectionAuditEventType EventType { get; set; }

    public GitConnectionAuditOutcome Outcome { get; set; }

    [StringLength(36)]
    public string? RepositoryId { get; set; }

    [StringLength(64)]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Stable error code such as <c>connection_disabled</c>.
    /// </summary>
    [StringLength(64)]
    public string? ErrorCode { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(GitConnectionId))]
    public virtual GitConnection? GitConnection { get; set; }
}
