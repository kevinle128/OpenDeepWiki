# Phase 1 Scout：安全和数据基础

## 阶段边界

本阶段只建立 Git Connection 的安全数据基础、全局身份唯一性、秘密保护和基础授权规则。
产品只有一个 Shared Workspace。
所有已认证用户都可以使用连接，也可以新增和管理仓库分支。
只有连接创建者或 `Admin` 可以维护连接元数据和凭据。

全局连接唯一键已确认是 `(Provider, NormalizedServerUrl, ExternalAccountId)`。
软删除后应恢复原连接，不能以同一键创建第二条记录。

## 当前证据和冲突

### 身份和授权

- 当前用户 ID 取自 JWT `NameIdentifier`：`src/OpenDeepWiki/Services/Auth/UserContext.cs:17-29`。
- JWT 把角色写入 token：`src/OpenDeepWiki/Services/Auth/JwtService.cs:22-43`。
- `AdminOnly` 只检查 `Admin` role：`src/OpenDeepWiki/Program.cs:99-118`。
- 仓库重新生成采用“所有者或 Admin”：`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:477-489`。
- 向现有仓库新增分支也采用“所有者或 Admin”：`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:653-662`。
- Branch generation 的入队、重试和取消都复用“所有者或 Admin”：`src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs:33-83`、`src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs:101-155`。

后两项与已确认的“所有认证用户可新增和管理分支”冲突。
实现时必须把分支操作规则改为只要求认证，同时不能误放宽仓库删除、可见性修改、连接维护或其他仓库管理操作。

### 当前凭据路径

- `Repository` 直接保存 `AuthAccount` 和明文 `AuthPassword`：`src/OpenDeepWiki.Entities/Repositories/Repository.cs:71-81`。
- 提交请求直接接收两个字段：`src/OpenDeepWiki/Models/RepositorySubmitRequest.cs:33-43`。
- `RepositoryService.SubmitAsync` 验证并传递旧字段：`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:61-103`。
- `RepositoryService.CreateRepositoryAsync` 把旧字段写入实体：`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:575-626`。
- `RepositoryAnalyzer.BuildCredentials` 直接把旧字段交给 LibGit2Sharp：`src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:1416-1431`。
- `GetRemoteBranchHeadCommitAsync` 和 `PrepareWorkspaceAsync` 都调用该 helper：`src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:133-146`、`src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:192-211`。
- 管理端仍能直接更新旧凭据：`src/OpenDeepWiki/Models/Admin/RepositoryModels.cs:46-54`、`src/OpenDeepWiki/Services/Admin/AdminRepositoryService.cs:174-190`。

### 加密能力

- 现有 `AesConfigEncryption` 使用固定 IV：`src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:16-30`。
- 它使用无认证标签的 AES-CBC：`src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:53-63`、`src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:78-87`。
- 没有配置时它使用代码内默认密钥：`src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:20-24`。
- 它只在 Chat 服务中注册为 `IConfigEncryption`：`src/OpenDeepWiki/Chat/ChatServiceExtensions.cs:50-56`、`src/OpenDeepWiki/Chat/ChatServiceExtensions.cs:82-88`。

Phase 1 必须使用 ASP.NET Core Data Protection。
不能复用或扩展 `AesConfigEncryption`。
Web SDK 已包含 Data Protection 平台能力，因此 `src/OpenDeepWiki/OpenDeepWiki.csproj:1-45` 不需要新增加密包。

### 数据库升级现实

