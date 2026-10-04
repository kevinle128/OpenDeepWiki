# Git Connections、仓库目录与分支生命周期研究

## 结论

现有代码已经有可复用的分支工作区、全量任务、增量任务、分支级文档模型和后台 worker。
新功能不应再创建另一套索引流水线。
最小且稳健的方案是增加 `GitConnection`，把远端发现和凭据解析放在新的 provider 层，并让现有 `RepositoryAnalyzer` 通过连接取得凭据。
现有 `Repository` 可以继续表示 Connected Repository，但必须增加稳定远端身份和连接外键，并停止用 `GitUrl + BranchName` 表示唯一性。
每个选中的 Indexed Branch 必须先创建 `RepositoryBranch`，再创建独立的 `BranchGenerationTask`。
移除 Indexed Branch 时只删除该分支的索引数据、任务和工作区，不删除 `Repository` 或 `GitConnection`。

当前实现有四个直接阻塞项。
`Repository.AuthPassword` 明文存储在数据库中。
`GitPlatformService` 只支持固定 SaaS 主机，并且 GitHub 和 GitLab 分支列表都只取最多 100 条。
`RepositoryGenerationLock` 对 `RepositoryId` 做唯一约束，所以同一远端的不同分支不能真正独立运行。
`BranchGenerationTaskService.FindActiveBranchTaskAsync` 接收 `branchId`，但查询没有使用它，因此任一分支的活动任务会阻止同仓库其他分支入队。

## 已确认范围与非目标

- 支持 GitHub.com PAT。
- 支持 GitLab.com PAT。
- 支持 HTTPS GitLab Self-Managed。
- 一个 Git Connection 可供多个远端仓库复用。
- 仓库目录来自连接对应的 provider API。
- 同一远端只创建一个 Connected Repository。
- 一个 Connected Repository 可选择多个 Indexed Branches。
- 每个分支有独立的 full generation 和 incremental update 作业。
- 移除分支只删除该分支索引，不删除远端仓库记录。
- 本阶段不需要 GitHub App、SSH、Gitee 或 OAuth 连接适配。
- 现有 GitHub App 导入可保留为兼容路径，但不应成为新 PAT 模型的基础接口。

## 现有端到端调用链

### 手工提交与初次生成

1. `RepositoryService.SubmitAsync` 在 `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:26-104` 接收一个 Git URL 和一个分支。
2. 它以原始 `GitUrl + BranchName` 查重，再按 `GitUrl + OrgName + RepoName` 决定新建仓库或增加分支，见同文件 `:33-59`。
3. `CreateRepositoryAsync` 在同文件 `:575-650` 同时写入 `Repository`、`RepositoryBranch` 和默认 `BranchLanguage`，并把仓库设为 `Pending`。
4. `RepositoryProcessingWorker.DispatchPendingAsync` 在 `src/OpenDeepWiki/Services/Repositories/RepositoryProcessingWorker.cs:62-143` 轮询 `Pending/Processing` 仓库。
5. `ProcessRepositoryAsync` 在同文件 `:311-350` 顺序遍历该仓库的全部分支。
6. `RepositoryBranchProcessor.ProcessBranchAsync` 在 `src/OpenDeepWiki/Services/Repositories/RepositoryBranchProcessor.cs:29-142` 准备工作区、选择全量或增量、处理全部语言，并更新分支 commit。
7. `RepositoryAnalyzer.PrepareWorkspaceAsync` 在 `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:156-249` 按分支创建独立工作区，并 clone 或 fetch。
8. 工作区路径是 `{RepositoriesDirectory}/{org}/{repo}/branches/{branch}/tree`，见同文件 `:348-362` 和 `RepositoryWorkspacePath.cs:16-27`。

该入口不适合新的多选分支流程。
它把首次处理绑定到仓库聚合状态，并且一个分支失败会使仓库级工作失败。
新的连接导入应直接创建 branch full-generation tasks，不应依赖 `Repository.Status = Pending` 的旧 worker。

### Clone、认证与更新

`RepositoryAnalyzer.BuildCredentials` 在 `RepositoryAnalyzer.cs:1419-1432` 从 `Repository.AuthAccount/AuthPassword` 构造 `UsernamePasswordCredentials`。
`CloneRepositoryAsync` 在同文件 `:1438-1526` 使用 LibGit2Sharp、目标分支和三次可配置重试。
`PullRepositoryAsync` 在同文件 `:1531-1613` fetch `origin`，再执行硬切换。
`CheckoutRemoteBranchHard` 在同文件 `:1615-1646` 创建或更新本地分支，并 hard reset 到远端 tip。
`GetRemoteBranchHeadCommitAsync` 在同文件 `:72-153` 用相同凭据调用 `ListRemoteReferences`。

