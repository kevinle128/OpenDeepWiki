# 后端失败测试修复

Status: DONE_WITH_CONCERNS

初始复现命令为 `dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter 'FullyQualifiedName~RepositoryAnalyzerSourceTests|FullyQualifiedName~RepositorySkillMarkdownBuilderTests' --no-restore`。
结果为 7 个失败、11 个通过。

本地 Git 来源的六个失败均来自路径身份比较。
macOS 的 TMPDIR 使用 `/var/folders/...`，而 `/var` 指向 `/private/var`。
`TryOpenExactGitWorkdir` 和 Git CLI 的根路径检查用 `Path.GetFullPath` 比较路径，该方法不解析父目录的符号链接。
Git 返回 `/private/var/...`，检查误判为不同根目录，随后把 Git 来源当作普通目录快照处理。
`LocalPathEquals` 现在逐级解析现有目录的符号链接，并使用相同规则检查工作区删除边界。
`safe.directory` 参数保持原有路径格式。
根路径规范化保留 `/`，避免将根目录变成空字符串。

文档索引失败来自 `AddDocumentEntries` 只收录叶节点。
有子目录的 Overview 节点也有 DocFileId，其文档被遗漏。
现在所有有 DocFileId 的节点均进入共享文档列表，索引和 ZIP 导出因此保持一致。

新增两个回归检查：父目录是符号链接的本地 Git 来源，以及包含父节点和子节点正文的真实 ZIP 导出。
原有工作区根目录检查仍使用严格相等断言，比较解析后的实体路径。

最终验证命令为 `dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter 'FullyQualifiedName~RepositoryAnalyzer|FullyQualifiedName~RepositorySkillMarkdownBuilder|FullyQualifiedName~RepositoryDocsServiceGraphifyTests' --no-restore --verbosity quiet`。
结果为 57 个通过、0 个失败、0 个跳过。
没有启动后台进程，没有访问或修改 Git 元数据。

Concerns: 编译仍报告归属范围外的已有 C#、EF、OpenAPI 警告，以及 ASP.NET 分析器 AD0001。
这些文件未修改，需由主控处理或记录。
未决问题：无。
