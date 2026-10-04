using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace OpenDeepWiki.E2EHost;

/// <summary>
/// Methods and paths that reached the application, newest last. No query string, header or body is stored.
/// </summary>
internal sealed class RequestLog
{
    private const int Capacity = 20_000;
    private readonly ConcurrentQueue<RequestEntry> _entries = new();
    private long _sequence;

    public void Add(string method, string path, int status)
    {
        _entries.Enqueue(new RequestEntry(Interlocked.Increment(ref _sequence), method, path, status));
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<RequestEntry> Snapshot() => _entries.ToArray();

    public sealed record RequestEntry(long Sequence, string Method, string Path, int Status);
}

/// <summary>
/// Adds the request log and the test control routes in front of the application pipeline.
/// The routes exist only in this host and are reached by the test runner, never by the browser.
/// </summary>
internal sealed class E2EControlStartupFilter(
    RequestLog log, FakeGitProviderHandler provider, IConfiguration configuration, string workDir) : IStartupFilter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipelineNext) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (!path.StartsWith("/__e2e/", StringComparison.Ordinal))
            {
                await pipelineNext();
                log.Add(context.Request.Method, path, context.Response.StatusCode);
                return;
            }

            await HandleControlAsync(context, path);
        });

        next(app);
    };

    private async Task HandleControlAsync(HttpContext context, string path)
    {
        var method = context.Request.Method;
        object? result = null;

        switch ((method, path))
        {
            case ("GET", "/__e2e/info"):
                result = new
                {
                    workDir,
                    databasePath = ReadDatabasePath(),
                    unfakedProviderCalls = provider.UnfakedCalls,
                };
                break;
            case ("GET", "/__e2e/requests"):
                result = log.Snapshot();
                break;
            case ("GET", "/__e2e/provider-calls"):
                result = provider.Calls;
                break;
            case ("PUT", "/__e2e/rules"):
            {
                var rules = await JsonSerializer.DeserializeAsync<List<ProviderRule>>(context.Request.Body, JsonOptions) ?? [];
                provider.SetRules(rules);
                result = new { rules = rules.Count };
                break;
            }
            case ("DELETE", "/__e2e/rules"):
                provider.SetRules([]);
                result = new { rules = 0 };
                break;
            case ("PUT", "/__e2e/hold"):
            {
                var body = await JsonSerializer.DeserializeAsync<HoldRequest>(context.Request.Body, JsonOptions);
                BranchProcessorSwitches.Hold = body?.Enabled ?? false;
                result = new { hold = BranchProcessorSwitches.Hold };
                break;
            }
            case ("PUT", "/__e2e/pause"):
            {
                var body = await JsonSerializer.DeserializeAsync<HoldRequest>(context.Request.Body, JsonOptions);
                BranchProcessorSwitches.Pause = body?.Enabled ?? false;
                result = new { pause = BranchProcessorSwitches.Pause };
                break;
            }
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonOptions));
    }

    private string? ReadDatabasePath()
    {
        var connection = configuration.GetConnectionString("Default") ?? string.Empty;
        const string prefix = "Data Source=";
        return connection.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? connection[prefix.Length..] : null;
    }

    private sealed record HoldRequest(bool Enabled);
}
