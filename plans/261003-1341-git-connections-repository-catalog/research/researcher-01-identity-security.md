# Git Connections：身份、授权和密钥安全研究

## 已确认的产品边界

- 产品只有一个 Shared Workspace，不创建 Organization 或 Workspace 实体。
- 每个已登录用户都可以创建 Git Connection，也可以使用任意启用的 Git Connection。
- 只有连接创建者和 `Admin` 可以更改、轮换、禁用或删除该连接的凭据。
- 第一阶段只支持 GitHub PAT 和 GitLab PAT。
- PAT 只在服务端接收、加密、解密和使用。
- 现有仓库的 `AuthAccount` 和 `AuthPassword` 必须迁移到 Git Connection。
- 本研究不改变公开仓库文档的读权限，也不引入更细的连接使用权限。

## 当前实现证据

### 身份和角色

- `UserContext.UserId` 从 JWT 的 `ClaimTypes.NameIdentifier` 读取当前用户 ID，`UserContext.User` 提供角色检查入口：`src/OpenDeepWiki/Services/Auth/UserContext.cs:17-29`。
- `JwtService.GenerateToken` 写入用户 ID、名称、邮箱和全部角色声明：`src/OpenDeepWiki/Services/Auth/JwtService.cs:22-43`。
- `Program` 注册 JWT Bearer 验证和 `AdminOnly` 策略，策略只要求 `Admin` 角色：`src/OpenDeepWiki/Program.cs:99-118`。
- 全部 `/api/admin` 端点统一使用 `AdminOnly`：`src/OpenDeepWiki/Endpoints/Admin/AdminEndpoints.cs:8-24`。
- 注册用户默认获得 `User` 角色：`src/OpenDeepWiki/Services/Auth/AuthService.cs:71-115`。
- `AuthService.GetUserRolesAsync` 只过滤 `UserRole.IsDeleted`，没有过滤已停用或软删除的 `Role`：`src/OpenDeepWiki/Services/Auth/AuthService.cs:154-159`。
- JWT 内的角色在令牌有效期内保持不变，所以撤销 Admin 权限不会立即使旧令牌失效：`src/OpenDeepWiki/Services/Auth/JwtService.cs:38-43`。

### 仓库所有权和现有授权模式

- `Repository.OwnerUserId` 是必填用户外键，仓库还直接保存 `AuthAccount` 和标注为明文的 `AuthPassword`：`src/OpenDeepWiki.Entities/Repositories/Repository.cs:31-43`、`src/OpenDeepWiki.Entities/Repositories/Repository.cs:71-81`。
- SQLite 和 PostgreSQL 初始迁移都将 `AuthPassword` 建为普通字符串列，并将仓库所有者外键配置为级联删除：`src/EFCore/OpenDeepWiki.Sqlite/Migrations/20260223135846_Initial.cs:572-608`、`src/EFCore/OpenDeepWiki.Postgresql/Migrations/20260223135805_Initial.cs:572-608`。
- 普通仓库提交路径从请求直接接收账户和密码，并把它们写入仓库实体：`src/OpenDeepWiki/Models/RepositorySubmitRequest.cs:33-43`、`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:91-103`、`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:611-626`。
- 克隆路径直接把实体中的账户和密码传给 LibGit2Sharp：`src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:1416-1431`。
- 管理接口可以直接替换仓库中的账户和密码：`src/OpenDeepWiki/Models/Admin/RepositoryModels.cs:46-54`、`src/OpenDeepWiki/Services/Admin/AdminRepositoryService.cs:174-190`。
- 仓库重新生成已经采用“所有者或 Admin”规则：`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:477-489`。
- 向现有仓库添加分支也采用“所有者或 Admin”规则：`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:653-662`。
- 已有授权测试验证匿名用户得到 401、非所有者得到 403、所有者或 Admin 通过：`tests/OpenDeepWiki.Tests/Services/Repositories/BranchGenerationTaskServiceTests.cs:120-174`。

### 生命周期和审计

