using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenDeepWiki.Entities;

/// <summary>
/// Git hosting provider that a connection authenticates against.
/// </summary>
public enum GitProvider
{
    GitHub = 1,
    GitLab = 2
}

/// <summary>
/// Shared Git connection that stores one protected personal access token.
/// The identity (provider, normalized server URL, external account ID) is globally unique,
/// including soft-deleted rows, so a deleted connection is restored instead of duplicated.
/// </summary>
public class GitConnection : AggregateRoot<string>
{
    /// <summary>
    /// Git hosting provider.
    /// </summary>
    public GitProvider Provider { get; set; }

    /// <summary>
    /// Normalized server URL: scheme and host (and optional base path) without user info, query, or fragment.
    /// </summary>
    [Required]
    [StringLength(500)]
    public string NormalizedServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Stable account ID returned by the provider after the token was validated.
    /// </summary>
    [Required]
    [StringLength(128)]
    public string ExternalAccountId { get; set; } = string.Empty;

    /// <summary>
    /// Name that users see and maintainers can change. This value is not a secret.
    /// </summary>
    [Required]
    [StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Account name used as the HTTPS user name. This value is not a secret.
    /// </summary>
    [StringLength(200)]
    public string? AccountName { get; set; }

    /// <summary>
    /// Token payload produced by the secret protector. The column has no length limit
    /// because the protected form is larger than the plain token.
    /// </summary>
    [Required]
    public string ProtectedToken { get; set; } = string.Empty;

    /// <summary>
    /// Immutable ID of the user who created the connection.
    /// </summary>
    [Required]
    [StringLength(36)]
    public string CreatedByUserId { get; set; } = string.Empty;

    /// <summary>
    /// Disabled connections block discovery, new indexing, and synchronization.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Time of the last successful server-side token validation.
    /// </summary>
    public DateTime? LastValidatedAt { get; set; }

    /// <summary>
    /// Stable error code of the last failed validation. Never contains provider response text.
    /// </summary>
    [StringLength(64)]
    public string? LastValidationErrorCode { get; set; }

    /// <summary>
    /// Explicit concurrency token. The service sets a new value on every change, so a second
    /// writer that read the old row fails on SQLite and PostgreSQL alike. The inherited
    /// <see cref="AggregateRoot{TKey}.Version"/> column is not filled by either provider.
    /// </summary>
    [Required]
    [StringLength(36)]
    [ConcurrencyCheck]
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Creator navigation property.
    /// </summary>
    [ForeignKey(nameof(CreatedByUserId))]
    public virtual User? CreatedBy { get; set; }

    /// <summary>
    /// Restores a soft-deleted connection that matches a new request for the same identity.
    /// </summary>
    public void Restore()
    {
        IsDeleted = false;
        DeletedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }
}
