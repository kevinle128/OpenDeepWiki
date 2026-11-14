using System.Text.Json;
using OpenDeepWiki.Entities;
using OpenDeepWiki.MCP;
using OpenDeepWiki.Tests.Chat.Sessions;
using Xunit;

namespace OpenDeepWiki.Tests.MCP;

public class McpGlobalToolsTests
{
    [Fact]
    public async Task SearchRepositories_ShouldRouteQuestionToRelevantRepository()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context,
            "repo-camera",
            "YD_HW/services",
            "youdao-capture-frame",
            "Camera capture service",
            "zh",
            "CaptureFrame 启动链路",
            "capture/startup",
            "CaptureFrame 负责 camera 取图、sensor 初始化和帧处理。");
        await SeedRepositoryAsync(
            context,
            "repo-ota",
            "YD_HW/services",
            "youdao-ota-manager",
            "OTA service",
            "zh",
            "OTA 安装流程",
            "ota/install",
            "OTA 下载、校验和安装升级包。");

        var json = await McpGlobalTools.SearchRepositories(context, "Camera 取图链路", "zh");
        using var document = JsonDocument.Parse(json);
        var repositories = document.RootElement.GetProperty("repositories");

        Assert.True(repositories.GetArrayLength() > 0);
        Assert.Equal("youdao-capture-frame", repositories[0].GetProperty("repo").GetString());
    }

    [Fact]
    public async Task SearchDocs_ShouldSearchAcrossRoutedRepositories()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context,
            "repo-camera",
            "YD_HW/services",
            "youdao-capture-frame",
            "Camera capture service",
            "zh",
            "CaptureFrame 取图链路",
            "capture/frame-flow",
            "CaptureFrame 从 camera sensor 获取图像帧，经过 ISP 和帧处理后输出。");
        await SeedRepositoryAsync(
            context,
            "repo-ota",
            "YD_HW/services",
            "youdao-ota-manager",
            "OTA service",
            "zh",
            "OTA 安装流程",
            "ota/install",
            "OTA 安装会校验包、写分区并重启。");

        var json = await McpGlobalTools.SearchDocs(context, "sensor 图像帧", language: "zh");
        using var document = JsonDocument.Parse(json);
        var results = document.RootElement.GetProperty("results");

        Assert.True(results.GetArrayLength() > 0);
        Assert.Equal("youdao-capture-frame", results[0].GetProperty("repo").GetString());
        Assert.Equal("capture/frame-flow", results[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task SearchDocs_WithExplicitRepository_ShouldOnlySearchThatRepository()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context,
            "repo-camera",
            "YD_HW/services",
            "youdao-capture-frame",
            "Camera capture service",
            "zh",
            "CaptureFrame 取图链路",
            "capture/frame-flow",
            "sensor 图像帧处理");
        await SeedRepositoryAsync(
            context,
            "repo-display",
            "YD_HW/apps",
            "youdao-display",
            "Display app",
            "zh",
            "显示图像帧",
            "display/frame-flow",
            "sensor 图像帧显示");

        var json = await McpGlobalTools.SearchDocs(
            context,
            "sensor 图像帧",
            "YD_HW/apps",
            "youdao-display",
            "zh");
        using var document = JsonDocument.Parse(json);
        var results = document.RootElement.GetProperty("results");
        var routedRepositories = document.RootElement.GetProperty("routedRepositories");

        Assert.Equal(1, routedRepositories.GetArrayLength());
        Assert.True(results.GetArrayLength() > 0);
        Assert.All(results.EnumerateArray(), result =>
        {
            Assert.Equal("YD_HW/apps", result.GetProperty("owner").GetString());
            Assert.Equal("youdao-display", result.GetProperty("repo").GetString());
        });
    }

    [Fact]
    public async Task SearchDocs_ShouldRouteChineseQueryWithoutSpaces()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context,
            "repo-camera",
            "YD_HW/services",
            "youdao-capture-frame",
            "Camera capture service",
            "zh",
            "前摄无图排查",
            "camera/front-no-image",
            "前摄无图时检查 sensor 上电、V4L2 节点和帧计数。");

        var json = await McpGlobalTools.SearchDocs(context, "前摄无图", language: "zh");
        using var document = JsonDocument.Parse(json);
        var results = document.RootElement.GetProperty("results");

        Assert.True(results.GetArrayLength() > 0);
        Assert.Equal("camera/front-no-image", results[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task SearchDocs_WhenNoRepositoryMatches_ShouldReturnEmptyResults()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context,
            "repo-camera",
            "YD_HW/services",
            "youdao-capture-frame",
            "Camera capture service",
            "zh",
            "CaptureFrame 取图链路",
            "capture/frame-flow",
            "sensor 图像帧处理");

        var json = await McpGlobalTools.SearchDocs(context, "completely-unmatched-query", language: "zh");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.Empty(root.GetProperty("routedRepositories").EnumerateArray());
        Assert.Empty(root.GetProperty("results").EnumerateArray());
    }

    [Theory]
    [InlineData("zzzz-unmatched-query-20260701")]
    [InlineData("霓虹火山水母词条")]
    public async Task SearchDocs_WithRareSingleTokenNoise_ShouldReturnEmptyResults(string query)
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context,
            "repo-camera",
            "YD_HW/services",
            "youdao-capture-frame",
            "Camera capture service",
            "zh",
            "CaptureFrame 取图链路",
            "capture/query-overview",
            "本文档描述普通词条、query 参数、camera sensor 和图像帧处理。");

        var json = await McpGlobalTools.SearchDocs(context, query, language: "zh");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.Empty(root.GetProperty("routedRepositories").EnumerateArray());
        Assert.Empty(root.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task ReadDoc_ShouldReturnMatchingDocumentContent()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context,
            "repo-camera",
            "YD_HW/services",
            "youdao-capture-frame",
            "Camera capture service",
            "zh",
            "CaptureFrame 取图链路",
            "capture/frame-flow",
            "CaptureFrame 取图链路正文。",
            sourceFiles: """["Capture/CaptureFrame.c"]""");

        var json = await McpGlobalTools.ReadDoc(
            context,
            "YD_HW/services",
            "youdao-capture-frame",
            "capture/frame-flow",
            "zh");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("YD_HW/services/youdao-capture-frame", root.GetProperty("repository").GetString());
        Assert.Equal("CaptureFrame 取图链路正文。", root.GetProperty("content").GetString());
        Assert.Equal("Capture/CaptureFrame.c", root.GetProperty("sourceFiles")[0].GetString());
    }

    [Fact]
    public async Task ReadDoc_WhenDocumentDoesNotExist_ShouldReturnError()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context,
            "repo-camera",
            "YD_HW/services",
            "youdao-capture-frame",
            "Camera capture service",
            "zh",
            "CaptureFrame 取图链路",
            "capture/frame-flow",
            "CaptureFrame 取图链路正文。");

        var json = await McpGlobalTools.ReadDoc(
            context,
            "YD_HW/services",
            "youdao-capture-frame",
            "missing/path",
            "zh");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("error").GetBoolean());
        Assert.Contains("not found", root.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SearchDocs_WithBranch_ShouldOnlySearchThatBranchWiki()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context, "repo-gateway", "vtc_blr", "blr-gateway", "Gateway", "en",
            "User Connections", "runtime/user-connections", "websocket write error on main");
        await SeedBranchAsync(
            context, "repo-gateway", "dev", "en",
            "User Connections (dev)", "runtime-architecture/user-connections", "websocket write error on dev");

        var json = await McpGlobalTools.SearchDocs(
            context, "websocket write error", "vtc_blr", "blr-gateway", "en", branch: "dev");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var results = root.GetProperty("results");

        Assert.Equal("dev", root.GetProperty("branch").GetString());
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("dev", results[0].GetProperty("branch").GetString());
        Assert.Equal("runtime-architecture/user-connections", results[0].GetProperty("path").GetString());
        Assert.Equal(["dev", "main"], root.GetProperty("availableBranches").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("message").ValueKind);
    }

    [Fact]
    public async Task SearchDocs_WithoutBranch_ShouldNameTheBranchOfEachResult()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context, "repo-gateway", "vtc_blr", "blr-gateway", "Gateway", "en",
            "User Connections", "runtime/user-connections", "websocket write error on main");
        await SeedBranchAsync(
            context, "repo-gateway", "dev", "en",
            "User Connections (dev)", "runtime-architecture/user-connections", "websocket write error on dev");

        var json = await McpGlobalTools.SearchDocs(context, "websocket write error", "vtc_blr", "blr-gateway", "en");
        using var document = JsonDocument.Parse(json);
        var branches = document.RootElement.GetProperty("results").EnumerateArray()
            .Select(result => result.GetProperty("branch").GetString())
            .OrderBy(name => name)
            .ToList();

        Assert.Equal(["dev", "main"], branches);
    }

    [Fact]
    public async Task SearchDocs_WithBranchThatHasNoWiki_ShouldSayWhichBranchesHaveOne()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context, "repo-gateway", "vtc_blr", "blr-gateway", "Gateway", "en",
            "User Connections", "runtime/user-connections", "websocket write error on main");

        var json = await McpGlobalTools.SearchDocs(
            context, "websocket write error", "vtc_blr", "blr-gateway", "en", branch: "dev");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.Contains("No wiki on branch dev", root.GetProperty("message").GetString());
        Assert.Contains("main", root.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReadDoc_WithBranchMissingThePath_ShouldNameTheBranchesThatHaveIt()
    {
        await using var context = TestDbContext.Create();
        await SeedRepositoryAsync(
            context, "repo-gateway", "vtc_blr", "blr-gateway", "Gateway", "en",
            "User Connections", "runtime/user-connections", "main content");
        await SeedBranchAsync(
            context, "repo-gateway", "dev", "en",
            "User Connections (dev)", "runtime-architecture/user-connections", "dev content");

        var json = await McpGlobalTools.ReadDoc(
            context, "vtc_blr", "blr-gateway", "runtime/user-connections", "en", branch: "dev");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.GetProperty("error").GetBoolean());
        Assert.Contains("not found on branch dev", root.GetProperty("message").GetString());
        Assert.Equal(["main"], root.GetProperty("branchesWithPath").EnumerateArray().Select(item => item.GetString()));

        var devJson = await McpGlobalTools.ReadDoc(
            context, "vtc_blr", "blr-gateway", "runtime-architecture/user-connections", "en", branch: "dev");
        using var devDocument = JsonDocument.Parse(devJson);
        Assert.Equal("dev", devDocument.RootElement.GetProperty("branch").GetString());
        Assert.Equal("dev content", devDocument.RootElement.GetProperty("content").GetString());
    }

    private static async Task SeedBranchAsync(
        TestDbContext context,
        string repositoryId,
        string branchName,
        string language,
        string title,
        string path,
        string content)
    {
        var prefix = $"{repositoryId}-{branchName}";
        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = $"{prefix}-branch",
            RepositoryId = repositoryId,
            BranchName = branchName,
            LastCommitId = "def456",
            LastProcessedAt = DateTime.UtcNow
        });
        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = $"{prefix}-language",
            RepositoryBranchId = $"{prefix}-branch",
            LanguageCode = language,
            IsDefault = true
        });
        context.DocFiles.Add(new DocFile
        {
            Id = $"{prefix}-doc-file",
            BranchLanguageId = $"{prefix}-language",
            Content = content
        });
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = $"{prefix}-catalog",
            BranchLanguageId = $"{prefix}-language",
            Title = title,
            Path = path,
            DocFileId = $"{prefix}-doc-file",
            Order = 1
        });

        await context.SaveChangesAsync();
    }

    private static async Task SeedRepositoryAsync(
        TestDbContext context,
        string repositoryId,
        string owner,
        string repo,
        string description,
        string language,
        string title,
        string path,
        string content,
        string? sourceFiles = null)
    {
        var branchId = $"{repositoryId}-branch";
        var languageId = $"{repositoryId}-language";
        var docFileId = $"{repositoryId}-doc-file";

        context.Repositories.Add(new Repository
        {
            Id = repositoryId,
            OwnerUserId = "user-1",
            OrgName = owner,
            RepoName = repo,
            Description = description,
            GitUrl = $"https://example.com/{owner}/{repo}.git",
            PrimaryLanguage = "C",
            Status = RepositoryStatus.Completed
        });
        context.RepositoryBranches.Add(new RepositoryBranch
        {
            Id = branchId,
            RepositoryId = repositoryId,
            BranchName = "main",
            LastCommitId = "abc123",
            LastProcessedAt = DateTime.UtcNow
        });
        context.BranchLanguages.Add(new BranchLanguage
        {
            Id = languageId,
            RepositoryBranchId = branchId,
            LanguageCode = language,
            IsDefault = true
        });
        context.DocFiles.Add(new DocFile
        {
            Id = docFileId,
            BranchLanguageId = languageId,
            Content = content,
            SourceFiles = sourceFiles
        });
        context.DocCatalogs.Add(new DocCatalog
        {
            Id = $"{repositoryId}-catalog",
            BranchLanguageId = languageId,
            Title = title,
            Path = path,
            DocFileId = docFileId,
            Order = 1
        });

        await context.SaveChangesAsync();
    }
}
