using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace OpenDeepWiki.Tests.Infrastructure;

public class ProductionStartupSecurityTests
{
    private const string StrongKey = "0123456789abcdef0123456789abcdef-strong-signing-key";
    private const string BuiltInKey = "OpenDeepWiki-Default-Secret-Key-Please-Change-In-Production-Environment-2024";

    [Fact]
    public void Validate_WhenProductionHasStrongKeyAndKeyRing_Passes()
    {
        var configuration = Configure(("Jwt:SecretKey", StrongKey), ("DataProtection:KeyRingPath", "/data/keys"));

        Program.ValidateProductionSecurity(configuration, Environment("Production"));
    }

    [Fact]
    public void Validate_WhenProductionHasNoDurableKeyRing_FailsWithSafeMessage()
    {
        var configuration = Configure(("Jwt:SecretKey", StrongKey));

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.ValidateProductionSecurity(configuration, Environment("Production")));

        Assert.Contains("DataProtection:KeyRingPath", exception.Message);
        Assert.DoesNotContain(StrongKey, exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WhenProductionKeyRingPathIsBlank_Fails(string? path)
    {
        var configuration = Configure(("Jwt:SecretKey", StrongKey), ("DataProtection:KeyRingPath", path));

        Assert.Throws<InvalidOperationException>(
            () => Program.ValidateProductionSecurity(configuration, Environment("Production")));
    }

    [Fact]
    public void Validate_WhenProductionJwtKeyIsMissing_UsesBuiltInKeyAndFails()
    {
        var configuration = Configure(("DataProtection:KeyRingPath", "/data/keys"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.ValidateProductionSecurity(configuration, Environment("Production")));

        Assert.DoesNotContain(BuiltInKey, exception.Message);
    }

    [Theory]
    [InlineData("Jwt:SecretKey")]
    [InlineData("JWT_SECRET_KEY")]
    public void Validate_WhenProductionJwtKeyIsTheBuiltInDefault_Fails(string key)
    {
        var configuration = Configure((key, BuiltInKey), ("DataProtection:KeyRingPath", "/data/keys"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.ValidateProductionSecurity(configuration, Environment("Production")));

        Assert.DoesNotContain(BuiltInKey, exception.Message);
    }

    [Theory]
    [InlineData("short-key")]
    [InlineData("0123456789012345678901234567890")] // 31 bytes
    public void Validate_WhenProductionJwtKeyIsWeak_FailsWithoutEchoingTheKey(string weakKey)
    {
        var configuration = Configure(("Jwt:SecretKey", weakKey), ("DataProtection:KeyRingPath", "/data/keys"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => Program.ValidateProductionSecurity(configuration, Environment("Production")));

        Assert.DoesNotContain(weakKey, exception.Message);
    }

    [Fact]
    public void Validate_WhenProductionJwtKeyIsExactlyThirtyTwoBytes_Passes()
    {
        var configuration = Configure(
            ("Jwt:SecretKey", "01234567890123456789012345678901"),
            ("DataProtection:KeyRingPath", "/data/keys"));

        Program.ValidateProductionSecurity(configuration, Environment("Production"));
    }

    [Fact]
    public void Validate_WhenDevelopmentHasNoSecurityConfiguration_Passes()
    {
        Program.ValidateProductionSecurity(Configure(), Environment("Development"));
    }

    [Fact]
    public void ResolveJwtSecretKey_PrefersExplicitKeyOverBuiltInDefaultInAppSettings()
    {
        var configuration = Configure(("Jwt:SecretKey", BuiltInKey), ("JWT_SECRET_KEY", StrongKey));

        Assert.Equal(StrongKey, Program.ResolveJwtSecretKey(configuration));
    }

    [Fact]
    public void ResolveJwtSecretKey_WhenNothingIsConfigured_FallsBackToTheBuiltInDefault()
    {
        Assert.Equal(BuiltInKey, Program.ResolveJwtSecretKey(Configure()));
    }

    [Fact]
    public void ResolveJwtSecretKey_WhenConfiguredKeyIsCustom_UsesIt()
    {
        var configuration = Configure(("Jwt:SecretKey", StrongKey), ("JWT_SECRET_KEY", "ignored"));

        Assert.Equal(StrongKey, Program.ResolveJwtSecretKey(configuration));
    }

    private static IConfiguration Configure(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build();

    private static IHostEnvironment Environment(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(item => item.EnvironmentName).Returns(name);
        return environment.Object;
    }
}
