using Microsoft.Extensions.Options;
using OpenDeepWiki.Entities;

namespace OpenDeepWiki.Services.GitConnections;

public static class GitConnectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers provider clients, the address policy, and the named HTTP clients.
    /// The GitLab client runs behind the address guard; both clients disable redirects.
    /// </summary>
    public static IServiceCollection AddGitProviderCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GitProviderOptions>()
            .Bind(configuration.GetSection(GitProviderOptions.SectionName))
            .Validate(options => options.HasValidCidrs(), "GitProviders:AllowedPrivateCidrs contains an invalid CIDR.")
            .Validate(
                options => options.RequestTimeoutSeconds is >= 1 and <= 120,
                "GitProviders:RequestTimeoutSeconds must be between 1 and 120.")
            .ValidateOnStart();

        services.AddSingleton<IGitHostResolver, SystemGitHostResolver>();
        services.AddSingleton<GitLabServerUrlValidator>();
        services.AddTransient<GitProviderGuardHandler>();
        services.AddSingleton<ProviderPaginationCursorCodec>();

        services.AddHttpClient(GitProviderHttpClientNames.GitHub)
            .ConfigureHttpClient(ConfigureTimeout)
            .ConfigurePrimaryHttpMessageHandler(GitProviderGuardHandler.CreateDirectHandler);

        services.AddHttpClient(GitProviderHttpClientNames.GitLab)
            .ConfigureHttpClient(ConfigureTimeout)
            .ConfigurePrimaryHttpMessageHandler(GitProviderGuardHandler.CreatePrimaryHandler)
            .AddHttpMessageHandler<GitProviderGuardHandler>();

        services.AddSingleton<IGitProviderClient, GitHubPatProviderClient>();
        services.AddSingleton<IGitProviderClient, GitLabPatProviderClient>();
        services.AddSingleton<IGitProviderClientResolver, GitProviderClientResolver>();
        services.AddScoped<IGitConnectionService, GitConnectionService>();
        return services;
    }

    private static void ConfigureTimeout(IServiceProvider provider, HttpClient client)
        => client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<GitProviderOptions>>().Value.RequestTimeoutSeconds);
}