- 所有聚合根都有软删除、删除时间、更新时间和并发版本字段：`src/OpenDeepWiki.Entities/AggregateRoot.cs:10-60`。
- 用户删除只设置 `IsDeleted`，不会处理用户创建的仓库或其他资源：`src/OpenDeepWiki/Services/Admin/AdminUserService.cs:155-164`。
- 仓库删除也使用软删除，但会先清理关联的生成数据：`src/OpenDeepWiki/Services/Admin/AdminRepositoryService.cs:193-203`。
- 现有 `UserActivity` 是推荐数据，不是安全审计日志；它只覆盖浏览、搜索、收藏、订阅和分析：`src/OpenDeepWiki.Entities/Statistics/UserActivity.cs:9-35`。
- `UserActivity` 的字段也不能表达连接创建、令牌轮换、授权拒绝或连接使用结果：`src/OpenDeepWiki.Entities/Statistics/UserActivity.cs:41-94`。

### 现有加密能力

- `AesConfigEncryption` 使用从同一个字符串派生的固定密钥和固定 IV：`src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:16-30`。
- 它使用 AES-CBC，但没有认证标签，所以不能可靠检测密文篡改：`src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:53-63`、`src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:78-87`。
- 没有配置密钥时，它会使用代码中的默认密钥：`src/OpenDeepWiki/Chat/Config/AesConfigEncryption.cs:20-24`。
- 现有测试只验证往返、前缀和基础幂等性，没有验证随机 nonce、篡改拒绝或密钥轮换：`tests/OpenDeepWiki.Tests/Chat/Config/ConfigEncryptionPropertyTests.cs:60-204`。
- GitHub App 的短期访问令牌也以明文保存在数据库：`src/OpenDeepWiki.Entities/GitHub/GitHubAppInstallation.cs:47-55`、`src/OpenDeepWiki/Services/GitHub/GitHubAppService.cs:125-163`。

## 最小且稳健的数据模型

### `GitConnection`

建议新增一个 `GitConnection : AggregateRoot<string>`，只使用以下字段。

| 字段 | 类型 | 约束和用途 |
| --- | --- | --- |
| `Id` | `string` | 现有 GUID 字符串惯例。 |
| `Name` | `string` | 必填，最多 100 字符，在未删除记录中不区分大小写地唯一。 |
| `Provider` | enum | 只允许 `GitHub` 或 `GitLab`，不要接受任意字符串。 |
| `ServerUrl` | `string` | GitHub 默认为 `https://github.com`，GitLab 默认为 `https://gitlab.com`；规范化为 scheme、host 和可选基路径，不保存用户信息、查询或片段。 |
| `AccountName` | `string?` | LibGit2Sharp HTTPS 用户名；它不是秘密，可以返回给客户端。 |
| `ProtectedToken` | `string` | 只保存平台保护后的负载，永不进入响应 DTO。 |
| `CreatedByUserId` | `string` | 不可变创建者和维护授权主体。 |
| `IsEnabled` | `bool` | 默认 `true`；禁用后拒绝所有新列表、导入、克隆和同步操作。 |
| `LastValidatedAt` | `DateTime?` | 最近一次服务端验证成功时间。 |
| `LastValidationErrorCode` | `string?` | 仅保存归一化错误码，例如 `unauthorized` 或 `network_error`，不保存上游响应正文。 |

不要保存 PAT 尾号、PAT 哈希、作用域正文或上游错误正文。
这些数据不是第一阶段功能所需，而且会扩大秘密旁路和日志泄漏面。

`ProtectedToken` 应配置足够长度的文本列，不要沿用 `AuthPassword` 的 500 字符限制。
数据保护负载包含元数据并会膨胀，建议最大长度 4096。

### `Repository` 关联

在 `Repository` 增加可空 `GitConnectionId` 和导航属性。
外键使用 `DeleteBehavior.Restrict`。
公开仓库、ZIP 和本地目录仓库可以保持 `null`。

不要复制令牌到仓库记录。
后台任务应在每次 Git 操作前按 `GitConnectionId` 读取启用的连接，并在一个最短作用域内解密。

迁移完成并通过验证后，删除 `Repository.AuthAccount` 和 `Repository.AuthPassword`。
在过渡版本中只允许兼容读取，不允许继续写这两个旧字段。

### `GitConnectionAuditEvent`

