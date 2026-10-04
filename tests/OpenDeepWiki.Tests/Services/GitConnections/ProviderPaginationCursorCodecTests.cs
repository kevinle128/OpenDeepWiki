using Microsoft.AspNetCore.DataProtection;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;
using Xunit;

namespace OpenDeepWiki.Tests.Services.GitConnections;

public class ProviderPaginationCursorCodecTests
{
    private const string Origin = "https://gitlab.example.com";

    private static readonly IReadOnlyDictionary<string, string> Page2 = new Dictionary<string, string> { ["page"] = "2" };

    [Fact]
    public void RoundTrip_ReturnsTheStoredFields()
    {
        var codec = NewCodec();
        var target = Target("c1");

        var cursor = codec.Encode(target, Origin, "repositories", null, Page2);

        Assert.Equal(Page2, codec.Decode(cursor, target, Origin, "repositories", null));
    }

    [Fact]
    public void Encode_ProducesOpaqueTextWithoutUrlOrFieldNames()
    {
        var cursor = NewCodec().Encode(Target("c1"), Origin, "repositories", null, Page2);

        Assert.DoesNotContain("gitlab", cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("page", cursor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", cursor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_WhenCursorIsChanged_RejectsWithInvalidCursor()
    {
        var codec = NewCodec();
        var target = Target("c1");
        var cursor = codec.Encode(target, Origin, "repositories", null, Page2);
        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? "B" : "A") + cursor[^1];

        AssertInvalid(() => codec.Decode(tampered, target, Origin, "repositories", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("https://evil.example.com/api/v4/projects?page=2")]
    public void Decode_WhenCursorIsNotProtectedData_RejectsWithInvalidCursor(string cursor)
    {
        AssertInvalid(() => NewCodec().Decode(cursor, Target("c1"), Origin, "repositories", null));
    }

    [Fact]
    public void Decode_WhenCursorIsTooLong_RejectsWithInvalidCursor()
    {
        AssertInvalid(() => NewCodec().Decode(new string('A', 5000), Target("c1"), Origin, "repositories", null));
    }

    [Fact]
    public void Decode_WhenUsedWithAnotherConnection_Rejects()
    {
        var codec = NewCodec();
        var cursor = codec.Encode(Target("c1"), Origin, "repositories", null, Page2);

        AssertInvalid(() => codec.Decode(cursor, Target("c2"), Origin, "repositories", null));
    }

    [Fact]
    public void Decode_WhenUsedWithAnotherProvider_Rejects()
    {
        var codec = NewCodec();
        var cursor = codec.Encode(Target("c1"), Origin, "repositories", null, Page2);

        AssertInvalid(() => codec.Decode(cursor, Target("c1", GitProvider.GitHub), Origin, "repositories", null));
    }

    [Fact]
    public void Decode_WhenUsedWithAnotherOrigin_Rejects()
    {
        var codec = NewCodec();
        var cursor = codec.Encode(Target("c1"), Origin, "repositories", null, Page2);

        AssertInvalid(() => codec.Decode(cursor, Target("c1"), "https://other.example.com", "repositories", null));
    }

    [Fact]
    public void Decode_WhenUsedForAnotherListKindOrRepository_Rejects()
    {
        var codec = NewCodec();
        var target = Target("c1");
        var cursor = codec.Encode(target, Origin, "branches", "77", Page2);

        AssertInvalid(() => codec.Decode(cursor, target, Origin, "repositories", null));
        AssertInvalid(() => codec.Decode(cursor, target, Origin, "branches", "78"));
        Assert.Equal(Page2, codec.Decode(cursor, target, Origin, "branches", "77"));
    }

    [Fact]
    public void Decode_WhenCursorWasProtectedByAnotherKeyRing_Rejects()
    {
        var cursor = NewCodec().Encode(Target("c1"), Origin, "repositories", null, Page2);

        AssertInvalid(() => NewCodec().Decode(cursor, Target("c1"), Origin, "repositories", null));
    }

    [Theory]
    [InlineData("per_page", "1000")]
    [InlineData("membership", "false")]
    [InlineData("page", "abc")]
    [InlineData("page", "-1")]
    [InlineData("id_after", "1; DROP")]
    [InlineData("pagination", "other")]
    [InlineData("sort", "sideways")]
    public void Encode_WhenFieldIsNotAllowedOrMalformed_Refuses(string name, string value)
    {
        var fields = new Dictionary<string, string> { [name] = value };

        Assert.Throws<GitProviderException>(
            () => NewCodec().Encode(Target("c1"), Origin, "repositories", null, fields));
    }

    [Fact]
    public void Encode_WhenConnectionIdIsMissing_Refuses()
    {
        var target = new GitProviderTarget(null, GitProvider.GitLab, Origin, "token");

        Assert.Throws<GitProviderException>(() => NewCodec().Encode(target, Origin, "repositories", null, Page2));
    }

    private static ProviderPaginationCursorCodec NewCodec() => new(new EphemeralDataProtectionProvider());

    private static GitProviderTarget Target(string connectionId, GitProvider provider = GitProvider.GitLab)
        => new(connectionId, provider, Origin, "token");

    private static void AssertInvalid(Action action)
    {
        var exception = Assert.Throws<GitProviderException>(action);
        Assert.Equal(GitProviderErrorCodes.InvalidCursor, exception.Code);
    }
}