- `MasterDbContext` 是共享模型和索引定义入口：`src/OpenDeepWiki.EFCore/MasterDbContext.cs:7-56`、`src/OpenDeepWiki.EFCore/MasterDbContext.cs:115-143`。
- 运行时先调用 `EnsureCreatedAsync`，而不是 `Database.MigrateAsync`：`src/OpenDeepWiki/Infrastructure/DbInitializer.cs:17-27`。
- 现有数据库升级由 `MigrateSqliteAsync` 和 `MigratePostgresqlAsync` 的手写 DDL 完成：`src/OpenDeepWiki/Infrastructure/DbInitializer.cs:38-51`、`src/OpenDeepWiki/Infrastructure/DbInitializer.cs:295-324`、`src/OpenDeepWiki/Infrastructure/DbInitializer.cs:566-595`。
- 应用启动时总会调用 `DbInitializer.InitializeAsync`：`src/OpenDeepWiki/Program.cs:413-421`。

因此只生成 EF migration 不会升级现有运行库。
Phase 1 必须同时更新 EF 模型、两个 provider 的 migration/snapshot 和 `DbInitializer` 的幂等 DDL，或先把项目统一迁移到 `Database.MigrateAsync`。
在本阶段内，最小风险路径是保持当前机制并补齐两套 DDL。

## 依赖图

```text
JWT / IUserContext
  -> GitConnection authorization policy
     -> connection create/read/use/maintain decisions

IDataProtectionProvider
  -> IGitConnectionSecretProtector
     -> protect PAT on create/rotate
     -> unprotect PAT only for provider API and LibGit2Sharp

Git provider validator
  -> normalize server URL
  -> validate PAT
  -> return ExternalAccountId + account display name
     -> GitConnection global unique key

GitConnection entity
  -> Repository.GitConnectionId
  -> RepositoryAnalyzer credential resolver
  -> audit events

MasterDbContext
  -> SQLite model/migration/snapshot/manual DDL
  -> PostgreSQL model/migration/snapshot/manual DDL
```

## 精确文件清单

### 新建