安全审计不能复用推荐用途的 `UserActivity`。
建议新增只追加的 `GitConnectionAuditEvent`，字段限于 `Id`、`GitConnectionId`、`ActorUserId`、`EventType`、`Outcome`、`RepositoryId?`、`CorrelationId?`、`ErrorCode?` 和 `CreatedAt`。

`EventType` 至少包括 `Created`、`Validated`、`CredentialRotated`、`Enabled`、`Disabled`、`RepositoryAssigned`、`RepositoryUnassigned`、`Used`、`UseDenied` 和 `Deleted`。
审计事件绝不能保存请求体、PAT、受保护密文、Authorization 头、完整上游响应或异常对象序列化结果。

`Used` 事件应按一次用户操作或一次后台任务记录，不要按每个 Git HTTP 请求记录，以免形成高容量日志和可用性风险。

### EF Core 约束和索引

- 在 `IContext` 和 `MasterDbContext` 增加两个 `DbSet`；现有注册位置见 `src/OpenDeepWiki.EFCore/MasterDbContext.cs:7-56` 和 `src/OpenDeepWiki.EFCore/MasterDbContext.cs:67-113`。
- 为 `GitConnection.CreatedByUserId` 建外键并使用 `Restrict`，防止未来的物理用户删除静默删除共享连接。
- 为 `Repository.GitConnectionId` 建外键并使用 `Restrict`，删除前必须显式解除或改派仓库。
- 建 `GitConnection(NameNormalized, IsDeleted)` 唯一索引，或在两个数据库都能稳定表达的情况下使用只覆盖活动记录的过滤唯一索引。
- 建 `GitConnection(Provider, ServerUrl, IsEnabled)` 索引，支持连接目录和平台筛选。
- 建 `GitConnectionAuditEvent(GitConnectionId, CreatedAt)` 和 `(ActorUserId, CreatedAt)` 索引。
- 保留 `Version` 乐观并发检查，令牌轮换、禁用和删除应在并发冲突时返回 409，防止后写覆盖前写。

## 权限矩阵

| 操作 | 匿名 | 普通用户 | 创建者 | Admin | 后台 Worker |
| --- | --- | --- | --- | --- | --- |
| 查看连接目录和非秘密元数据 | 401 | 允许 | 允许 | 允许 | 不适用 |
| 创建连接并提交 PAT | 401 | 允许 | 允许 | 允许 | 禁止 |
| 验证连接 | 401 | 只允许使用，不允许手动验证他人连接 | 允许 | 允许 | 可在使用时验证 |
| 使用启用连接列仓库或导入 | 401 | 允许 | 允许 | 允许 | 按任务引用允许 |
| 将连接指派给仓库 | 401 | 仅对自己可变更的仓库 | 仅对自己可变更的仓库 | 允许 | 禁止 |
| 改名或修改非秘密元数据 | 401 | 403 | 允许 | 允许 | 禁止 |
| 替换或轮换 PAT | 401 | 403 | 允许 | 允许 | 禁止 |
| 启用、禁用或删除连接 | 401 | 403 | 允许 | 允许 | 禁止 |
| 读取 PAT 或密文 | 禁止 | 禁止 | 禁止 | 禁止 | 只通过内部服务得到短生命期明文 |
| 查看安全审计 | 401 | 仅自己的操作，若产品需要 | 可查看本连接 | 全部 | 禁止 |

所有连接查询必须先要求认证。
“所有用户可使用”不等于“所有用户可看见令牌”或“所有用户可维护连接”。

维护授权应集中在一个 `CanMaintain(connection, userContext)` 规则中：`connection.CreatedByUserId == userContext.UserId || userContext.User.IsInRole("Admin")`。
连接使用授权应集中在一个 `CanUse` 规则中：用户已认证、连接未软删除且 `IsEnabled`。
仓库指派还必须单独检查调用者是否有权修改目标仓库，沿用现有“仓库所有者或 Admin”模式。

不要相信客户端传入的 `CreatedByUserId`、角色、`HasCredential` 或连接状态。
这些值必须从 JWT 和数据库得到。

## PAT 处理和加密策略

### 选择

使用 ASP.NET Core Data Protection 的专用 protector，例如固定 purpose `OpenDeepWiki.GitConnections.Pat.v1`。
这是当前 ASP.NET Core 平台能力，不需要新增密码学依赖。