这些 clone、fetch、checkout、retry 和工作区代码应全部复用。
需要改变的是凭据来源，而不是 Git 操作实现。
建议给 `IRepositoryAnalyzer` 注入一个 `IGitCredentialResolver`。
resolver 按 `Repository.GitConnectionId` 读取并解密 token，再返回 GitHub 或 GitLab 的 HTTPS 用户名和 token。
GitHub PAT 的兼容用户名可使用非空固定值，例如 `x-access-token`。
GitLab PAT 的兼容用户名可使用 `oauth2`，密码为 PAT。
不要把 token 拼入 URL、日志或异常。

当前 clone 和 fetch 在 `RepositoryAnalyzer.cs:1461-1462` 与 `:1562-1564` 无条件接受任意 TLS 证书。
这会破坏 HTTPS Self-Managed 的信任边界。
新实现必须默认执行正常证书校验。
如产品以后需要私有 CA，应配置系统信任链或明确的证书策略，不应保留全局跳过校验。

### Provider 仓库目录和分支目录

`GitPlatformService` 在 `src/OpenDeepWiki/Services/Repositories/GitPlatformService.cs:8-400` 直接读取全局配置 token。
`ParseGitUrl` 在 `:49-85` 只识别 `github.com`、`gitee.com` 和 `gitlab.com`，并只取前两个路径段。
因此它不能正确处理 GitLab subgroup，也不能处理 Self-Managed。
GitHub 分支查询在 `:160-207` 固定调用 `per_page=100` 一次。
GitLab 分支查询在 `:298-346` 也固定调用 `per_page=100` 一次。
`CheckRepoExistsAsync` 在 `:349-399` 无论 URL 来自哪个 provider 都只检查 GitHub。

现有 GitHub App 路径可提供 DTO 和 UI 交互参考。
`GitHubAppService.ListInstallationReposAsync` 在 `src/OpenDeepWiki/Services/GitHub/GitHubAppService.cs:171-206` 已支持显式 `page/perPage`。
`UserGitHubImportService.ListInstallationReposAsync` 在 `src/OpenDeepWiki/Services/GitHub/UserGitHubImportService.cs:71-114` 已把远端列表和本地已导入状态合并。
`UserGitHubImportService.ImportAsync` 在同文件 `:116-225` 已有批量创建仓库、默认分支和语言的事务单元形状。
但是它用 clone URL 查重，只导入默认分支，并且不把临时 installation token 供 clone 使用。

建议增加一个小的 provider 契约，而不是继续扩展 `GitPlatformService` 的 host switch。

```csharp
interface IGitProviderClient
{
    GitProviderKind Kind { get; }
    Task<GitConnectionIdentity> ValidateAsync(GitConnection connection, CancellationToken ct);
    Task<ProviderPage<RemoteRepository>> ListRepositoriesAsync(GitConnection connection, string? cursor, int pageSize, CancellationToken ct);
    Task<ProviderPage<RemoteBranch>> ListBranchesAsync(GitConnection connection, string providerRepositoryId, string? cursor, int pageSize, CancellationToken ct);
}
```

只需两个实现：`GitHubPatProviderClient` 和 `GitLabPatProviderClient`。
一个 resolver 可按 `GitProviderKind` 选择实现。

### Full generation

`BranchGenerationEndpoints` 在 `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs:12-28` 提供入队、读取、重试和取消端点。
`BranchGenerationTaskService.EnqueueFullGenerationAsync` 在 `src/OpenDeepWiki/Services/Repositories/BranchGenerationTaskService.cs:37-145` 校验仓库和分支，创建任务并预留 generation lock。
`BranchGenerationWorker` 在 `src/OpenDeepWiki/Services/Repositories/BranchGenerationWorker.cs:51-116` 按优先级领取任务。
worker 最终调用共享的 `RepositoryBranchProcessor`，因此已有生成算法不需要复制。
`BranchFullGenerationCleaner` 在 `src/OpenDeepWiki/Services/Repositories/BranchFullGenerationCleaner.cs:14-70` 删除该分支的 catalog/doc 数据，取消未完成的增量任务，并重置 branch baseline。

