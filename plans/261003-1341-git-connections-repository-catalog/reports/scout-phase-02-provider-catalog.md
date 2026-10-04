# Phase 02 Scout：Provider Catalog

## 目标与验收边界

本阶段只交付 PAT Git Connection 和远端仓库、分支目录。
支持 GitHub.com、GitLab.com 和 HTTPS GitLab Self-Managed。
远端仓库使用 provider 稳定 ID 识别。
本阶段不创建索引任务，也不改变现有 GitHub App 导入行为。

验收结果必须满足以下条件。

- 任一已登录用户可创建、查看、更新和删除自己的 Git Connection。
- token 只以加密形式持久化，并且不会出现在响应、日志或异常中。
- GitHub 和 GitLab 仓库、分支列表能读取全部分页。
- Self-Managed URL 通过 HTTPS、DNS、地址范围和 redirect 校验。
- 目录项包含稳定 provider ID、显示名、clone URL、默认分支、可见性和本地连接状态。
- clone 和 API 请求不跳过 TLS 证书校验。

## 当前依赖图

```text
RepositoryService.GetBranchesAsync
  -> IGitPlatformService.GetBranchesAsync
     -> GitPlatformService.ParseGitUrl
     -> IHttpClientFactory.CreateClient
     -> GitHub/GitLab fixed SaaS URLs

GitHubImportEndpoints
  -> IUserGitHubImportService
     -> IGitHubAppService
        -> GitHubAppService
           -> IHttpClientFactory.CreateClient("GitHubApp")
           -> GitHub App installation token
```

第一条路径位于 `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:534-562`。
`IGitPlatformService` 的 contract 位于 `src/OpenDeepWiki/Services/Repositories/IGitPlatformService.cs:36-58`。
`GitPlatformService` 位于 `src/OpenDeepWiki/Services/Repositories/GitPlatformService.cs:8-400`。
第二条路径从 `src/OpenDeepWiki/Endpoints/GitHubImportEndpoints.cs:7-64` 开始。
GitHub App provider 位于 `src/OpenDeepWiki/Services/GitHub/GitHubAppService.cs:19-255`。

## 当前实现缺口

`GitPlatformService.ParseGitUrl` 只接受三个固定 host，见 `GitPlatformService.cs:49-85`。
它只读取两个 path segment，所以 GitLab subgroup 会解析错误。
GitHub 和 GitLab token 来自全局 configuration，见同文件 `:10-12`。
这不支持每用户可复用连接。
GitHub branch 请求只读取一次 `per_page=100`，见同文件 `:160-207`。
GitLab branch 请求也只读取一次 `per_page=100`，见同文件 `:298-346`。
`CheckRepoExistsAsync` 总是查询 GitHub，见同文件 `:349-399`。

`GitHubAppService.ListInstallationReposAsync` 已有 page/perPage 形状，见 `GitHubAppService.cs:171-206`。
它可作为 response mapping 参考，但认证模型和范围不符合 PAT scope。
`UserGitHubImportService.ListInstallationReposAsync` 已合并本地 `AlreadyImported`，见 `src/OpenDeepWiki/Services/GitHub/UserGitHubImportService.cs:71-114`。
它用 clone URL 判断已导入，所以不能直接复用身份算法。

`RepositorySubmitRequest` 暴露 `AuthAccount/AuthPassword`，见 `src/OpenDeepWiki/Models/RepositorySubmitRequest.cs:33-43`。
`Repository.AuthPassword` 明文持久化，见 `src/OpenDeepWiki.Entities/Repositories/Repository.cs:71-81`。
新目录路径不得继续使用这两个字段。

`AesConfigEncryption` 位于 `src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:10-95`。
它使用固定 IV，并在无配置时使用默认密钥，见同文件 `:16-29`。
PAT 不能复用该实现。

## 推荐 provider contract

```text
GitConnectionEndpoints
  -> IGitConnectionService
     -> IGitConnectionSecretProtector
     -> IGitProviderClientResolver
        -> GitHubPatProviderClient
        -> GitLabPatProviderClient
           -> IHttpClientFactory named clients
```