不要复用 `AesConfigEncryption`。
固定 IV、无认证 CBC 和默认密钥不满足 PAT 的机密性、完整性和生产密钥要求。

### 密钥环

- 生产环境必须显式配置持久化密钥环，容器重启后仍能解密旧连接。
- 多副本部署必须共享同一密钥环，并使用相同的 application name。
- 密钥环存储必须位于数据库之外，并由部署环境的证书、KMS 或等效主密钥保护。
- 应用启动时如果处于生产环境但没有持久化且受保护的密钥环，应快速失败，不能回退到代码默认密钥。
- 数据库备份和密钥环备份属于同一个恢复单元；任何一个缺失都不能完成恢复。

### 密钥轮换

Data Protection 负载自带用于解密的密钥标识，所以 `GitConnection` 不需要另存 `KeyId`。
平台生成新活动密钥后，新写入会自动使用新密钥，旧密钥保留用于读取旧密文。

连接凭据在成功读取时可以检测旧 protector 负载并重新保护，也可以由 Admin 启动作业批量重新保护。
批量轮换必须逐行事务化，先验证新密文可解密，再提交该行，不能一次性覆盖全表。

旧密钥只有在以下条件全部成立后才可撤销：全部连接已重保护、数据库与密钥环备份已更新、恢复演练成功、回滚窗口已结束。

### 明文生命周期

- PAT 只通过 TLS 请求体进入服务端。
- DTO 和日志的默认字符串化不能包含 PAT 字段。
- Controller/endpoint 把 PAT 立即传给秘密服务，不在实体 DTO 或缓存中回传。
- 明文只在验证或 Git 操作的局部变量中存在，不写文件、不写环境变量、不写队列负载、不写异常消息。
- 不把带 PAT 的 URL 传给 LibGit2Sharp；继续使用凭据回调或 `UsernamePasswordCredentials`。
- 禁止记录 Git 命令、HTTP Authorization 头或上游响应正文。
- 更新接口中缺少 `token` 表示“保留原凭据”；只有显式的替换操作才轮换，避免空字符串意外清除。
- 删除或禁用连接后，正在运行的单次操作可以完成，但后续操作必须重新读取状态并失败。

### 提供商验证

- GitHub 和 GitLab 分别使用固定提供商适配器，不根据用户输入拼接任意验证 URL。
- `ServerUrl` 仅允许 HTTPS，并拒绝 loopback、link-local、私网地址和解析后落入这些网段的主机，除非未来明确支持自托管实例并设计单独的网络策略。
- 创建和轮换时调用提供商身份端点验证 PAT，并将可访问账户与 `AccountName` 做一致性检查。
- 上游 401/403、速率限制、DNS、TLS 和超时错误要转为稳定错误码，不能把响应正文返回前端。
- 使用最小所需 PAT 权限；界面说明 Git 读取和仓库元数据读取所需权限，但服务端不能把客户端声明的 scope 当作事实。

## 生命周期规则

### 创建

1. 从 JWT 得到创建者。
2. 验证名称、提供商和规范化后的服务地址。
3. 用短超时调用提供商验证 PAT。
4. 验证成功后才保护并持久化 PAT。
5. 在同一事务中写连接和 `Created`、`Validated` 审计事件。
6. 响应只返回 `hasCredential: true`、验证时间和非秘密元数据。

如果验证失败，不保存连接，也不保存 PAT 或上游正文。

### 使用

1. 认证用户选择连接。
2. 服务端加载未删除且启用的连接。
3. 若为仓库变更，再检查仓库所有者或 Admin 权限。
4. 内部秘密服务解密 PAT，并立即构造提供商 API 或 Git 凭据。
5. 记录归一化结果审计事件。

### 轮换

只有创建者或 Admin 可以提交新 PAT。
先验证新 PAT，再替换密文和更新时间。
如果验证或数据库提交失败，旧密文必须保持可用。
成功后记录 `CredentialRotated`，但不记录新旧 PAT 的任何指纹。

### 禁用、删除和仓库改派

默认操作是禁用，不是删除。
禁用保留仓库引用和审计记录，并让新 Git 操作以明确的 `connection_disabled` 失败。