这里有两个必须先修的协调问题。
`BranchGenerationTaskService.FindActiveBranchTaskAsync` 在 `:287-300` 没有使用 `branchId`。
`MasterDbContext` 在 `src/OpenDeepWiki.EFCore/MasterDbContext.cs:360-362` 把 lock 唯一性设为整个仓库。
`RepositoryGenerationLockService.TryAcquireAsync` 在 `src/OpenDeepWiki/Services/Repositories/RepositoryGenerationLockService.cs:80-139` 也只按 `RepositoryId` 查找。
因此多分支任务虽然是独立记录，但不能为同一仓库独立排队或并行。

建议给 lock 增加可空 `BranchId`。
仓库级任务使用 `BranchId = null`，并与任一分支锁互斥。
分支级 full 和 incremental 任务使用具体 `BranchId`，只与同分支任务互斥。
数据库约束必须分别覆盖一个仓库级活动锁和每分支一个活动锁。
如果同仓库不同分支允许并行，当前每分支独立工作区已经满足文件系统隔离。

### Incremental update

手工入口是 `POST /api/v1/repositories/{repositoryId}/branches/{branchId}/incremental-update`，见 `src/OpenDeepWiki/Endpoints/IncrementalUpdateEndpoints.cs:23-46`。
该端点目前没有调用与 full generation 相同的 owner/admin 授权，见同文件 `:55-135`。
这必须修复，否则知道 ID 的调用者可触发其他用户仓库的任务。

`IncrementalUpdateService.TriggerManualUpdateAsync` 在 `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateService.cs:287-349` 对同分支活动增量任务去重，并检查 full task。
它在 `:326-339` 没有确认 branch 属于传入 repository，也会在 branch 不存在时创建任务。
`ProcessIncrementalUpdateAsync` 在 `:125-285` 准备分支工作区，比较 commit，调用 `IWikiGenerator.IncrementalUpdateAsync`，然后推进 baseline。
查询 branch 的语句在 `:141-143` 也没有校验 `RepositoryId`。

`IncrementalUpdateWorker` 在 `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateWorker.cs:69-105` 先处理手工任务，再扫描定时更新。
定时扫描在 `:316-354` 选择已完成仓库。
`CreateScheduledUpdateTasksAsync` 在 `:414-529` 按分支查询远端 HEAD，只在 commit 变化时创建任务。
这些逻辑可保留，但必须改为连接凭据，并移除对仓库聚合 `Completed` 状态的错误依赖。

`RepositoryAnalyzer.GetChangedFilesBetweenCommits` 在 `RepositoryAnalyzer.cs:1698-1742` 忽略 deleted files。
因此删除源文件时增量文档可能不会删除或刷新。
新的分支生命周期测试必须覆盖文件删除和重命名。

## 数据模型建议

### `GitConnection`

新增 `src/OpenDeepWiki.Entities/Repositories/GitConnection.cs`。

建议字段如下。

- `Id`。
- `OwnerUserId`。
- `Name`，供 UI 显示。
- `Provider`，枚举值仅为 `GitHub` 和 `GitLab`。
- `BaseUrl`，GitHub 固定为 `https://github.com`，GitLab.com 固定为 `https://gitlab.com`，Self-Managed 保存规范化 HTTPS origin。
- `ApiBaseUrl` 可由 `BaseUrl` 派生为 GitHub API 或 `{BaseUrl}/api/v4`，不必单独持久化。
- `EncryptedToken`。
- `ExternalUserId`，保存 GitHub user `id` 或 GitLab user `id` 的字符串形式。
- `ExternalLogin`，只用于显示。
- `LastValidatedAt`。
- `CreatedAt/UpdatedAt/IsDeleted` 来自基类。

唯一约束建议为 `(OwnerUserId, Provider, BaseUrl, Name)`。
连接名是用户可控标识，同一用户可为同一实例保存多个账号连接。
不要用 token hash 作为业务身份。

现有 `AesConfigEncryption` 位于 `src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:10-95`，但它使用固定 IV，并且在没有配置时使用默认密钥。
它不适合作为 PAT 的长期安全存储。
建议使用 ASP.NET Core Data Protection 的专用 purpose，或新增带随机 nonce 和认证标签的 AES-GCM secret protector。
启动时缺少生产密钥必须失败。
API 响应只返回 `hasToken` 和掩码，不返回密文或明文。

