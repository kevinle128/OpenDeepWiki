using OpenDeepWiki.Services.Wiki;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Wiki;

public class WikiLanguageNamesTests
{
    [Theory]
    [InlineData("zh", "Chinese (Simplified)")]
    [InlineData("zh-CN", "Chinese (Simplified)")]
    [InlineData("zh-tw", "Chinese (Traditional)")]
    [InlineData("pt-BR", "Portuguese (Brazil)")]
    [InlineData("pt-br", "Portuguese (Brazil)")]
    [InlineData("ja", "Japanese")]
    [InlineData("ko", "Korean")]
    [InlineData("es", "Spanish")]
    [InlineData("fr", "French")]
    [InlineData("de", "German")]
    [InlineData("pl", "Polish")]
    [InlineData("ru", "Russian")]
    [InlineData("ar", "Arabic")]
    [InlineData("it", "Italian")]
    [InlineData("vi", "Vietnamese")]
    [InlineData("vi-VN", "Vietnamese")]
    [InlineData(null, "English")]
    [InlineData("", "English")]
    public void GetEnglishName_ShouldMapKnownCodes(string? code, string expected)
    {
        Assert.Equal(expected, WikiLanguageNames.GetEnglishName(code));
    }

    [Fact]
    public void GetEnglishName_ShouldKeepUnknownCodes()
    {
        Assert.Equal("xx-custom", WikiLanguageNames.GetEnglishName("xx-custom"));
    }

    [Fact]
    public void GetTranslationLanguages_ShouldUseEnglishAndVietnameseOnly()
    {
        var options = new WikiGeneratorOptions();

        Assert.Equal("en,vi", options.Languages);
        Assert.Equal(["vi"], options.GetTranslationLanguages("en"));
        Assert.Equal(["en"], options.GetTranslationLanguages("vi"));
    }
}
