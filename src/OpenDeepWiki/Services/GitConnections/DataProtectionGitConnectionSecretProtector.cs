using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// ASP.NET Core Data Protection adapter. Keys live in the application key ring, never in the database.
/// </summary>
public sealed class DataProtectionGitConnectionSecretProtector : IGitConnectionSecretProtector
{
    /// <summary>
    /// Purpose string that isolates Git connection tokens from other protected data.
    /// </summary>
    public const string Purpose = "OpenDeepWiki.GitConnections.Pat.v1";

    private readonly IDataProtector _protector;

    public DataProtectionGitConnectionSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(Purpose);
    }

    public string Protect(string plainToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainToken);
        return _protector.Protect(plainToken);
    }

    public string Unprotect(string protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken))
        {
            throw new GitConnectionSecretException("The protected token is empty.");
        }

        try
        {
            return _protector.Unprotect(protectedToken);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // The inner exception is not kept: its text can echo parts of the payload.
            throw new GitConnectionSecretException("The protected token cannot be read.");
        }
    }
}