| 文件 | 责任 |
| --- | --- |
| `src/OpenDeepWiki.Entities/GitConnections/GitConnection.cs` | `GitProvider` enum、规范化身份字段、`ProtectedToken`、创建者、启用状态和验证状态。 |
| `src/OpenDeepWiki.Entities/GitConnections/GitConnectionAuditEvent.cs` | 只追加安全事件，不保存 PAT、密文或上游正文。 |
| `src/OpenDeepWiki/Services/GitConnections/IGitConnectionSecretProtector.cs` | 只暴露 `Protect` 和 `Unprotect`，不暴露 Data Protection 细节。 |
| `src/OpenDeepWiki/Services/GitConnections/DataProtectionGitConnectionSecretProtector.cs` | 使用固定 purpose `OpenDeepWiki.GitConnections.Pat.v1`。 |
| `src/OpenDeepWiki/Services/GitConnections/IGitConnectionIdentityService.cs` | 规范化 provider/server，并用 PAT 验证得到可信 `ExternalAccountId`。 |
| `src/OpenDeepWiki/Services/GitConnections/GitConnectionIdentityService.cs` | GitHub/GitLab 验证和稳定错误映射；只允许确认的 SaaS host。 |
| `src/OpenDeepWiki/Services/GitConnections/IGitConnectionAuthorizationService.cs` | 集中实现 `CanUse` 和 `CanMaintain`，避免端点各写一次。 |
| `src/OpenDeepWiki/Services/GitConnections/GitConnectionAuthorizationService.cs` | `CanUse`=已认证且连接启用；`CanMaintain`=创建者或 Admin。 |
| `src/OpenDeepWiki/Services/GitConnections/IGitCredentialResolver.cs` | 供所有 Git 调用按连接 ID 获取短生命期 LibGit2Sharp 凭据。 |
| `src/OpenDeepWiki/Services/GitConnections/GitCredentialResolver.cs` | 加载连接、检查启用状态、解密并构造凭据。 |
| `tests/OpenDeepWiki.Tests/EFCore/GitConnectionModelTests.cs` | 唯一索引、外键、Restrict 删除和字段长度。 |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/GitConnectionSecretProtectorTests.cs` | 随机密文、篡改拒绝、purpose 隔离和密钥轮换。 |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/GitConnectionAuthorizationServiceTests.cs` | 认证用户使用、创建者/Admin 维护、非创建者拒绝。 |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/GitConnectionIdentityServiceTests.cs` | URL 规范化、可信外部账户 ID、host allowlist 和错误清洗。 |
| `src/EFCore/OpenDeepWiki.Sqlite/Migrations/<timestamp>_AddGitConnections.cs` | SQLite expand schema。 |
| `src/EFCore/OpenDeepWiki.Sqlite/Migrations/<timestamp>_AddGitConnections.Designer.cs` | SQLite migration metadata。 |
| `src/EFCore/OpenDeepWiki.Postgresql/Migrations/<timestamp>_AddGitConnections.cs` | PostgreSQL expand schema。 |
| `src/EFCore/OpenDeepWiki.Postgresql/Migrations/<timestamp>_AddGitConnections.Designer.cs` | PostgreSQL migration metadata。 |

如果 Phase 1 也交付连接 API，还需新增以下文件。

| 文件 | 责任 |
| --- | --- |
| `src/OpenDeepWiki/Endpoints/GitConnectionEndpoints.cs` | 统一 `.RequireAuthorization()` 的连接端点组。 |
| `src/OpenDeepWiki/Models/GitConnections/GitConnectionModels.cs` | 写入 DTO 可含 PAT，响应 DTO 永不含 PAT 或 `ProtectedToken`。 |
| `src/OpenDeepWiki/Services/GitConnections/IGitConnectionService.cs` | 连接目录、创建、轮换、禁用和恢复接口。 |
| `src/OpenDeepWiki/Services/GitConnections/GitConnectionService.cs` | 事务、唯一冲突、授权和审计。 |

### 修改

| 文件 | 精确修改 |
| --- | --- |
| `src/OpenDeepWiki.Entities/Repositories/Repository.cs` | 增加可空 `GitConnectionId` 和导航属性；Phase 1 保留旧字段以支持双读。 |
| `src/OpenDeepWiki.EFCore/MasterDbContext.cs` | 增加两个 `DbSet`；配置全局唯一 `(Provider, NormalizedServerUrl, ExternalAccountId)`；配置创建者和仓库引用为 `Restrict`；配置审计索引。 |
| `src/OpenDeepWiki/Program.cs` | 配置持久 Data Protection key ring 和 application name；注册 protector、identity、authorization 和 credential resolver；若有 API则映射端点。现有服务注册位置见 `src/OpenDeepWiki/Program.cs:138-179`，端点映射位置见 `src/OpenDeepWiki/Program.cs:395-411`。 |
| `src/OpenDeepWiki/appsettings.json` | 增加非秘密 Data Protection key-ring 路径或配置键；不放主密钥。 |
| `compose.yaml` | 挂载持久 key-ring volume，并通过环境或 secret 提供 key-ring 保护材料。 |
| `src/OpenDeepWiki/Infrastructure/DbInitializer.cs` | 为 SQLite/PostgreSQL 增加幂等建表、索引和 `Repositories.GitConnectionId` 列；不要在此处迁移明文 PAT。 |
| `src/EFCore/OpenDeepWiki.Sqlite/Migrations/SqliteDbContextModelSnapshot.cs` | 加入新实体、索引和外键。 |
| `src/EFCore/OpenDeepWiki.Postgresql/Migrations/PostgresqlDbContextModelSnapshot.cs` | 加入新实体、索引和外键。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs` | 注入 `IGitCredentialResolver`；把静态 `BuildCredentials` 改为 resolver；保护 remote refs、clone 和 pull 三条路径。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs` | 连接 ID 成为新远程仓库和新分支路径的凭据来源；把 `AddBranchToExistingRepositoryAsync` 的 owner/Admin 检查改为只要求认证。 |
| `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs` | 仅对 branch 操作把 `AuthorizeRepositoryMutationAsync` 改成认证检查；名称应改为 `AuthorizeBranchMutationAsync`，避免未来被仓库级危险操作误用。 |
| `tests/OpenDeepWiki.Tests/Services/Repositories/RepositoryAnalyzerSourceTests.cs` | 更新构造器；增加 resolver 被 remote refs、clone、pull 调用的测试。 |
| `tests/OpenDeepWiki.Tests/Services/Repositories/RepositorySourceSubmitTests.cs` | 更新构造器和提交 DTO；把“非所有者不能加分支”的缺失场景补为“任意认证用户可加分支”。 |
| `tests/OpenDeepWiki.Tests/Services/Repositories/BranchGenerationTaskServiceTests.cs` | 将非所有者 403 断言改为认证用户允许；匿名仍为 401。 |
| `tests/OpenDeepWiki.Tests/Chat/Config/TestConfigDbContext.cs` | 增加新 `DbSet`，否则实现 `IContext` 后不能编译。 |
| `tests/OpenDeepWiki.Tests/Chat/Sessions/TestDbContext.cs` | 增加新 `DbSet`，否则实现 `IContext` 后不能编译。 |

### 删除

Phase 1 不删除文件或旧字段。
旧字段删除属于 Phase 4 contract 步骤。

## 需要保护的函数和接口

| 符号 | 当前位置 | 必需保护 |
| --- | --- | --- |
| `RepositoryAnalyzer.GetRemoteBranchHeadCommitAsync` | `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:100-153` | 不能直接读实体明文；通过 resolver；日志不能输出凭据。 |
| `RepositoryAnalyzer.PrepareWorkspaceAsync` | `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:156-215` | clone/pull 前读取启用连接；密文损坏时失败关闭。 |
| `RepositoryAnalyzer.BuildCredentials` | `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:1419-1431` | 删除静态字段读取，替换为内部 resolver。 |
| `IGitPlatformService.GetBranchesAsync` | `src/OpenDeepWiki/Services/Repositories/IGitPlatformService.cs:45-50` | 需要连接上下文或受保护 token，当前只接受 URL。 |
| `IGitPlatformService.GetRepoStatsAsync` | `src/OpenDeepWiki/Services/Repositories/IGitPlatformService.cs:38-43` | 私有仓库统计必须能使用连接。 |
| `IGitPlatformService.CheckRepoExistsAsync` | `src/OpenDeepWiki/Services/Repositories/IGitPlatformService.cs:52-58` | 可见性验证必须使用选定连接，不能只用全局配置 token。 |
| `RepositoryService.SubmitAsync` | `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:26-104` | 不再接收直接 PAT；验证 connection/server 与 Git URL 匹配。 |
| `RepositoryService.GetBranchesAsync` | `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:534-561` | 必须要求认证并按连接读取私有分支。 |
| `RepositoryService.AddBranchToExistingRepositoryAsync` | `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:653-685` | 规则改为所有认证用户；仍需唯一分支和连接可用检查。 |
| `BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync` | `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs:101-131` | 不应继续表达 owner-only branch 策略；拆分命名并限制复用范围。 |

`GitPlatformService` 当前同时从全局配置读取 GitHub、Gitee、GitLab token：`src/OpenDeepWiki/Services/Repositories/GitPlatformService.cs:8-13`。
其 GitHub 和 GitLab API 方法直接加认证头：`src/OpenDeepWiki/Services/Repositories/GitPlatformService.cs:87-119`、`src/OpenDeepWiki/Services/Repositories/GitPlatformService.cs:262-294`。
Phase 1 需要明确连接 token 优先，不能把全局 token 作为私有连接的静默回退。

## 现有测试数量和覆盖缺口

以下数量按 `[Fact]`、`[Theory]` 和 `[Property]` 属性统计。

| 文件 | 当前数量 | 已覆盖 | 缺口 |
| --- | ---: | --- | --- |
| `RepositorySourceSubmitTests.cs` | 26 | 来源提交、重复仓库、新分支、删除和重新生成。 | 没有 Git Connection、全局唯一身份、共享分支权限、秘密不落库测试。 |
| `PrivateRepositoryVisibilityPropertyTests.cs` | 21 | 旧 `AuthPassword` 与私有可见性规则。 | 全部依赖将被废弃的“有密码”模型；需要改为“有可用连接”。 |
| `BranchGenerationTaskServiceTests.cs` | 8 | 其中 4 个覆盖 owner/Admin branch mutation。 | 已确认策略相反；缺普通认证用户成功和停用用户失败。 |
| `RepositoryAnalyzerSourceTests.cs` | 16 | 本地、归档、分支和 workspace 行为。 | 没有凭据 resolver、篡改密文、禁用连接、PAT 日志泄漏。 |
| `ConfigEncryptionPropertyTests.cs` | 10 | 旧 AES 往返和前缀。 | 不能复用；没有随机 nonce、完整性、purpose、key-ring 轮换。 |
| `GitHubAppInstallationModelTests.cs` | 1 | 单一外键映射。 | 没有 Git Connection 唯一索引、Restrict、审计模型测试。 |

至少新增 4 个测试文件。
不要把 Data Protection 新测试加到 `ConfigEncryptionPropertyTests`，因为那会模糊两个不同安全合同。

## 重复和危险边缘

1. `RepositoryService`、`BranchGenerationEndpoints` 和其他仓库操作各自写 owner/Admin 判断。
分支共享规则若只改一处，会产生同一功能不同授权结果。

2. `GitPlatformService.ParseGitUrl` 是私有 helper，并且只取两个 path segment：`src/OpenDeepWiki/Services/Repositories/GitPlatformService.cs:49-85`。
GitLab group/subgroup 项目会被错误解析，规范化逻辑不能复制此实现。

3. `GitPlatformService` 仍包含 Gitee，但确认范围只有 GitHub 和 GitLab。
新连接 enum 不应加入 Gitee，也不应删除旧公开统计能力。

4. 连接全局唯一必须使用提供商返回的外部账户 ID，而不是可变 login、email 或 PAT 哈希。
创建和轮换必须先验证 token，再持久化。

5. SQLite 不支持简单地给现有表追加带约束外键。
`DbInitializer` 的 expand DDL 可先加可空列和索引；外键完整性需要通过表重建或明确依赖 EF model 对新库保证。

6. Data Protection key ring 如果放在容器临时目录，重启会使全部 PAT 不可解密。
生产配置缺失必须启动失败。

7. `Version` 标为 `[Timestamp]`：`src/OpenDeepWiki.Entities/AggregateRoot.cs:38-42`。
需要实际验证 SQLite 和 PostgreSQL 的并发行为，不能假定两个 provider 都自动生成 rowversion。

## Phase 1 完成门禁

- 两个 provider 的新库和旧库升级后都有相同实体、唯一索引和引用列。
- 相同 provider、规范化 server 和 external account ID 不能创建第二条连接，包括原记录软删除的情况。
- PAT 明文不出现在数据库、API 响应、审计或日志。
- 相同 PAT 两次保护得到不同密文，篡改密文不能解密。
- 重启和 key rotation 后旧 PAT 仍可解密。
- 所有认证用户能使用连接并管理 branch。
- 非创建者不能维护连接，Admin 可以。
- 仓库级危险操作没有因为 branch 权限放宽而被放宽。

## 未决风险

- 当前角色在 JWT 中缓存，Admin 撤销不会立即生效。
- Shared Workspace 的连接使用权让所有认证用户获得 PAT 可访问仓库的能力，这是确认的产品风险。
- 自托管 GitLab/GitHub Enterprise 不在 Phase 1；若加入，server URL 唯一性和 SSRF 模型必须重审。
