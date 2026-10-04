using Microsoft.AspNetCore.Http;

namespace OpenDeepWiki.Services.Repositories;

/// <summary>
/// Stable codes for repository requests that name a credential or a Git connection in a way that is not allowed.
/// They are safe to return to clients: they never contain a token or a URL.
/// </summary>
public static class RepositoryConnectionErrorCodes
{
    public const string LegacyCredentialFieldsRejected = "LEGACY_CREDENTIAL_FIELDS_REJECTED";
    public const string GitUrlContainsCredentials = "GIT_URL_CONTAINS_CREDENTIALS";
    public const string ConnectionNotFound = "CONNECTION_NOT_FOUND";
    public const string ConnectionDisabled = "CONNECTION_DISABLED";
    public const string ConnectionHostMismatch = "CONNECTION_HOST_MISMATCH";
}

/// <summary>
/// A submit or update request was refused because of its credential fields or its connection. The route returns 400
/// with the stable code. The message names the replacement and never repeats what the caller sent.
/// </summary>
public sealed class RepositoryConnectionRequestException : InvalidOperationException
{
    public RepositoryConnectionRequestException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }

    public static RepositoryConnectionRequestException LegacyCredentialFields()
        => new(
            RepositoryConnectionErrorCodes.LegacyCredentialFieldsRejected,
            "Repository credentials are no longer accepted. Create a Git connection and send its ID in GitConnectionId instead.");

    public static RepositoryConnectionRequestException UrlWithCredentials()
        => new(
            RepositoryConnectionErrorCodes.GitUrlContainsCredentials,
            "The Git URL must not contain a user name or a token. Create a Git connection and send its ID in GitConnectionId instead.");

    public static RepositoryConnectionRequestException ConnectionNotFound()
        => new(RepositoryConnectionErrorCodes.ConnectionNotFound, "The Git connection does not exist.");

    public static RepositoryConnectionRequestException ConnectionDisabled()
        => new(RepositoryConnectionErrorCodes.ConnectionDisabled, "The Git connection is disabled.");

    public static RepositoryConnectionRequestException ConnectionHostMismatch()
        => new(RepositoryConnectionErrorCodes.ConnectionHostMismatch, "The Git connection belongs to another Git server than the repository.");
}

public static class RepositoryConnectionRequestErrorExtensions
{
    /// <summary>
    /// Turns <see cref="RepositoryConnectionRequestException"/> into a 400 JSON answer with the stable code.
    /// Other exceptions are not touched.
    /// </summary>
    public static IApplicationBuilder UseRepositoryConnectionRequestErrors(this IApplicationBuilder app)
        => app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (RepositoryConnectionRequestException ex) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { success = false, errorCode = ex.ErrorCode, message = ex.Message });
            }
        });
}