### Connected Repository

复用 `Repository`，避免再建一张与文档、书签、订阅和任务体系平行的仓库表。
在 `src/OpenDeepWiki.Entities/Repositories/Repository.cs` 增加：

- `GitConnectionId`，可空以兼容 archive、local 和旧 Git URL。
- `Provider`，可空枚举。
- `ProviderRepositoryId`，GitHub numeric repository ID 或 GitLab project ID 的字符串形式。
- `CanonicalWebUrl` 或规范化 `CloneUrl`。
- 可选 `DefaultBranch`，减少目录刷新时重复推断。

远端唯一键应是 `(Provider, NormalizedBaseUrl, ProviderRepositoryId)`。
GitHub repository `id` 在 GitHub.com 内稳定。
GitLab project `id` 只在某个 GitLab instance 内稳定，所以必须包含规范化 instance origin。
不要用 owner/name 或 clone URL 做唯一键，因为仓库可重命名、转移或改变 path。

为了让数据库直接强制“一远端一 Connected Repository”，`Repository` 最简单的持久化形状是同时保存 `ProviderBaseUrl` 和 `ProviderRepositoryId`，并建立过滤唯一索引。
如果只保存 `GitConnectionId`，跨两个连接指向同一实例时，数据库不能直接阻止重复远端。

连接删除时，不应级联删除 Connected Repository。
如果连接仍被仓库使用，应返回冲突，或先要求用户为这些仓库选择替代连接。
静默置空会使私有仓库的后续 full 和 incremental 作业失败。

### Indexed Branch

继续复用 `RepositoryBranch` 和 `BranchLanguage`。
`MasterDbContext` 已在 `src/OpenDeepWiki.EFCore/MasterDbContext.cs:141-148` 为 `(RepositoryId, BranchName)` 与 `(RepositoryBranchId, LanguageCode)` 建唯一索引。
应把 branch 唯一索引改为只覆盖未删除记录，或在恢复同名软删除行时复用原 ID。
否则软删除后重新索引同名 branch 会撞唯一键。

建议增加 `ProviderBranchRef` 不是必需的。
Git branch 名就是 `refs/heads/{name}` 的稳定选择键。
分支改名应显示为旧分支已消失和新分支可添加，不应猜测 rename。

## Provider API 与分页策略

### GitHub.com