软删除连接前必须满足以下任一条件：没有活动仓库引用，或调用者在同一个事务中提供替代连接并完成全部改派。
不要级联删除仓库。

仓库改派必须验证新连接的 provider 和 server host 与仓库 Git URL 匹配。
改派只改变凭据来源，不改变仓库所有者、公开状态或生成数据。

### 创建者停用或删除

现有用户删除是软删除，因此保留 `CreatedByUserId` 作为不可变审计信息。
创建者被停用或软删除后，连接继续可供 Shared Workspace 使用，但只有 Admin 可以维护。

不自动把维护权交给其他普通用户。
如果未来支持物理删除用户，应先要求 Admin 选择新创建者，或把连接进入 Admin 托管状态；当前外键应使用 `Restrict` 阻止无意删除。

## 存量仓库迁移和回滚

### 迁移原则

不能只用一次不可逆 migration 把明文列转换为密文。
EF migration 在设计时不能可靠访问生产 Data Protection 密钥环，而且失败后可能留下部分转换数据。

采用 expand、backfill、switch、contract 四步。

### 1. Expand

- 新增 `GitConnections`、`GitConnectionAuditEvents` 和可空 `Repositories.GitConnectionId`。
- 保留 `AuthAccount` 和 `AuthPassword`。
- 部署双读代码：优先读取连接；连接为空时读取旧字段。
- 所有新建和编辑流程只写 Git Connection，不再写旧凭据列。

### 2. Backfill

- 运行前同时备份数据库和 Data Protection 密钥环，并记录备份时间、应用版本和行数。
- 使用应用内迁移命令运行，因为它能访问与运行时相同的 protector。
- 只处理 `GitConnectionId IS NULL` 且旧凭据非空的 Git 仓库。
- 按规范化 provider、server host、`OwnerUserId`、`AuthAccount` 和旧 PAT 的进程内安全摘要分组，避免不必要的重复连接。
- 摘要只用于当前迁移进程去重，不能写入数据库或日志。
- 每组创建一个连接，`CreatedByUserId` 使用原仓库 `OwnerUserId`。
- 连接名使用确定性且不含秘密的名称，并处理冲突，例如 `Migrated GitHub - owner/repo - <short-id>`。
- 每个仓库更新 `GitConnectionId` 后，立即通过新路径执行只读验证。
- 分批事务提交并写入迁移进度表或安全的结构化进度记录，使命令可重复运行。
- 日志只记录连接 ID、仓库 ID、状态和错误码。

不建议跨不同 `OwnerUserId` 去重，即使 PAT 相同。
这样可以保留创建者维护规则，并避免把一个用户原有的凭据维护权转给另一个用户。

### 3. Switch

- 统计所有带旧凭据的活动仓库，要求每个仓库都有可解密且验证成功的连接。
- 关闭旧字段读取特性开关。
- 观察至少一个完整的定时更新周期，并确认克隆、分支列表、增量更新和重新生成都使用连接。
- 再把 `GitConnectionId` 的业务约束提高到：需要认证的远程 Git 仓库必须非空；公开匿名 Git 仓库可保持空值。

### 4. Contract

- 在独立版本中删除请求和响应中的 `AuthAccount`、`AuthPassword`。
- 删除实体字段和两个数据库中的旧列。
- 更新两个 provider 的 migration 和 model snapshot。
- 此步骤完成后，回滚只能回到仍理解 Git Connection 的版本，不能回到只理解旧明文列的版本。

### 回滚

- Expand 或 Backfill 阶段回滚：重新开启旧字段读取，保留新表，不需要把密文解密回旧列。
- Switch 阶段回滚：重新开启旧字段读取；Backfill 期间禁止清空旧字段，所以旧路径仍可用。
- Contract 前必须建立明确回滚截止点和数据库快照。
- Contract 后如果必须回到旧二进制，应从 Contract 前数据库备份和配套密钥环恢复，不能现场把 PAT 批量写回明文列。
- 恢复演练必须验证至少一个 SQLite 备份和一个 PostgreSQL 备份能在隔离环境中解密连接并完成只读 Git 操作。

## 多用户威胁模型

