using System.Net;
using System.Net.Sockets;
using OpenDeepWiki.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

public class GitLabServerUrlValidatorTests
{
    private const string PublicIp = "93.184.216.34";

    // ---- URL normalization ----

    [Theory]
    [InlineData("https://gitlab.example.com", "https://gitlab.example.com")]
    [InlineData("HTTPS://GitLab.Example.COM/", "https://gitlab.example.com")]
    [InlineData("https://gitlab.example.com:443", "https://gitlab.example.com")]
    [InlineData("https://gitlab.example.com:8443", "https://gitlab.example.com:8443")]
    [InlineData("  https://gitlab.example.com  ", "https://gitlab.example.com")]
    [InlineData("https://gitlab.example.com.", "https://gitlab.example.com")]
    [InlineData("https://203.0.113.10", "https://203.0.113.10")]
    public void Normalize_WhenOriginIsValid_ReturnsCanonicalOrigin(string input, string expected)
    {
        var validator = GitProviderTestFactory.CreateValidator(new FakeHostResolver());

        Assert.Equal(expected, validator.Normalize(input));
    }

    [Theory]
    [InlineData("http://gitlab.example.com")]
    [InlineData("ftp://gitlab.example.com")]
    [InlineData("gitlab.example.com")]
    [InlineData("https://user@gitlab.example.com")]
    [InlineData("https://user:secret@gitlab.example.com")]
    [InlineData("https://gitlab.example.com/gitlab")]
    [InlineData("https://gitlab.example.com/?a=1")]
    [InlineData("https://gitlab.example.com?a=1")]
    [InlineData("https://gitlab.example.com/#frag")]
    [InlineData("https://gitlab.example.com#frag")]
    [InlineData("https://gitlab.example.com:0")]
    [InlineData("https://gitlab.example.com:70000")]
    [InlineData("https://gitlab.example.com:abc")]
    [InlineData("https://")]
    [InlineData("https://git lab.example.com")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Normalize_WhenOriginIsInvalid_RejectsWithStableCodeAndNoInputEcho(string? input)
    {
        var validator = GitProviderTestFactory.CreateValidator(new FakeHostResolver());

        var exception = Assert.Throws<GitProviderException>(() => validator.Normalize(input));

        Assert.Equal(GitProviderErrorCodes.ServerUrlInvalid, exception.Code);
        Assert.DoesNotContain("secret", exception.Message);
    }

    // ---- address policy ----

    [Theory]
    [InlineData(PublicIp)]
    [InlineData("140.82.112.5")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("192.0.1.1")]
    [InlineData("64:ff9b:1:808:8:800::")]
    [InlineData("::ffff:0:808:808")]
    public async Task ResolveAllowedAddresses_WhenHostResolvesOnlyToPublicAddresses_ReturnsThem(string address)
    {
        var validator = GitProviderTestFactory.CreateValidator(new FakeHostResolver().Add("gitlab.example.com", address));

        var result = await validator.ResolveAllowedAddressesAsync("gitlab.example.com", CancellationToken.None);

        Assert.Equal(IPAddress.Parse(address), Assert.Single(result));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.5.5")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.1.1")]
    [InlineData("fe80::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fc00::1")]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.200")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.0.170")]
    [InlineData("::ffff:192.0.0.9")]
    [InlineData("64:ff9b:1:a00:0:100::")]
    [InlineData("64:ff9b:1:a9fe:a9:fe00::")]
    [InlineData("::ffff:0:a00:1")]
    [InlineData("::ffff:0:a9fe:a9fe")]
    public async Task ResolveAllowedAddresses_WhenAddressIsNotPublic_RejectsByDefault(string address)
    {
        var validator = GitProviderTestFactory.CreateValidator(new FakeHostResolver().Add("gitlab.example.com", address));

        var exception = await Assert.ThrowsAsync<GitProviderException>(
            () => validator.ResolveAllowedAddressesAsync("gitlab.example.com", CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.ServerUrlBlocked, exception.Code);
        Assert.DoesNotContain(address, exception.Message);
    }

    [Fact]
    public async Task ResolveAllowedAddresses_WhenAnswersMixPublicAndPrivate_RejectsTheWholeRequest()
    {
        var validator = GitProviderTestFactory.CreateValidator(
            new FakeHostResolver().Add("gitlab.example.com", PublicIp, "10.0.0.5"));

        var exception = await Assert.ThrowsAsync<GitProviderException>(
            () => validator.ResolveAllowedAddressesAsync("gitlab.example.com", CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.ServerUrlBlocked, exception.Code);
    }

    [Fact]
    public async Task ResolveAllowedAddresses_WhenHostIsLiteralPrivateAddress_RejectsWithoutDns()
    {
        var resolver = new FakeHostResolver();
        var validator = GitProviderTestFactory.CreateValidator(resolver);

        await Assert.ThrowsAsync<GitProviderException>(
            () => validator.ResolveAllowedAddressesAsync("192.168.0.9", CancellationToken.None));
        Assert.Equal(0, resolver.CallCount);
    }

    // ---- operator allowlist ----

    [Fact]
    public async Task ResolveAllowedAddresses_WhenExactPrivateHostIsAllowlisted_AllowsItsPrivateAddresses()
    {
        var validator = GitProviderTestFactory.CreateValidator(
            new FakeHostResolver().Add("git.corp.example", "10.1.2.3"),
            allowedHosts: ["GIT.corp.example"]);

        var result = await validator.ResolveAllowedAddressesAsync("git.corp.example", CancellationToken.None);

        Assert.Equal(IPAddress.Parse("10.1.2.3"), Assert.Single(result));
    }

    [Fact]
    public async Task ResolveAllowedAddresses_WhenOnlyAnotherHostIsAllowlisted_StillRejects()
    {
        var validator = GitProviderTestFactory.CreateValidator(
            new FakeHostResolver().Add("evil.example.com", "10.1.2.3"),
            allowedHosts: ["git.corp.example"]);

        await Assert.ThrowsAsync<GitProviderException>(
            () => validator.ResolveAllowedAddressesAsync("evil.example.com", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAllowedAddresses_WhenAddressIsInsideAllowlistedCidr_AllowsOnlyThatRange()
    {
        var resolver = new FakeHostResolver()
            .Add("inside.example", "10.20.1.5")
            .Add("outside.example", "10.30.1.5");
        var validator = GitProviderTestFactory.CreateValidator(resolver, allowedCidrs: ["10.20.0.0/16"]);

        Assert.Single(await validator.ResolveAllowedAddressesAsync("inside.example", CancellationToken.None));
        await Assert.ThrowsAsync<GitProviderException>(
            () => validator.ResolveAllowedAddressesAsync("outside.example", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAllowedAddresses_WhenMixedAnswersAreAllCoveredByAllowlist_Allows()
    {
        var validator = GitProviderTestFactory.CreateValidator(
            new FakeHostResolver().Add("git.corp.example", PublicIp, "10.20.1.5"),
            allowedCidrs: ["10.20.0.0/16"]);

        Assert.Equal(2, (await validator.ResolveAllowedAddressesAsync("git.corp.example", CancellationToken.None)).Count);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("0.0.0.0")]
    [InlineData("100.100.100.200")]
    public async Task ResolveAllowedAddresses_WhenAddressIsLinkLocalOrMetadata_RejectsEvenWhenAllowlisted(string address)
    {
        var validator = GitProviderTestFactory.CreateValidator(
            new FakeHostResolver().Add("git.corp.example", address),
            allowedHosts: ["git.corp.example"],
            allowedCidrs: ["169.254.0.0/16", "100.64.0.0/10", "fe80::/10", "0.0.0.0/8"]);

        await Assert.ThrowsAsync<GitProviderException>(
            () => validator.ResolveAllowedAddressesAsync("git.corp.example", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAllowedAddresses_WhenLoopbackHostIsAllowlisted_AllowsLoopback()
    {
        var validator = GitProviderTestFactory.CreateValidator(
            new FakeHostResolver().Add("localhost", "127.0.0.1"),
            allowedHosts: ["localhost"]);

        Assert.Single(await validator.ResolveAllowedAddressesAsync("localhost", CancellationToken.None));
    }

    [Fact]
    public void Constructor_WhenAllowlistedCidrIsInvalid_FailsLoudly()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => GitProviderTestFactory.CreateValidator(
            new FakeHostResolver(), allowedCidrs: ["10.0.0.0/99"]));

        Assert.Contains("10.0.0.0/99", exception.Message);
    }

    // ---- DNS failures ----

    [Fact]
    public async Task ResolveAllowedAddresses_WhenHostDoesNotResolve_ReturnsDnsFailure()
    {
        var validator = GitProviderTestFactory.CreateValidator(new FakeHostResolver());

        var exception = await Assert.ThrowsAsync<GitProviderException>(
            () => validator.ResolveAllowedAddressesAsync("missing.example.com", CancellationToken.None));

        Assert.Equal(GitProviderErrorCodes.DnsFailure, exception.Code);
    }

    // ---- request-time guard (redirect and rebinding) ----

    [Fact]
    public async Task Guard_WhenHostResolvesPublic_ForwardsRequestWithPinnedAddresses()
    {
        var fake = new FakeProviderHandler().EnqueueJson("{}");
        using var client = GitProviderTestFactory.CreateGuardedClient(
            fake,
            GitProviderTestFactory.CreateValidator(new FakeHostResolver().Add("gitlab.example.com", PublicIp)));

        using var response = await client.SendAsync(
            GuardedRequest("https://gitlab.example.com/api/v4/user", "https://gitlab.example.com"));

        var request = Assert.Single(fake.Requests);
        Assert.Equal(IPAddress.Parse(PublicIp), Assert.Single(request.PinnedAddresses!));
    }

    [Fact]
    public async Task Guard_WhenHostResolvesPrivate_NeverCallsTheProvider()
    {
        var fake = new FakeProviderHandler().EnqueueJson("{}");
        using var client = GitProviderTestFactory.CreateGuardedClient(
            fake,
            GitProviderTestFactory.CreateValidator(new FakeHostResolver().Add("gitlab.example.com", "10.0.0.7")));

        var exception = await Assert.ThrowsAsync<GitProviderException>(() => client.SendAsync(
            GuardedRequest("https://gitlab.example.com/api/v4/user", "https://gitlab.example.com")));

        Assert.Equal(GitProviderErrorCodes.ServerUrlBlocked, exception.Code);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Guard_WhenDnsAnswerChangesToPrivateBetweenRequests_BlocksTheSecondRequest()
    {
        var fake = new FakeProviderHandler().EnqueueJson("{}").EnqueueJson("{}");
        var resolver = new FakeHostResolver()
            .Add("gitlab.example.com", PublicIp)
            .Add("gitlab.example.com", "169.254.169.254");
        using var client = GitProviderTestFactory.CreateGuardedClient(fake, GitProviderTestFactory.CreateValidator(resolver));

        using var first = await client.SendAsync(
            GuardedRequest("https://gitlab.example.com/api/v4/user", "https://gitlab.example.com"));
        await Assert.ThrowsAsync<GitProviderException>(() => client.SendAsync(
            GuardedRequest("https://gitlab.example.com/api/v4/user", "https://gitlab.example.com")));

        Assert.Single(fake.Requests);
    }

    [Theory]
    [InlineData("https://other.example.com/api/v4/user", "https://gitlab.example.com")]
    [InlineData("https://gitlab.example.com:8443/api/v4/user", "https://gitlab.example.com")]
    [InlineData("http://gitlab.example.com/api/v4/user", "https://gitlab.example.com")]
    [InlineData("https://gitlab.example.com/api/v4/user", null)]
    public async Task Guard_WhenRequestLeavesTheAllowedOrigin_NeverCallsTheProvider(string url, string? allowedOrigin)
    {
        var fake = new FakeProviderHandler().EnqueueJson("{}");
        var resolver = new FakeHostResolver()
            .Add("gitlab.example.com", PublicIp)
            .Add("other.example.com", PublicIp);
        using var client = GitProviderTestFactory.CreateGuardedClient(fake, GitProviderTestFactory.CreateValidator(resolver));

        await Assert.ThrowsAsync<GitProviderException>(() => client.SendAsync(GuardedRequest(url, allowedOrigin)));

        Assert.Empty(fake.Requests);
    }

    // ---- primary handler ----

    [Fact]
    public void PrimaryHandler_DisablesRedirectsAndKeepsDefaultCertificateValidation()
    {
        using var handler = GitProviderGuardHandler.CreatePrimaryHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void PrimaryHandler_IgnoresTheEnvironmentProxy()
    {
        using var handler = GitProviderGuardHandler.CreatePrimaryHandler();

        // A proxy would receive the request instead of the pinned address, which defeats the address policy.
        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task ConnectToPinnedAddress_ConnectsToTheValidatedAddress()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();

        await using var stream = await GitProviderGuardHandler.ConnectToPinnedAddressAsync(
            [IPAddress.Loopback], port, CancellationToken.None);
        using var accepted = await accept;

        Assert.True(accepted.Connected);
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task ConnectToPinnedAddress_WhenNoAddressIsPinned_Refuses()
    {
        var exception = await Assert.ThrowsAsync<GitProviderException>(() =>
            GitProviderGuardHandler.ConnectToPinnedAddressAsync([], 443, CancellationToken.None).AsTask());

        Assert.Equal(GitProviderErrorCodes.ServerUrlBlocked, exception.Code);
    }

    private static HttpRequestMessage GuardedRequest(string url, string? allowedOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (allowedOrigin is not null)
        {
            request.Options.Set(GitProviderGuardHandler.AllowedOriginKey, allowedOrigin);
        }

        return request;
    }
}