`IGitProviderClient` 只需要四个操作。

- `ValidateAsync`。
- `GetRepositoryAsync`。
- `ListRepositoriesAsync`。
- `ListBranchesAsync`。

返回分页对象应包含 `Items` 和 opaque `NextCursor`。
`TotalCount` 必须可空，因为 GitLab 可能省略总数 header。
cursor 不能接受任意 URL。
cursor 必须绑定 provider、connection 和允许的 API origin。

GitHub 仓库身份使用 numeric repository `id`。
GitLab 仓库身份使用 numeric project `id`。
全局 remote key 使用 `(Provider, NormalizedBaseUrl, ProviderRepositoryId)`。
owner/name、path、clone URL 和 default branch 都是可刷新元数据，不是 identity。

## 需要保护的函数与接口

### 安全不变量

- `GitConnectionService.CreateAsync` 必须先验证 token，再保存连接。
- `GitConnectionService.UpdateAsync` 必须在 token 或 base URL 变化时重新验证。
- `GitConnectionService.DeleteAsync` 只能操作当前用户自己的连接。
- `GitLabBaseUrlValidator.Validate` 必须拒绝 HTTP、userinfo、path、query 和 fragment。
- `GitLabBaseUrlValidator.ValidateResolvedAddress` 必须阻止 loopback、link-local、metadata 和未允许的私网地址。
- `ProviderPaginationCursorCodec.Decode` 必须校验 connection、provider 和 API origin。
- `GitConnectionSecretProtector` 必须使用 authenticated encryption 或 ASP.NET Core Data Protection。
- `GitProviderHttpMessageHandler` 必须重做 redirect 目标校验，或禁止自动 redirect。
- 所有 provider logger 必须只记录 connection ID、provider、status code 和 request ID。

### 现有符号保护

- 保持 `IGitPlatformService.GetRepoStatsAsync` 的公开统计兼容行为，见 `IGitPlatformService.cs:38-44`。
- 保持现有 GitHub App endpoints 和 `IGitHubAppService` contract 不变。
- 保持 archive、local 和旧 `/api/v1/repositories/submit` 路径不变。
- 不得修改自动生成的 migration designer 或 snapshot 以外的 generated 文件。

## 文件操作清单

### 创建

- `src/OpenDeepWiki.Entities/Repositories/GitConnection.cs`。
- `src/OpenDeepWiki/Services/Git/IGitProviderClient.cs`。
- `src/OpenDeepWiki/Services/Git/GitProviderModels.cs`。
- `src/OpenDeepWiki/Services/Git/GitProviderClientResolver.cs`。
- `src/OpenDeepWiki/Services/Git/GitHubPatProviderClient.cs`。
- `src/OpenDeepWiki/Services/Git/GitLabPatProviderClient.cs`。
- `src/OpenDeepWiki/Services/Git/GitLabBaseUrlValidator.cs`。
- `src/OpenDeepWiki/Services/Git/GitConnectionSecretProtector.cs`。
- `src/OpenDeepWiki/Services/Git/GitConnectionService.cs`。
- `src/OpenDeepWiki/Endpoints/GitConnectionEndpoints.cs`。
- `src/OpenDeepWiki/Models/Git/GitConnectionModels.cs`。
- `tests/OpenDeepWiki.Tests/Services/Git/GitHubPatProviderClientTests.cs`。
- `tests/OpenDeepWiki.Tests/Services/Git/GitLabPatProviderClientTests.cs`。
- `tests/OpenDeepWiki.Tests/Services/Git/GitLabBaseUrlValidatorTests.cs`。
- `tests/OpenDeepWiki.Tests/Services/Git/GitConnectionServiceTests.cs`。
- `tests/OpenDeepWiki.Tests/Endpoints/GitConnectionEndpointsTests.cs`。
- 一组 PostgreSQL migration、designer 和一组 SQLite migration、designer。

### 修改