| 威胁 | 场景 | 影响 | 必需控制 |
| --- | --- | --- | --- |
| 横向越权维护 | 普通用户修改或禁用他人连接 | 凭据劫持或全局中断 | 所有写操作服务端执行创建者或 Admin 检查；IDOR 测试。 |
| 共享使用权扩大 | 任意用户使用迁移后的 PAT 枚举或导入原本只有创建者知道的私有仓库 | 私有代码元数据和内容泄漏 | 这是已确认“所有用户可使用”的直接风险；UI 明示共享范围，审计每次列表和导入，要求最小权限 PAT。 |
| DTO 泄漏 | 实体被直接序列化，返回 `ProtectedToken` | 离线攻击面扩大 | 专用响应 DTO，秘密字段不进入 DTO；序列化测试。 |
| 日志泄漏 | HTTP、Git 或异常日志包含 PAT、请求体、带凭据 URL | 直接凭据泄漏 | 结构化白名单日志、日志清洗、秘密 canary 测试。 |
| 数据库窃取 | 攻击者读取数据库备份或卷 | PAT 被利用 | 认证加密、数据库外密钥环、密钥环单独保护。 |
| 密文篡改 | 攻击者替换或修改 `ProtectedToken` | 错误身份或服务中断 | Data Protection 完整性验证；篡改必须失败关闭。 |
| SSRF | 用户提供伪造 GitLab 地址访问云元数据或内网 | 基础设施凭据泄漏 | 第一阶段固定 GitHub/GitLab SaaS host，HTTPS 和解析后 IP 校验。 |
| 路径混淆 | 连接 host 与仓库 URL host 不同 | PAT 发给攻击者主机 | 指派和使用时校验 provider、scheme、host 和端口。 |
| 删除级联 | 删除创建者或连接导致仓库被删除 | 数据丢失 | 两个外键都用 `Restrict`；用户当前只软删除；显式改派。 |
| 旧 JWT 权限 | 被撤销 Admin 的旧 JWT 仍有 Admin claim | 越权维护连接 | 敏感维护操作可查询当前角色状态，或缩短 JWT 生命周期并引入会话版本。 |
| 并发轮换 | 两个维护者同时轮换令牌 | 新凭据被旧写覆盖 | 使用 `Version` 并发令牌并返回 409。 |
| 后台任务陈旧秘密 | 队列消息携带 PAT，轮换后仍继续使用 | 秘密扩散和撤销失效 | 队列只保存连接 ID；执行时加载状态并解密。 |
| 备份不可恢复 | 数据库恢复但密钥环丢失 | 所有私有仓库失去更新能力 | 联合备份、保留旧密钥、定期恢复演练。 |

## TDD 验证清单

### 先写的授权测试

1. 匿名用户对连接列表、创建、使用和维护端点都得到 401。
2. 任意已登录用户都能创建连接，并且服务端忽略伪造的 `CreatedByUserId`。
3. 任意已登录用户都能使用启用连接列仓库和导入。
4. 非创建者不能改名、轮换、禁用或删除连接，均得到 403。
5. 创建者可以维护自己的连接。
6. Admin 可以维护任何连接。
7. 普通用户不能借连接使用权修改不属于自己的仓库。
8. 已删除或禁用连接不能用于新操作。
9. 创建者软删除后普通用户仍可使用连接，但只有 Admin 可以维护。
10. 连接 ID 不存在时返回 404；存在但无维护权时返回 403，且响应不泄漏秘密或额外内部信息。

这些测试可以沿用 `BranchGenerationTaskServiceTests` 的 401、403、所有者和 Admin 测试形状：`tests/OpenDeepWiki.Tests/Services/Repositories/BranchGenerationTaskServiceTests.cs:120-205`。

### 加密和密钥轮换测试

1. 相同 PAT 两次保护得到不同密文。
2. 任意一位密文被修改后解密失败，且失败不回退为明文。
3. 错误 application name 或 purpose 不能解密。
4. 旧密钥生成的密文在新密钥启用后仍能解密。
5. 重保护后只依赖新密钥也能解密。
6. 生产环境缺少持久密钥环配置时应用启动失败。
7. 数据库实体、API JSON、审计表和应用日志中都找不到测试 canary PAT。
8. `ProtectedToken` 永不出现在响应 DTO，即使调用者是创建者或 Admin。

