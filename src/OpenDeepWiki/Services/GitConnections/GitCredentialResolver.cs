using Microsoft.EntityFrameworkCore;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

public sealed class GitCredentialResolver : IGitCredentialResolver
{
    // Providers accept any non-empty user name for token authentication.
    private const string DefaultUsername = "git";

    private readonly IContext _context;
    private readonly IGitConnectionSecretProtector _protector;
    private readonly ILogger<GitCredentialResolver> _logger;

    public GitCredentialResolver(
        IContext context,
        IGitConnectionSecretProtector protector,
        ILogger<GitCredentialResolver> logger)
    {
        _context = context;
        _protector = protector;
        _logger = logger;
    }

    public async Task<GitCredential?> ResolveAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (string.IsNullOrWhiteSpace(repository.GitConnectionId))
        {
            return ResolveLegacyCredential(repository);
        }

        var connection = await _context.GitConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == repository.GitConnectionId && !item.IsDeleted, cancellationToken);
        if (connection is null)
        {
            throw Fail(repository.GitConnectionId, GitCredentialErrorCodes.ConnectionNotFound);
        }

        if (!connection.IsEnabled)
        {
            throw Fail(connection.Id, GitCredentialErrorCodes.ConnectionDisabled);
        }

        try
        {
            var token = _protector.Unprotect(connection.ProtectedToken);
            var username = string.IsNullOrWhiteSpace(connection.AccountName) ? DefaultUsername : connection.AccountName;
            return new GitCredential(username, token);
        }
        catch (GitConnectionSecretException)
        {
            throw Fail(connection.Id, GitCredentialErrorCodes.SecretUnreadable);
        }
    }

    public async Task<bool> HasUsableCredentialAsync(Repository repository, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        if (string.IsNullOrWhiteSpace(repository.GitConnectionId))
        {
            return !string.IsNullOrWhiteSpace(repository.AuthPassword);
        }

        try
        {
            return await ResolveAsync(repository, cancellationToken) is not null;
        }
        catch (GitCredentialResolutionException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the repository has a connection or a legacy password. It reads no secret and does not check that
    /// the connection works. List views use it; Git operations and private visibility use
    /// <see cref="HasUsableCredentialAsync"/>.
    /// </summary>
    public static bool HasStoredCredential(Repository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return !string.IsNullOrWhiteSpace(repository.GitConnectionId)
               || !string.IsNullOrWhiteSpace(repository.AuthPassword);
    }

    /// <summary>
    /// Credentials stored directly on the repository by older versions. This is the only code that reads them.
    /// The log line names the repository only, so an operator can see which repositories still depend on them.
    /// </summary>
    private GitCredential? ResolveLegacyCredential(Repository repository)
    {
        if (string.IsNullOrWhiteSpace(repository.AuthAccount)
            && string.IsNullOrWhiteSpace(repository.AuthPassword))
        {
            return null;
        }

        _logger.LogInformation(
            "Legacy repository credential in use. RepositoryId: {RepositoryId}", repository.Id);
        return new GitCredential(repository.AuthAccount ?? string.Empty, repository.AuthPassword ?? string.Empty);
    }

    private GitCredentialResolutionException Fail(string connectionId, string errorCode)
    {
        _logger.LogWarning(
            "Git credential resolution failed. ConnectionId: {ConnectionId}, ErrorCode: {ErrorCode}",
            connectionId, errorCode);
        return new GitCredentialResolutionException(errorCode);
    }
}