- `src/OpenDeepWiki.EFCore/MasterDbContext.cs:7-57` 增加 `DbSet<GitConnection>`。
- `src/OpenDeepWiki.EFCore/MasterDbContext.cs:60-490` 增加实体、关系和索引配置。
- `src/OpenDeepWiki/Program.cs:139` 附近注册 provider clients 和 connection services。
- `src/OpenDeepWiki/Program.cs:396-411` 附近映射新 endpoints。
- `src/EFCore/OpenDeepWiki.Postgresql/Migrations/PostgresqlDbContextModelSnapshot.cs`。
- `src/EFCore/OpenDeepWiki.Sqlite/Migrations/SqliteDbContextModelSnapshot.cs`。

### 删除

本阶段不删除文件。
`GitPlatformService` 保留给旧公开统计和兼容分支查询。

## 依赖审计

`src/OpenDeepWiki/OpenDeepWiki.csproj:12-44` 没有 Octokit 或 GitLab SDK。
项目已有 `IHttpClientFactory` 和 `System.Text.Json`。
两个 provider 的 API surface 很小，因此不应增加 provider SDK。
使用现有平台 HTTP 和 JSON 能减少依赖、版本漂移和重复认证模型。

不要新增另一个通用 retry package。
Provider API 应尊重 cancellation、429 和 `Retry-After`，但 endpoint 请求不应隐藏长时间重试。
clone 的重试属于 Phase 3，并继续使用现有 analyzer 配置。

不要复用 `AesConfigEncryption`。
如果项目选择 ASP.NET Core Data Protection，则框架已经可用，无需新 NuGet package。

## 当前测试基线与缺口

仓库相关、管理、EFCore 和基础设施选定目录当前共有 140 个 `[Fact]`、`[Theory]` 或 `[Property]` 测试。
其中 provider catalog 的直接自动测试为 0。
`GitHubAppInstallationModelTests.cs` 只有 1 个模型测试。
`GitHubImportGenerateSkillTests.cs` 有 2 个导入字段测试，但不覆盖 API、分页或 PAT。
`RepositorySourceSubmitTests.cs` 有 26 个 source submit 测试，但不覆盖连接目录。

缺失覆盖如下。

- GitHub `/user` PAT 验证。
- GitLab `/user` PAT 验证。
- GitHub repository 和 branch Link pagination。
- GitLab keyset、offset fallback 和 Link pagination。
- GitLab subgroup 和 numeric project ID。
- Self-Managed URL 和 SSRF 防护。
- redirect 重新校验。
- 401、403、404、429、5xx 和 timeout 映射。
- token encryption、rotation、masking 和 log redaction。
- 连接 owner authorization。
- stable remote key 在 rename 和 transfer 后保持不变。
- 两个连接看到同一 remote 时的 imported state。

HTTP 测试应使用自定义 `HttpMessageHandler`，不要调用真实 provider。
真实 provider E2E 只在 secret-enabled CI 或手工验证中运行。

## 建议验证命令

```bash
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitHubPatProviderClientTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitLabPatProviderClientTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitLabBaseUrlValidatorTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitConnectionServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitConnectionEndpointsTests
dotnet build OpenDeepWiki.sln
```

## 风险依赖图

```text
User supplied BaseUrl
  -> URL normalization
  -> DNS resolution
  -> address allow policy
  -> HttpClient request
  -> redirect target validation

PAT
  -> request DTO
  -> provider validation
  -> secret protector
  -> database ciphertext
  -> provider request header

Provider repository ID
  -> catalog item
  -> local connected state lookup
  -> Phase 3 repository identity
```

任何一层如果记录原始 request header，都会泄露 PAT。
任何一层如果相信 provider path 而不是 provider ID，都会在 rename 或 subgroup 场景产生重复仓库。

## 未解决问题

- Self-Managed 是否允许管理员配置内网 allowlist。
- Git Connection 是否允许部门共享，当前确认只说明所有已登录用户可管理 branch。
- 生产 secret protector 的 key ring 持久化位置由部署专项决定。

Status: DONE
Summary: Phase 02 的文件、依赖、接口保护、测试基线和 provider 分页边界已确认。
Concerns/Blockers: Self-Managed 内网 allowlist 和连接共享范围仍需产品确认。
