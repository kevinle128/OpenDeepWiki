using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

public class GitProviderStartupTests
{
    [Fact]
    public void ShippedAppSettings_HaveAnEmptyPrivateNetworkAllowlistAndABoundedTimeout()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(FindAppSettings(), optional: false).Build();

        var options = configuration.GetSection(GitProviderOptions.SectionName).Get<GitProviderOptions>();

        Assert.NotNull(options);
        Assert.Empty(options!.AllowedPrivateHosts);
        Assert.Empty(options.AllowedPrivateCidrs);
        Assert.InRange(options.RequestTimeoutSeconds, 1, 120);
    }

    [Fact]
    public void Registration_ResolvesTheResolverWithBothProviders()
    {
        using var provider = BuildProvider([]);

        var resolver = provider.GetRequiredService<IGitProviderClientResolver>();

        Assert.IsType<GitHubPatProviderClient>(resolver.Resolve(GitProvider.GitHub));
        Assert.IsType<GitLabPatProviderClient>(resolver.Resolve(GitProvider.GitLab));
        var exception = Assert.Throws<GitProviderException>(() => resolver.Resolve((GitProvider)99));
        Assert.Equal(GitProviderErrorCodes.UnsupportedProvider, exception.Code);
    }

    [Fact]
    public void Registration_AppliesTheConfiguredTimeoutToBothNamedClients()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["GitProviders:RequestTimeoutSeconds"] = "7" });
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        Assert.Equal(TimeSpan.FromSeconds(7), factory.CreateClient(GitProviderHttpClientNames.GitHub).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(7), factory.CreateClient(GitProviderHttpClientNames.GitLab).Timeout);
    }

    [Fact]
    public void Registration_WhenAllowlistedCidrIsInvalid_FailsOptionsValidation()
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["GitProviders:AllowedPrivateCidrs:0"] = "10.0.0.0/99" });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GitProviderOptions>>().Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100000")]
    public void Registration_WhenTimeoutIsOutOfRange_FailsOptionsValidation(string seconds)
    {
        using var provider = BuildProvider(new Dictionary<string, string?> { ["GitProviders:RequestTimeoutSeconds"] = seconds });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<GitProviderOptions>>().Value);
    }

    [Fact]
    public void Registration_ReadsTheOperatorAllowlist()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["GitProviders:AllowedPrivateHosts:0"] = "git.corp.example",
            ["GitProviders:AllowedPrivateCidrs:0"] = "10.20.0.0/16"
        });

        var options = provider.GetRequiredService<IOptions<GitProviderOptions>>().Value;

        Assert.Equal(["git.corp.example"], options.AllowedPrivateHosts);
        Assert.Equal(["10.20.0.0/16"], options.AllowedPrivateCidrs);
        Assert.NotNull(provider.GetRequiredService<GitLabServerUrlValidator>());
    }

    [Fact]
    public void DirectHandler_DisablesRedirectsAndKeepsDefaultCertificateValidation()
    {
        using var handler = GitProviderGuardHandler.CreateDirectHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddGitProviderCatalog(configuration);
        return services.BuildServiceProvider();
    }

    private static string FindAppSettings()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "OpenDeepWiki", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("appsettings.json of the API project was not found.");
    }
}