### 提供商和网络测试

1. GitHub 连接只把 PAT 发给允许的 GitHub host。
2. GitLab 连接只把 PAT 发给允许的 GitLab host。
3. HTTP、loopback、link-local、私网 IP、用户信息 URL、重定向到非允许 host 都被拒绝。
4. 上游 401、403、429、超时、DNS 和 TLS 失败映射为稳定错误码。
5. 上游错误正文包含 canary PAT 时，API、日志和审计仍不包含该值。
6. 仓库 URL 与连接 host 不匹配时不能指派或使用。

### 生命周期和并发测试

1. 新 PAT 验证失败时旧密文保持不变。
2. 两个并发轮换请求中只有一个成功，另一个得到 409。
3. 有活动仓库引用时软删除被拒绝。
4. 在同一事务中改派全部仓库后可以软删除旧连接。
5. 禁用连接不会删除仓库或生成数据。
6. 后台任务只序列化连接 ID，不序列化 PAT 或密文。
7. 审计事件只追加，连接软删除后仍可按连接 ID 查询。

### 迁移测试

1. SQLite 和 PostgreSQL migration 都创建相同字段、索引和 `Restrict` 外键。
2. 无凭据公开仓库保持 `GitConnectionId = null`。
3. 每个带旧凭据的远程仓库都得到连接引用。
4. 同一所有者、同一平台和同一旧凭据可以安全去重。
5. 不同所有者即使凭据相同也不会合并。
6. Backfill 中途失败后重跑不会创建重复连接或改变已迁移结果。
7. 迁移日志和错误日志不包含 canary PAT、账户密码或密文。
8. 双读阶段新连接优先，旧字段只在连接为空时使用。
9. 关闭旧字段读取后，任何未迁移私有仓库都会在发布门禁中被发现。
10. 数据库和密钥环联合恢复后能解密并完成只读 Git 操作。

## 建议实施顺序

1. 先建立授权测试、秘密 canary 测试和提供商 host 安全测试。
2. 增加 Data Protection 生产配置和秘密服务，并先验证密钥环重启及多副本行为。
3. 增加实体、EF 映射、审计表和两个数据库的 expand migration。
4. 增加连接 CRUD、验证和共享使用 API，全部使用专用 DTO。
5. 把 Git API、克隆、增量更新和重新生成统一改为按连接 ID 取凭据。
6. 部署双读版本并运行可恢复 Backfill。
7. 完成完整周期验证后关闭旧字段读取。
8. 在独立发布中删除旧 DTO、实体字段和数据库列。

## 假设

- Shared Workspace 中的所有已登录用户都被视为可信协作者，可以使用任何连接访问该 PAT 能访问的仓库。
- 第一阶段只支持 GitHub.com 和 GitLab.com SaaS，不支持自托管 GitHub Enterprise 或 GitLab。
- “使用连接”包括列出可访问仓库、导入仓库、克隆、拉取和读取远程元数据。
- 连接创建者是维护授权主体，不是独占使用者。
- 用户删除继续使用现有软删除语义。
- 部署环境可以提供持久且受保护的 Data Protection 密钥环。

## 风险和待确认项

1. “所有用户可使用任意连接”会让普通用户使用其他人的 PAT 枚举和导入私有仓库。
这是产品决定带来的真实访问扩张，不是加密可以消除的风险。

2. 如果必须支持自托管 GitLab 或 GitHub Enterprise，固定 host 防 SSRF 模型必须改为管理员维护的 allowlist，并加入 DNS rebinding 防护。

3. 当前 JWT 角色不会在角色撤销后立即失效。
如果连接维护被视为高风险操作，应在每次维护时查询当前数据库角色，或引入用户会话版本。

4. 当前 `Repository` 所有者外键在物理用户删除时使用级联删除。
虽然现有用户服务只软删除，但未来任何物理清理任务都可能删除仓库，实施连接功能时应一并把该外键改为 `Restrict` 并验证升级影响。

5. 现有 OAuth、GitHub App 和系统设置仍有其他明文秘密存储路径。
它们不属于本功能范围，但新 Git Connection 不能复制这些模式。
