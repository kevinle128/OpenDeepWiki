using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenDeepWiki.Tests.Endpoints;

/// <summary>
/// Kestrel host on a loopback port with header-based test authentication.
/// The X-Test-User header carries the user ID and X-Test-Role the role. No header means anonymous.
/// Requests go through the real authentication, authorization, and routing pipeline.
/// </summary>
internal sealed class TestEndpointHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private TestEndpointHost(WebApplication app, HttpClient client)
    {
        _app = app;
        _client = client;
    }

    public IServiceProvider Services => _app.Services;

    public static async Task<TestEndpointHost> StartAsync(
        Action<IServiceCollection> configureServices,
        Action<WebApplication> mapEndpoints)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        configureServices(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        mapEndpoints(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestEndpointHost(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    public async Task<TestResponse> SendAsync(string method, string path, string? user, object? body = null, string? role = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
        }

        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonElement parsed = default;
        if (!string.IsNullOrWhiteSpace(text) && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            parsed = JsonDocument.Parse(text).RootElement.Clone();
        }

        return new TestResponse(response.StatusCode, text, parsed);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    internal sealed record TestResponse(HttpStatusCode Status, string Body, JsonElement Json);

    private sealed class HeaderAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-User", out var user) || string.IsNullOrEmpty(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.ToString()) };
            if (Request.Headers.TryGetValue("X-Test-Role", out var role) && !string.IsNullOrEmpty(role))
            {
                claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
            }

            var identity = new ClaimsIdentity(claims, "Test");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}
