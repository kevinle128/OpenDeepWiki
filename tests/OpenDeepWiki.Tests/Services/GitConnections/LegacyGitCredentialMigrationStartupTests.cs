using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenDeepWiki.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

/// <summary>
/// The backfill calls GitHub and GitLab, so only an Admin request may start it. Nothing may start it at startup.
/// </summary>
public class LegacyGitCredentialMigrationStartupTests
{
    [Fact]
    public void Registration_AddsOneScopedServiceAndNoHostedService()
    {
        var services = new ServiceCollection();

        services.AddLegacyGitCredentialMigration();

        var descriptor = Assert.Single(services, item => item.ServiceType == typeof(ILegacyGitCredentialMigrationService));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(LegacyGitCredentialMigrationService), descriptor.ImplementationType);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void NoHostedServiceOrStartupTaskDependsOnTheMigrationService()
    {
        var hostedTypes = typeof(Program).Assembly.GetTypes()
            .Where(type => typeof(IHostedService).IsAssignableFrom(type) && !type.IsAbstract);

        foreach (var type in hostedTypes)
        {
            var parameters = type.GetConstructors().SelectMany(constructor => constructor.GetParameters());
            Assert.DoesNotContain(parameters, parameter =>
                parameter.ParameterType == typeof(ILegacyGitCredentialMigrationService)
                || parameter.ParameterType == typeof(LegacyGitCredentialMigrationService));
        }
    }

    [Fact]
    public void TheMigrationServiceExposesNoOperationThatTheStartupInitializerCouldCallWithoutAnAdmin()
    {
        // Every operation needs the signed-in caller, so a startup task without a user context is refused.
        var constructor = Assert.Single(typeof(LegacyGitCredentialMigrationService).GetConstructors());

        Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(OpenDeepWiki.Services.Auth.IUserContext));
        Assert.Contains(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IGitConnectionAuthorizationService));
    }
}
