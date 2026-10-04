using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using OpenDeepWiki.Services.GitConnections;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.E2EHost;

/// <summary>
/// The real application with three test seams: the Git provider HTTP transport, name resolution and wiki
/// generation. Everything else (authentication, authorization, endpoints, services, database, address guard,
/// cursor codec, Data Protection) is production code.
/// </summary>
internal sealed class E2EAppFactory(string workDir, RequestLog log, FakeGitProviderHandler provider)
    : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("E2E");
        builder.UseContentRoot(FindApplicationContentRoot());
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGitHostResolver>();
            services.AddSingleton<IGitHostResolver, FakeHostResolver>();

            // PostConfigure runs after every Configure call, so this handler is the innermost one. The real
            // address guard handler stays in front of it.
            foreach (var name in new[] { GitProviderHttpClientNames.GitHub, GitProviderHttpClientNames.GitLab })
            {
                services.AddOptions<HttpClientFactoryOptions>(name).PostConfigure(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handlerBuilder => handlerBuilder.PrimaryHandler = provider));
            }

            // The real coordinator, except that the pause switch keeps queued branch jobs from starting.
            services.RemoveAll<IWikiGenerationCoordinator>();
            services.AddScoped<IWikiGenerationCoordinator>(sp =>
                new PausableCoordinator(ActivatorUtilities.CreateInstance<WikiGenerationCoordinator>(sp)));

            // No clone or fetch can happen in this host, whatever job asks for one.
            services.RemoveAll<IRepositoryAnalyzer>();
            services.AddScoped<IRepositoryAnalyzer, NoNetworkAnalyzer>();

            services.RemoveAll<IRepositoryBranchProcessor>();
            services.AddScoped<IRepositoryBranchProcessor, FakeBranchProcessor>();

            services.AddSingleton(log);
            services.AddSingleton(provider);
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(sp =>
                new E2EControlStartupFilter(
                    log, provider, sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), workDir));
        });
    }

    /// <summary>
    /// The application directory (appsettings.json, prompts) next to the solution file.
    /// </summary>
    private static string FindApplicationContentRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenDeepWiki.sln")))
            {
                return Path.Combine(directory.FullName, "src", "OpenDeepWiki");
            }
        }

        throw new InvalidOperationException("OpenDeepWiki.sln was not found above the E2E host directory.");
    }
}
