using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using OpenDeepWiki.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

public sealed class GitConnectionSecretProtectorTests : IDisposable
{
    private const string CanaryPat = "ghp_CANARY_0123456789abcdefghijklmnopqrstuv";
    private const string ApplicationName = "OpenDeepWiki.Tests";

    private readonly string _keyRingDirectory = Path.Combine(
        TestPaths.ScratchRoot,
        $"keyring-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_keyRingDirectory))
        {
            Directory.Delete(_keyRingDirectory, recursive: true);
        }
    }

    [Fact]
    public void Protect_WhenCalledTwiceForSamePat_ReturnsDifferentCiphertexts()
    {
        var protector = CreateProtector(CreateProvider());

        var first = protector.Protect(CanaryPat);
        var second = protector.Protect(CanaryPat);

        Assert.NotEqual(first, second);
        Assert.Equal(CanaryPat, protector.Unprotect(first));
        Assert.Equal(CanaryPat, protector.Unprotect(second));
    }

    [Fact]
    public void Protect_ReturnsOnlyProtectedText()
    {
        var protector = CreateProtector(CreateProvider());

        var protectedText = protector.Protect(CanaryPat);

        Assert.DoesNotContain(CanaryPat, protectedText);
        Assert.DoesNotContain("CANARY", protectedText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(-1)]
    public void Unprotect_WhenOneCharacterChanges_FailsClosed(int position)
    {
        var protector = CreateProtector(CreateProvider());
        var protectedText = protector.Protect(CanaryPat);
        var index = position < 0 ? protectedText.Length - 1 : position;
        var tampered = ReplaceCharacter(protectedText, index);

        var exception = Assert.Throws<GitConnectionSecretException>(() => protector.Unprotect(tampered));

        Assert.DoesNotContain(CanaryPat, exception.Message);
    }

    [Fact]
    public void Unprotect_WhenPayloadWasProtectedForAnotherPurpose_FailsClosed()
    {
        var provider = CreateProvider();
        var foreignPayload = provider.CreateProtector("Some.Other.Purpose.v1").Protect(CanaryPat);
        var protector = CreateProtector(provider);

        Assert.Throws<GitConnectionSecretException>(() => protector.Unprotect(foreignPayload));
    }

    [Fact]
    public void Unprotect_WhenApplicationNameDiffers_FailsClosed()
    {
        var payload = CreateProtector(CreateProvider()).Protect(CanaryPat);
        var otherApplication = CreateProtector(CreateProvider("Another.Application"));

        Assert.Throws<GitConnectionSecretException>(() => otherApplication.Unprotect(payload));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-protected-payload")]
    public void Unprotect_WhenPayloadIsNotProtectedText_FailsClosed(string payload)
    {
        var protector = CreateProtector(CreateProvider());

        Assert.Throws<GitConnectionSecretException>(() => protector.Unprotect(payload));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Protect_WhenPatIsBlank_Throws(string pat)
    {
        var protector = CreateProtector(CreateProvider());

        Assert.Throws<ArgumentException>(() => protector.Protect(pat));
    }

    [Fact]
    public void Unprotect_AfterKeyRotationAndRestart_ReadsOldPayloadAndWritesWithNewKey()
    {
        var oldPayload = CreateProtector(CreateProvider()).Protect(CanaryPat);

        // A new provider over the same directory models an application restart.
        using var restarted = BuildServices();
        var keyManager = restarted.GetRequiredService<IKeyManager>();
        var keysBefore = keyManager.GetAllKeys().Count;
        keyManager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));

        using var rotated = BuildServices();
        var rotatedProtector = CreateProtector(rotated.GetRequiredService<IDataProtectionProvider>());
        var newPayload = rotatedProtector.Protect(CanaryPat);

        Assert.True(rotated.GetRequiredService<IKeyManager>().GetAllKeys().Count > keysBefore);
        Assert.Equal(CanaryPat, rotatedProtector.Unprotect(oldPayload));
        Assert.Equal(CanaryPat, rotatedProtector.Unprotect(newPayload));
    }

    [Fact]
    public void Protect_WhenPatIsFourKilobytes_RoundTripsAndStaysBelowUnboundedColumnLimit()
    {
        var protector = CreateProtector(CreateProvider());
        var largePat = new string('p', 4096);

        var protectedText = protector.Protect(largePat);

        Assert.Equal(largePat, protector.Unprotect(protectedText));
        Assert.True(protectedText.Length > 4096, "Protected payload is larger than the plain PAT.");
    }

    [Fact]
    public void Purpose_IsTheVersionedGitConnectionPatPurpose()
    {
        Assert.Equal("OpenDeepWiki.GitConnections.Pat.v1", DataProtectionGitConnectionSecretProtector.Purpose);
    }

    private IDataProtectionProvider CreateProvider(string applicationName = ApplicationName)
        => BuildServices(applicationName).GetRequiredService<IDataProtectionProvider>();

    private ServiceProvider BuildServices(string applicationName = ApplicationName)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName(applicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(_keyRingDirectory));
        return services.BuildServiceProvider();
    }

    private static IGitConnectionSecretProtector CreateProtector(IDataProtectionProvider provider)
        => new DataProtectionGitConnectionSecretProtector(provider);

    private static string ReplaceCharacter(string value, int index)
    {
        var chars = value.ToCharArray();
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        return new string(chars);
    }
}
