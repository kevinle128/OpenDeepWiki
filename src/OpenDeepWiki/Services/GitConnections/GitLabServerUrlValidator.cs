using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace OpenDeepWiki.Services.GitConnections;

/// <summary>
/// Resolves host names. A seam so tests never use real DNS.
/// </summary>
public interface IGitHostResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemGitHostResolver : IGitHostResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        => await Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>
/// Normalizes GitLab server origins and decides which resolved addresses a request may reach.
/// Public hosts are allowed by default. Private and loopback addresses need an exact host or CIDR
/// entry in the operator configuration. Link-local, metadata, unspecified, multicast, and reserved
/// addresses are never allowed.
/// </summary>
public sealed class GitLabServerUrlValidator
{
    private static readonly IPAddress AlibabaMetadata = IPAddress.Parse("100.100.100.200");
    private static readonly IPAddress AwsMetadataV6 = IPAddress.Parse("fd00:ec2::254");

    private readonly HashSet<string> _allowedHosts;
    private readonly List<IPNetwork> _allowedNetworks = [];
    private readonly IGitHostResolver _resolver;

    public GitLabServerUrlValidator(IOptions<GitProviderOptions> options, IGitHostResolver resolver)
    {
        _resolver = resolver;
        var settings = options.Value;
        _allowedHosts = settings.AllowedPrivateHosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim().TrimEnd('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var cidr in settings.AllowedPrivateCidrs)
        {
            if (!IPNetwork.TryParse(cidr, out var network))
            {
                throw new InvalidOperationException($"GitProviders allowlist CIDR '{cidr}' is not valid.");
            }

            _allowedNetworks.Add(network);
        }
    }

    /// <summary>
    /// Returns the canonical HTTPS origin (scheme, lower-case host, port only when it is not 443).
    /// </summary>
    public string Normalize(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw Invalid();
        }

        var trimmed = serverUrl.Trim();
        if (trimmed.Any(char.IsWhiteSpace)
            || trimmed.Contains('?')
            || trimmed.Contains('#')
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || string.IsNullOrEmpty(uri.IdnHost)
            || uri.Port is < 1 or > 65535)
        {
            throw Invalid();
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            throw Invalid();
        }

        var authority = uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        // IPv6 literals need brackets in an origin; IdnHost has none for them.
        if (host.Contains(':'))
        {
            authority = uri.IsDefaultPort ? $"[{host}]" : $"[{host}]:{uri.Port}";
        }

        return $"https://{authority}";
    }

    /// <summary>
    /// Resolves all DNS answers and returns them when every one is allowed.
    /// One violating answer rejects the whole request. The caller must connect to the returned
    /// addresses, not resolve again, so the check and the connection use the same answer.
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveAllowedAddressesAsync(string host, CancellationToken cancellationToken)
    {
        var name = host.Trim().TrimEnd('.');
        IReadOnlyList<IPAddress> addresses;
        if (IPAddress.TryParse(name.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolver.ResolveAsync(name, cancellationToken);
            }
            catch (SocketException)
            {
                throw new GitProviderException(GitProviderErrorCodes.DnsFailure);
            }
        }

        if (addresses.Count == 0)
        {
            throw new GitProviderException(GitProviderErrorCodes.DnsFailure);
        }

        var hostAllowed = _allowedHosts.Contains(name);
        foreach (var address in addresses)
        {
            if (!IsAllowed(address, hostAllowed))
            {
                throw new GitProviderException(GitProviderErrorCodes.ServerUrlBlocked);
            }
        }

        return addresses;
    }

    private bool IsAllowed(IPAddress address, bool hostAllowed)
    {
        var effective = Unwrap(address);
        return Classify(effective) switch
        {
            AddressClass.Public => true,
            AddressClass.Private => hostAllowed || _allowedNetworks.Exists(network => network.Contains(effective)),
            _ => false
        };
    }

    /// <summary>
    /// Maps addresses that embed an IPv4 address (mapped, compatible, translated, NAT64, 6to4) to that IPv4 address.
    /// </summary>
    private static IPAddress Unwrap(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();

        // ::a.b.c.d (IPv4-compatible). :: and ::1 are handled by the caller as plain IPv6 addresses.
        var isLoopback = bytes.Take(15).All(value => value == 0) && bytes[15] == 1;
        if (!isLoopback && bytes.Take(12).All(value => value == 0) && bytes.Skip(12).Any(value => value != 0))
        {
            return new IPAddress(bytes.AsSpan(12, 4));
        }

        // 64:ff9b::/96 (NAT64) embeds the IPv4 address in the last four bytes.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && bytes.Skip(4).Take(8).All(value => value == 0))
        {
            return new IPAddress(bytes.AsSpan(12, 4));
        }

        // 64:ff9b:1::/48 (local-use NAT64) embeds the IPv4 address around the reserved byte 8 (RFC 6052, /48 prefix).
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && bytes[4] == 0x00 && bytes[5] == 0x01)
        {
            return new IPAddress([bytes[6], bytes[7], bytes[9], bytes[10]]);
        }

        // ::ffff:0:a.b.c.d (IPv4-translated, SIIT) has the IPv4 address in the last four bytes.
        if (bytes.Take(8).All(value => value == 0)
            && bytes[8] == 0xff && bytes[9] == 0xff && bytes[10] == 0x00 && bytes[11] == 0x00)
        {
            return new IPAddress(bytes.AsSpan(12, 4));
        }

        // 2002::/16 (6to4) embeds the IPv4 address after the prefix.
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return new IPAddress(bytes.AsSpan(2, 4));
        }

        return address;
    }

    private static AddressClass Classify(IPAddress address)
    {
        if (address.Equals(AlibabaMetadata) || address.Equals(AwsMetadataV6))
        {
            return AddressClass.Never;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return ClassifyV4(bytes);
        }

        if (address.Equals(IPAddress.IPv6Any))
        {
            return AddressClass.Never;
        }

        if (address.Equals(IPAddress.IPv6Loopback))
        {
            return AddressClass.Private;
        }

        // fe80::/10 link-local
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
        {
            return AddressClass.Never;
        }

        // ff00::/8 multicast
        if (bytes[0] == 0xff)
        {
            return AddressClass.Never;
        }

        // 2001:0::/32 Teredo can embed any IPv4 address, so it is never trusted.
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return AddressClass.Never;
        }

        // fc00::/7 unique local and fec0::/10 deprecated site-local
        if ((bytes[0] & 0xfe) == 0xfc || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0))
        {
            return AddressClass.Private;
        }

        return AddressClass.Public;
    }

    private static AddressClass ClassifyV4(byte[] bytes)
    {
        var (a, b) = (bytes[0], bytes[1]);
        return (a, b) switch
        {
            (0, _) => AddressClass.Never,
            (169, 254) => AddressClass.Never,
            ( >= 224, _) => AddressClass.Never,
            // 192.0.0.0/24 holds IETF protocol assignments (for example DS-Lite and NAT64 discovery). It is not a public host range.
            (192, 0) when bytes[2] == 0 => AddressClass.Never,
            (10, _) => AddressClass.Private,
            (127, _) => AddressClass.Private,
            (172, >= 16 and <= 31) => AddressClass.Private,
            (192, 168) => AddressClass.Private,
            (100, >= 64 and <= 127) => AddressClass.Private,
            (198, 18 or 19) => AddressClass.Private,
            _ => AddressClass.Public
        };
    }

    private static GitProviderException Invalid() => new(GitProviderErrorCodes.ServerUrlInvalid);

    private enum AddressClass
    {
        Public,
        Private,
        Never
    }
}
