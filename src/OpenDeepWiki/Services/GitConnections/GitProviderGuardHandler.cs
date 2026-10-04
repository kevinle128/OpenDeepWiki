using System.Net;
using System.Net.Sockets;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Runs before every GitLab request. It checks that the request stays on the allowed origin,
/// resolves DNS once, rejects the request when any answer violates the address policy, and pins the
/// validated addresses on the request. The primary handler connects only to those addresses, so a
/// later DNS answer cannot redirect the connection (DNS rebinding).
/// </summary>
public sealed class GitProviderGuardHandler : DelegatingHandler
{
    /// <summary>
    /// Origin the request may reach. The provider client sets it on every request.
    /// </summary>
    public static readonly HttpRequestOptionsKey<string> AllowedOriginKey = new("OpenDeepWiki.GitProvider.AllowedOrigin");

    /// <summary>
    /// Addresses that passed the policy for this request.
    /// </summary>
    public static readonly HttpRequestOptionsKey<IReadOnlyList<IPAddress>> PinnedAddressesKey = new("OpenDeepWiki.GitProvider.PinnedAddresses");

    private readonly GitLabServerUrlValidator _validator;

    public GitProviderGuardHandler(GitLabServerUrlValidator validator)
    {
        _validator = validator;
    }

    /// <summary>
    /// Primary handler: no automatic redirects (a redirect target would receive the token), no cookies,
    /// no environment proxy (a proxy would connect instead of the pinned address), default certificate
    /// validation, and connections only to the pinned addresses.
    /// </summary>
    public static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, cancellationToken) =>
        {
            context.InitialRequestMessage.Options.TryGetValue(PinnedAddressesKey, out var addresses);
            return await ConnectToPinnedAddressAsync(addresses ?? [], context.DnsEndPoint.Port, cancellationToken);
        }
    };

    /// <summary>
    /// Primary handler for the fixed GitHub API host: no redirects, no cookies, default certificate validation.
    /// </summary>
    public static SocketsHttpHandler CreateDirectHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null
            || uri.Scheme != Uri.UriSchemeHttps
            || !request.Options.TryGetValue(AllowedOriginKey, out var allowedOrigin)
            || !string.Equals(uri.GetLeftPart(UriPartial.Authority), allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            throw new GitProviderException(GitProviderErrorCodes.ServerUrlBlocked);
        }

        var addresses = await _validator.ResolveAllowedAddressesAsync(uri.IdnHost, cancellationToken);
        request.Options.Set(PinnedAddressesKey, addresses);
        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Connects to the first reachable validated address. TLS runs on top of this stream with the
    /// request host name, so certificate checks are unchanged.
    /// </summary>
    internal static async ValueTask<Stream> ConnectToPinnedAddressAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        if (addresses.Count == 0)
        {
            throw new GitProviderException(GitProviderErrorCodes.ServerUrlBlocked);
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("The validated Git server address is not reachable.", last);
    }
}