创建或更新连接时调用 `GET https://api.github.com/user` 验证 PAT，并保存 user `id/login`。
仓库目录调用 `GET /user/repos?visibility=all&affiliation=owner,collaborator,organization_member&sort=full_name&per_page=100`。
GitHub 文档说明该端点返回认证用户明确可访问的仓库，并支持每页最多 100 条，见 [GitHub repositories API](https://docs.github.com/en/rest/repos/repos)。
分支目录调用 `GET /repos/{owner}/{repo}/branches?per_page=100`。
该端点对 fine-grained PAT 需要 Contents read 权限，见 [GitHub branches API](https://docs.github.com/en/rest/branches/branches)。
两类列表都应跟随响应 `Link` header 中 `rel="next"` 的 URL，直到没有 next，见 [GitHub pagination](https://docs.github.com/en/rest/using-the-rest-api/using-pagination-in-the-rest-api)。
请求应发送 `Accept: application/vnd.github+json`、`Authorization: Bearer ...` 和固定 API version header。
仓库身份使用响应的 numeric `id`，显示使用 `full_name`，clone 使用 `clone_url`。

### GitLab.com 与 Self-Managed

创建或更新连接时调用 `{BaseUrl}/api/v4/user` 验证 PAT，并保存 user `id/username`。
仓库目录调用 `GET /api/v4/projects?membership=true&simple=true&order_by=id&sort=asc&pagination=keyset&per_page=100`。
GitLab Projects API 支持 offset pagination，也支持按 ID 的 keyset pagination，见 [GitLab Projects API](https://docs.gitlab.com/api/projects/)。
若目标 Self-Managed 版本不接受该 keyset 参数，应回退到 offset pagination。
分支目录调用 `GET /api/v4/projects/{urlEncodedProjectId}/repository/branches?per_page=100`。
项目请求优先使用 numeric project ID，避免 subgroup path 编码错误和 rename 问题。
认证使用 `PRIVATE-TOKEN` header，见 [GitLab REST authentication](https://docs.gitlab.com/api/rest/authentication/)。
分页应优先跟随 `Link: rel="next"`，不要自行构造 next URL。
GitLab 文档也提供 `X-Next-Page`，但 GitLab.com 对大型结果可能省略部分总数 header，见 [GitLab REST pagination](https://docs.gitlab.com/api/rest/)。
项目身份使用 numeric `id`，显示使用 `path_with_namespace`，clone 使用 `http_url_to_repo`。

Self-Managed `BaseUrl` 只允许 HTTPS origin。
应拒绝 URL 中的用户名、密码、query、fragment 和非空 path。
在服务端发请求前应解析 DNS，并阻止 loopback、link-local、私网和云元数据地址，除非部署配置明确允许内部 GitLab 网段。
这是 SSRF 信任边界，不能只靠前端校验。
重定向也必须重新执行目标校验，或直接禁用自动重定向。

### 前端分页契约

后端不应把全部远端仓库一次性装入内存。
列表 API 应返回 `items`、`nextCursor` 和可空 `totalCount`。
GitHub cursor 可编码 provider next URL 或 page token。
GitLab cursor 可编码 next Link URL 或 keyset cursor。
cursor 必须是服务端签名或不透明值，不能允许客户端提交任意 URL。
连接 ID 决定 host 和 token，cursor 只能改变该 provider 允许的分页参数。

## API 建议

- `POST /api/v1/git-connections` 创建并立即验证连接。
- `GET /api/v1/git-connections` 列出当前用户的连接。
- `PATCH /api/v1/git-connections/{id}` 更新名称、base URL 或 token，并重新验证。
- `DELETE /api/v1/git-connections/{id}` 删除未被使用的连接。
- `GET /api/v1/git-connections/{id}/repositories?cursor=&pageSize=` 返回远端目录和本地连接状态。
- `GET /api/v1/git-connections/{id}/repositories/{providerRepositoryId}/branches?cursor=&pageSize=` 返回远端分支和 `isIndexed`。
- `POST /api/v1/connected-repositories` 以连接、provider repository ID、多个 branch 名和 language code 原子地创建或复用 Connected Repository，并为新增分支分别入队 full task。
- `POST /api/v1/repositories/{repositoryId}/indexed-branches` 给现有 Connected Repository 增加多个分支，并分别入队 full task。
- `DELETE /api/v1/repositories/{repositoryId}/indexed-branches/{branchId}` 只删除一个分支索引。

所有端点必须复用 `BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync` 在 `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs:101-130` 的 owner/admin 策略，或提取同等的共享授权服务。
连接端点还必须校验 `GitConnection.OwnerUserId`。

创建 Connected Repository 的服务必须使用数据库事务。
它应按稳定远端键读取或插入 `Repository`，按 `(RepositoryId, BranchName)` 读取或插入 branches，然后为每个新增 branch 创建 full task。
对已索引 branch 应返回现有状态，不应重复任务。
唯一约束冲突应重新读取并返回现有记录，以处理并发双击和多实例请求。

## 移除分支的准确边界

当前只有整仓库删除路径。
`AdminRepositoryService.DeleteRepositoryDataAsync` 在 `src/OpenDeepWiki/Services/Admin/AdminRepositoryService.cs:267-401` 展示了相关表，但它不能直接用于分支删除，因为它也删除仓库级关系。

建议新增 `IIndexedBranchRemovalService`。
一个事务内执行下列动作。

1. 校验 repository owner/admin 和 branch 归属。
2. 若该 branch 有 `Pending` full 或 incremental task，则先取消任务并释放对应分支 lock。
3. 若有 `Processing` task，则返回 `409 BRANCH_JOB_ACTIVE`，不要在运行中删除数据。
4. 删除或软删除 `DocCatalog`、`DocFile`、`BranchLanguage`、`TranslationTask`、`IncrementalUpdateTask`、`BranchGenerationTask`、`GraphifyArtifact` 和该分支 processing logs。
5. 删除或软删除 `RepositoryBranch`。
6. 提交后删除 `RepositoryWorkspacePath.ForBranch(...)` 对应的 branch 工作区。
7. 保留 `Repository`、连接外键、书签、订阅、仓库统计和其他分支。

工作区删除必须验证路径仍在 configured repository root 下。
数据库提交成功但目录删除失败时，应记录 warning，并允许后台清理重试。
不能因目录清理失败回滚已经完成的数据库删除。

`BranchFullGenerationCleaner` 只能作为清理 catalog/doc 和未完成增量任务的部分参考。
它会把 branch 状态重置为 Pending，所以不能直接用于 removal。

## 数据库迁移计划

项目同时维护 PostgreSQL 和 SQLite migration。
每次模型变更必须更新两个 provider 的 migration 和 model snapshot。
不要手工修改现有 migration。

建议按一个功能 migration 完成以下变更。

1. 新建 `GitConnections` 表，并建立 owner/provider/base/name 唯一索引。
2. 给 `Repositories` 增加可空 `GitConnectionId`、`GitProvider`、`ProviderBaseUrl`、`ProviderRepositoryId` 和可空 `DefaultBranch`。
3. 建立 `Repositories(GitProvider, ProviderBaseUrl, ProviderRepositoryId)` 的过滤唯一索引，只覆盖 provider 字段非空且 `IsDeleted = false` 的行。
4. `GitConnectionId` 使用 restrict delete。
5. 给 `RepositoryGenerationLocks` 增加可空 `BranchId` 和 branch FK。
6. 删除当前 `RepositoryId` 单列唯一索引。
7. 为仓库级锁建立 `RepositoryId` 的过滤唯一索引，其中 `BranchId IS NULL`。
8. 为分支级锁建立 `(RepositoryId, BranchId)` 的过滤唯一索引，其中 `BranchId IS NOT NULL`。
9. 把 `RepositoryBranches(RepositoryId, BranchName)` 改为只覆盖未删除行，或采用恢复软删除行的服务策略。

旧数据不应自动创建 Git Connection，因为无法可靠判断全局配置 token 属于哪个用户。
旧 Git 仓库保持 `GitConnectionId = null`，继续使用现有 `AuthAccount/AuthPassword` 兼容读取。
新连接仓库禁止写 `AuthPassword`。
后续单独迁移旧明文凭据前，应先提供用户重新绑定连接的 UI。

`MasterDbContext` 需要增加 `DbSet<GitConnection>` 和关系配置。
`IContext` 也必须增加对应 DbSet，见 `src/OpenDeepWiki.EFCore/MasterDbContext.cs:7-57`。

## 可复用代码与应替换代码

### 直接复用

- `RepositoryAnalyzer` 的按分支工作区、clone、fetch、checkout、commit diff 和 retry。
- `RepositoryWorkspacePath` 的分支路径布局与 sanitize。
- `RepositoryBranchProcessor` 的 full/incremental 分流和多语言处理。
- `BranchGenerationTaskService`、`BranchGenerationWorker` 和 `BranchFullGenerationCleaner`，修复分支锁后复用。
- `IncrementalUpdateService` 和 `IncrementalUpdateWorker`，补齐归属校验、授权和连接凭据后复用。
- `WikiGenerationCoordinator` 的全局 generation slot 限流。
- GitHub App DTO 的 repository summary 和 `AlreadyImported` 合并模式。
- 现有 PostgreSQL/SQLite 双 migration 结构。

### 替换或收窄

- 不要继续扩展 `GitPlatformService.ParseGitUrl` 的 host switch。
- 不要再使用全局 `GitHub:Token` 或 `GitLab:Token` 作为用户连接凭据。
- 不要使用 clone URL 做远端唯一身份。
- 不要让新导入走 `RepositoryProcessingWorker` 的仓库级 Pending 流程。
- 不要复用 `AesConfigEncryption` 保存 PAT，除非先替换固定 IV、默认密钥和无认证加密设计。
- 不要在任何新代码中跳过 TLS certificate check。

## 失败模式与响应

- PAT 无效或过期：创建/验证连接返回 401，保留已有连接的最后有效元数据，但将验证状态标为失败。
- PAT scope 不足：返回 403，并区分不能列仓库和不能读取仓库内容。
- GitHub rate limit：读取 `X-RateLimit-Reset`，返回 429 和可重试时间。
- GitLab rate limit：尊重 `Retry-After`，返回 429。
- Self-Managed DNS、TLS 或超时：返回 provider unreachable，不回显 token 或底层带凭据 URL。
- API 分页中途失败：不要返回“完整”标志，可返回失败；不要把部分列表误当成完整目录。
- 远端仓库 rename/transfer：按 provider ID 命中已有 repository，并刷新显示名和 clone URL。
- 远端仓库删除或权限撤销：目录刷新标记 unavailable，不删除本地索引。
- 远端 branch 删除：标记 missing，不自动删除本地索引。
- 并发连接同一远端：依赖稳定键唯一索引，并在冲突后读取现有记录。
- 多选分支部分无效：先验证全部分支，再原子创建；不要产生半批任务。
- full 与 incremental 同分支竞争：分支 lock 返回 409 或复用活动任务。
- 不同分支并行：允许各自取得 branch lock，但仍受全局 generation slot 限制。
- 删除运行中分支：返回 409，禁止删任务下的数据。
- 服务重启：现有 heartbeat/stale recovery 继续恢复 processing task。
- token 轮换：更新连接后所有引用仓库自动使用新 token。
- token 日志泄露：HTTP handler、LibGit2Sharp exception 和审计日志必须做 secret redaction。

## 文件清单

### 新增

- `src/OpenDeepWiki.Entities/Repositories/GitConnection.cs`。
- `src/OpenDeepWiki/Services/Git/GitProviderContracts.cs`。
- `src/OpenDeepWiki/Services/Git/GitHubPatProviderClient.cs`。
- `src/OpenDeepWiki/Services/Git/GitLabPatProviderClient.cs`。
- `src/OpenDeepWiki/Services/Git/GitConnectionService.cs`。
- `src/OpenDeepWiki/Services/Git/GitCredentialResolver.cs`。
- `src/OpenDeepWiki/Services/Repositories/ConnectedRepositoryService.cs`。
- `src/OpenDeepWiki/Services/Repositories/IndexedBranchRemovalService.cs`。
- `src/OpenDeepWiki/Endpoints/GitConnectionEndpoints.cs`。
- `src/OpenDeepWiki/Endpoints/ConnectedRepositoryEndpoints.cs`。
- PostgreSQL migration 和 designer。
- SQLite migration 和 designer。
- 对应 provider、连接、导入、锁、删除和端点测试文件。

### 修改

- `src/OpenDeepWiki.Entities/Repositories/Repository.cs`。
- `src/OpenDeepWiki.Entities/Repositories/RepositoryGenerationLock.cs`。
- `src/OpenDeepWiki.EFCore/MasterDbContext.cs`。
- `src/EFCore/OpenDeepWiki.Postgresql/Migrations/PostgresqlDbContextModelSnapshot.cs`。
- `src/EFCore/OpenDeepWiki.Sqlite/Migrations/SqliteDbContextModelSnapshot.cs`。
- `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs`。
- `src/OpenDeepWiki/Services/Repositories/RepositoryGenerationLockService.cs`。
- `src/OpenDeepWiki/Services/Repositories/BranchGenerationTaskService.cs`。
- `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateService.cs`。
- `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateWorker.cs`。
- `src/OpenDeepWiki/Endpoints/IncrementalUpdateEndpoints.cs`。
- `src/OpenDeepWiki/Program.cs`。
- 前端连接、仓库目录和多选 branch 页面及 API client 文件，具体路径应由前端专项研究确认。

### 兼容但不首改

- `src/OpenDeepWiki/Services/Repositories/GitPlatformService.cs` 保留公开统计兼容用途，但新连接目录不应调用它。
- `src/OpenDeepWiki/Services/GitHub/GitHubAppService.cs` 与相关 endpoints 保留旧 GitHub App 流程。
- `src/OpenDeepWiki/Services/Repositories/RepositoryProcessingWorker.cs` 保留 archive、local 和旧 submit 流程。

## TDD 场景矩阵

| 层 | 场景 | 预期 |
|---|---|---|
| GitConnection | GitHub 有效 PAT | 保存加密 token、external user ID 和 login，响应不含 token |
| GitConnection | GitLab.com 有效 PAT | 使用 `/api/v4/user` 验证并保存 identity |
| GitConnection | Self-Managed HTTPS 有效 PAT | 只向规范化实例 origin 发请求 |
| GitConnection | HTTP、带凭据 URL、query、fragment | 400 |
| GitConnection | loopback、link-local、metadata 或不允许私网 | 阻止请求 |
| GitConnection | token 更新 | 引用仓库后续 clone 使用新 token |
| GitConnection | 删除仍被仓库引用的连接 | 409，不删除仓库 |
| GitHub provider | 超过 100 个仓库 | 跟随全部 Link pages，无重复或漏项 |
| GitHub provider | 超过 100 个分支 | 跟随全部 Link pages |
| GitLab provider | subgroup project | 以 numeric ID 查 branch，不受 path 编码影响 |
| GitLab provider | keyset 可用 | 跟随 next Link 到末页 |
| GitLab provider | Self-Managed 不支持 keyset | 回退 offset pagination |
| Provider | 401、403、404、429、5xx、timeout | 映射稳定错误码，不泄露 token |
| Connected Repository | 两个连接发现同一远端 ID | 数据库只有一个 Repository |
| Connected Repository | 远端 rename/transfer | 更新名称和 URL，Repository ID 不变 |
| Branch selection | 一次选择三个新分支 | 一个 Repository、三个 RepositoryBranch、三个 full tasks |
| Branch selection | 混合已有和新分支 | 只为新分支创建任务 |
| Branch selection | 一个分支在提交前失效 | 整批失败，不留半批数据 |
| Full jobs | 同分支重复入队 | 返回现有活动 task 或 409 |
| Full jobs | 同仓库不同分支入队 | 两个独立 task 都能进入队列 |
| Full jobs | 同仓库不同分支执行 | 两个 branch lock 可并存，工作区互不覆盖 |
| Full jobs | 仓库级 regenerate 活动 | 所有 branch tasks 被阻止 |
| Incremental | branch 不属于 repository | 404/400，不创建 task |
| Incremental | 未授权用户触发 | 403 |
| Incremental | HEAD 不变 | 成功完成，文档不变 |
| Incremental | 文件新增或修改 | 只更新相关文档并推进 commit |
| Incremental | 文件删除 | 删除或刷新对应索引，推进 commit |
| Incremental | commit 不可达 | 安全回退 full file set 或明确失败 |
| Removal | 移除一个空闲 branch | 只删除该 branch 数据和工作区 |
| Removal | 移除最后一个 branch | Repository 和 GitConnection 保留 |
| Removal | branch 有 Pending task | 取消 task、释放 lock、完成删除 |
| Removal | branch 有 Processing task | 409，数据不变 |
| Removal | 其他 branch 存在 | 其他 branch 文档、任务和 workspace 不变 |
| Migration | 旧 Git/Archive/Local repository | 可读且行为不变，connection 字段为空 |
| Migration | 重复稳定远端键 | migration 前检测并报告，不静默合并数据 |

## 建议测试命令

先运行每个新增测试类。

```bash
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitConnectionServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitHubPatProviderClientTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~GitLabPatProviderClientTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~ConnectedRepositoryServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~IndexedBranchRemovalServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~RepositoryGenerationLockServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~BranchGenerationTaskServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~IncrementalUpdateServiceTests
```

再运行仓库级质量门。

```bash
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
dotnet build OpenDeepWiki.sln
cd web && npm run lint
cd web && npm run build
```

E2E 验证必须使用真实的测试 GitHub 用户、GitLab.com 用户和一个 HTTPS Self-Managed 测试实例。
测试凭据只通过环境变量或 secret store 注入。
测试日志和截图不能包含 PAT。

## 实施顺序

1. 先写 provider contract、URL 安全校验和 fake HTTP handler 测试。
2. 实现 GitHub 和 GitLab 连接验证、仓库分页和 branch 分页。
3. 增加 `GitConnection` 与 Repository 稳定远端身份 migration。
4. 实现 Connected Repository 幂等创建和多 branch 原子选择。
5. 修复 branch task 查询和 generation lock 的 branch 维度。
6. 把 `RepositoryAnalyzer` 凭据来源切到 connection resolver，并保留旧字段兼容回退。
7. 补齐 incremental 的授权和 repository/branch 归属校验。
8. 实现 branch-only removal 和工作区清理。
9. 接入 endpoints 和前端。
10. 跑真实 provider E2E，再跑全量 build、test、lint 和前端 build。

## 未解决问题

- 连接是严格个人所有，还是允许部门共享。
- Self-Managed GitLab 是否必须支持内网地址。
- 移除 Processing branch 时产品是否接受只返回 409，还是需要先实现可中断任务。
- 旧 `Repository.AuthPassword` 明文数据是否要在同一发布中迁移，还是只对新连接停止写入。
- 同一 Connected Repository 被不同用户或部门发现时，访问控制是共享记录加 assignment，还是每租户独立记录。

Status: DONE
Summary: 已完成 Git provider、仓库/分支生命周期、full/incremental 流水线、分页身份策略、迁移和 TDD 场景的端到端研究。
Concerns/Blockers: 上述五个产品与安全决策需要在实施计划前确认。
