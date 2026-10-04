# Phase 4 Scout：旧仓库凭据迁移

## 阶段边界

本阶段把 `Repository.AuthAccount` 和 `Repository.AuthPassword` 迁移为 `GitConnection` 引用。
迁移必须支持 SQLite 和 PostgreSQL，必须可重复执行，并且不能在任何日志或报告中输出真实凭据。

全局连接唯一键是 `(Provider, NormalizedServerUrl, ExternalAccountId)`。
所有认证用户都能使用迁移后的连接。
原仓库 `OwnerUserId` 只决定新连接的创建者和维护者；Admin 始终可以维护。

## 旧数据的完整调用面

`AuthAccount` 或 `AuthPassword` 的非生成代码引用只有以下位置。

| 文件和行 | 当前用途 | Phase 4 动作 |
| --- | --- | --- |
| `src/OpenDeepWiki.Entities/Repositories/Repository.cs:71-81` | 实体明文字段。 | contract 时删除。 |
| `src/OpenDeepWiki/Models/RepositorySubmitRequest.cs:33-43` | 普通提交 DTO 接收明文。 | 改为 `GitConnectionId`，删除字段。 |
| `src/OpenDeepWiki/Models/Admin/RepositoryModels.cs:46-54` | Admin 更新 DTO 接收明文。 | 改为连接指派，删除字段。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:61-103` | 验证、统计和创建参数。 | 用连接存在、启用、host 匹配替代。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:305-327` | 列表返回 `HasPassword`。 | 改为 `GitConnectionId` 或 `HasGitConnection`。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:398-407` | 私有可见性依赖 `AuthPassword`。 | 改为可用连接检查。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:575-626` | 创建实体写旧凭据。 | 只写 `GitConnectionId`。 |
| `src/OpenDeepWiki/Services/Admin/AdminRepositoryService.cs:174-190` | Admin 直接替换旧凭据。 | 只允许改派连接。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:133-146` | remote refs 使用旧凭据。 | dual-read 期间通过 resolver。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:192-211` | clone/pull 使用旧凭据。 | dual-read 期间通过 resolver。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:1419-1431` | 构造 LibGit2Sharp credentials。 | contract 时删除 helper。 |
| `tests/OpenDeepWiki.Tests/Services/Repositories/PrivateRepositoryVisibilityPropertyTests.cs` | 21 个测试依赖旧密码语义。 | 重写为连接语义。 |

数据库引用还存在于以下生成或历史文件。

- SQLite 初始 migration：`src/EFCore/OpenDeepWiki.Sqlite/Migrations/20260223135846_Initial.cs:572-608`。
- PostgreSQL 初始 migration：`src/EFCore/OpenDeepWiki.Postgresql/Migrations/20260223135805_Initial.cs:572-608`。
- SQLite 当前 snapshot：`src/EFCore/OpenDeepWiki.Sqlite/Migrations/SqliteDbContextModelSnapshot.cs:1388-1395`。
- PostgreSQL 当前 snapshot：`src/EFCore/OpenDeepWiki.Postgresql/Migrations/PostgresqlDbContextModelSnapshot.cs:1393-1400`。

不要修改历史 initial migration 或历史 designer 文件。
应新增 contract migration 并只更新当前 snapshots。

## 迁移依赖图

```text
Phase 1 expand schema
  -> GitConnections table exists
  -> Repositories.GitConnectionId nullable
  -> Data Protection key ring is durable
  -> credential resolver supports connection path

legacy migration service
  -> load repository legacy credentials
  -> infer provider + normalize server
  -> validate PAT with provider
  -> obtain ExternalAccountId
  -> upsert global GitConnection
  -> assign Repository.GitConnectionId
  -> verify new credential path

dual-read runtime
  -> prefer GitConnection
  -> fallback to legacy fields only when connection is null

switch gate
  -> no migratable repository remains without connection
  -> all connections decrypt and validate

contract schema
  -> remove DTO writes
  -> remove runtime fallback
  -> drop AuthAccount/AuthPassword columns
```

## 关键设计结论

### 不能按旧 PAT 或仓库 owner 去重

全局唯一性取决于提供商返回的 `ExternalAccountId`。
迁移必须验证每个不同旧 PAT，得到外部账户 ID 后再 upsert。

同一外部账户可能由多个不同 PAT 表示。
若目标唯一键已存在，迁移必须复用已有连接，而不是创建 owner 专属连接。
这也表示原仓库 owner 不一定成为最终连接创建者。

确定性的创建者规则如下。

1. 若唯一连接已存在，保留其 `CreatedByUserId`。
2. 若不存在，从匹配仓库中按 `Repository.CreatedAt`、再按 `Repository.Id` 排序，选择第一条仓库的 `OwnerUserId`。
3. 后续其他 owner 的仓库复用该连接。
4. 全体认证用户仍可使用，只有选出的创建者和 Admin 可以维护。

### 不在 EF migration 中解密或联网

EF migration 不能可靠获得运行时 Data Protection key ring，也不应调用 GitHub/GitLab。
DDL migration 只负责 schema。
数据 backfill 必须由应用服务执行。

### 双读必须集中

dual-read 只能存在于 `IGitCredentialResolver`。
`RepositoryAnalyzer`、Git API 客户端和 worker 不得各自实现 fallback。

规则是：有 `GitConnectionId` 时只用连接；连接无效或密文损坏时失败，绝不能静默回退旧明文。
只有 `GitConnectionId == null` 时才允许读取旧字段。
该规则防止攻击者禁用连接后恢复使用旧 PAT。

## 精确文件清单

### 新建

| 文件 | 责任 |
| --- | --- |
| `src/OpenDeepWiki/Services/GitConnections/ILegacyGitCredentialMigrationService.cs` | `MigrateAsync`、状态查询和 dry-run 合同。 |
| `src/OpenDeepWiki/Services/GitConnections/LegacyGitCredentialMigrationService.cs` | 分批读取、provider 验证、全局 upsert、仓库指派和幂等重跑。 |
| `src/OpenDeepWiki.Entities/GitConnections/GitCredentialMigrationRecord.cs` | 可选但建议的持久进度；记录仓库 ID、状态、错误码、尝试时间，不记录秘密。 |
| `tests/OpenDeepWiki.Tests/Services/GitConnections/LegacyGitCredentialMigrationServiceTests.cs` | SQLite 集成测试和 provider mock。 |
| `src/EFCore/OpenDeepWiki.Sqlite/Migrations/<timestamp>_RemoveLegacyRepositoryCredentials.cs` | contract 阶段 SQLite 删列或重建表。 |
| `src/EFCore/OpenDeepWiki.Sqlite/Migrations/<timestamp>_RemoveLegacyRepositoryCredentials.Designer.cs` | SQLite migration metadata。 |
| `src/EFCore/OpenDeepWiki.Postgresql/Migrations/<timestamp>_RemoveLegacyRepositoryCredentials.cs` | contract 阶段 PostgreSQL 删列。 |
| `src/EFCore/OpenDeepWiki.Postgresql/Migrations/<timestamp>_RemoveLegacyRepositoryCredentials.Designer.cs` | PostgreSQL migration metadata。 |

如果迁移是管理命令而不是启动任务，可新增 `src/OpenDeepWiki/Endpoints/Admin/AdminGitConnectionMigrationEndpoints.cs`。
该端点必须在现有 `/api/admin` `AdminOnly` 组下，不能作为公开或普通认证端点。

### 修改：backfill 和 dual-read 发布

| 文件 | 精确修改 |
| --- | --- |
| `src/OpenDeepWiki/Program.cs` | 注册 `ILegacyGitCredentialMigrationService`；不要无条件在每次启动运行外部网络迁移。 |
| `src/OpenDeepWiki/Infrastructure/DbInitializer.cs` | 若使用启动协调，只创建进度表和调用显式迁移 gate；现有初始化顺序见 `src/OpenDeepWiki/Infrastructure/DbInitializer.cs:17-65`。 |
| `src/OpenDeepWiki.EFCore/MasterDbContext.cs` | 若采用进度实体，增加 `DbSet` 和唯一 `RepositoryId` 索引。 |
| `src/OpenDeepWiki/Services/GitConnections/GitCredentialResolver.cs` | 实现 connection-first、null-only legacy fallback，并提供是否正在使用 legacy 的非秘密诊断。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs` | 所有 remote refs、clone、pull 继续只调用 resolver。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs` | 停止新写旧字段；列表和私有可见性改用连接；新仓库只接受连接 ID。 |
| `src/OpenDeepWiki/Models/RepositorySubmitRequest.cs` | 增加 `GitConnectionId`，先保留旧字段仅供兼容反序列化，并标记不再使用。 |
| `src/OpenDeepWiki/Models/Admin/RepositoryModels.cs` | 增加连接改派字段，停止旧字段写入。 |
| `tests/OpenDeepWiki.Tests/Services/Repositories/RepositorySourceSubmitTests.cs` | 验证新写路径绝不填旧字段。 |
| `tests/OpenDeepWiki.Tests/Services/Repositories/RepositoryAnalyzerSourceTests.cs` | 验证 connection-first 和 null-only fallback。 |
| `tests/OpenDeepWiki.Tests/Services/Repositories/PrivateRepositoryVisibilityPropertyTests.cs` | 把 21 个旧密码测试收敛为连接存在、启用和可解析规则。 |

### 修改：contract 发布

| 文件 | 精确修改 |
| --- | --- |
| `src/OpenDeepWiki.Entities/Repositories/Repository.cs` | 删除 `AuthAccount`、`AuthPassword`。 |
| `src/OpenDeepWiki/Models/RepositorySubmitRequest.cs` | 删除旧字段。 |
| `src/OpenDeepWiki/Models/Admin/RepositoryModels.cs` | 删除旧字段。 |
| `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs` | 删除旧校验、参数和 `HasPassword`；只用连接语义。 |
| `src/OpenDeepWiki/Services/Admin/AdminRepositoryService.cs` | 删除旧凭据写入。 |
| `src/OpenDeepWiki/Services/GitConnections/GitCredentialResolver.cs` | 删除 legacy fallback。 |
| `src/OpenDeepWiki/Infrastructure/DbInitializer.cs` | 为现有 SQLite/PostgreSQL 数据库加入幂等 contract 升级，或完成向标准 EF migration 的切换。 |
| `src/EFCore/OpenDeepWiki.Sqlite/Migrations/SqliteDbContextModelSnapshot.cs` | 删除旧列。 |
| `src/EFCore/OpenDeepWiki.Postgresql/Migrations/PostgresqlDbContextModelSnapshot.cs` | 删除旧列。 |
| `tests/OpenDeepWiki.Tests/Services/Repositories/PrivateRepositoryVisibilityPropertyTests.cs` | 删除所有 `AuthPassword` fixture 和断言。 |

### 删除

没有必须删除的完整源文件。
应删除 `RepositoryAnalyzer.BuildCredentials` 方法，位置为 `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:1419-1431`。
应删除 DTO 和实体中的旧字段，但不能改写历史 migration。

## 现有测试数量和迁移缺口

| 文件 | 当前数量 | 与迁移相关的现有覆盖 | 缺口 |
| --- | ---: | --- | --- |
| `RepositorySourceSubmitTests.cs` | 26 | 提交兼容、重复仓库、新分支、软删除。 | 无 backfill、幂等、全局 external account 去重、rollback。 |
| `PrivateRepositoryVisibilityPropertyTests.cs` | 21 | 全部围绕 `AuthPassword` 是否为空。 | 需要替换为 connection 可用性；当前测试会阻止删列。 |
| `RepositoryAnalyzerSourceTests.cs` | 16 | clone/workspace 和本地来源。 | 无 dual-read 优先级、连接损坏不回退、秘密清洗。 |
| `ConfigEncryptionPropertyTests.cs` | 10 | 旧 Chat AES。 | 与 Data Protection 迁移无关，不应作为验收证据。 |
| `GitHubAppInstallationModelTests.cs` | 1 | EF 外键形状。 | 无 migration schema parity 或 rollback。 |

至少新增以下迁移测试场景。

1. 无旧凭据的公开仓库保持 `GitConnectionId = null`。
2. GitHub PAT 验证后创建连接并指派仓库。
3. GitLab PAT 验证后创建连接并指派仓库。
4. 同一 external account 的多个 PAT 和多个 owner 只产生一个连接。
5. 已存在全局连接时保留原创建者。
6. 新连接创建者按仓库时间和 ID 确定，不受查询顺序影响。
7. 中途失败后重跑不重复连接、不重复审计、不改变已迁移仓库。
8. provider 401/403/429/timeout 只保存稳定错误码。
9. connection 有值但禁用、删除或密文损坏时不回退旧字段。
10. 任何数据库、进度记录、API、日志和异常中都不存在 canary PAT。
11. SQLite 和 PostgreSQL expand/contract 后模型一致。
12. contract 前备份可恢复到双读版本。
13. contract 后恢复需要数据库和 key ring 同时回退。

## 备份、发布和回滚门禁

### Expand 前

- 备份数据库。
- 备份并验证 Data Protection key ring。
- 记录应用版本、数据库 provider、活动仓库数和带旧凭据仓库数。
- 在隔离环境恢复两份备份并验证解密。

### Backfill

- 分批事务执行，建议每批 50 至 200 个仓库。
- 每个 PAT 验证必须有超时和取消。
- 每个仓库的迁移结果必须持久化，不能只写日志。
- dry-run 只报告计数、provider、状态和错误码。
- 禁止报告账户 token、密文或上游正文。

### Switch

- 带旧凭据的活动远程 Git 仓库必须全部有连接，或进入人工隔离清单。
- 所有引用连接必须可解密且 provider/server 与仓库 URL 匹配。
- 至少完成一次 clone、pull、remote branch list、增量更新和重新生成验证。
- 关闭 legacy fallback 后观察一个完整定时更新周期。

### Contract

- 在独立发布执行。
- SQLite 删列通常需要重建 `Repositories` 表，必须保留所有索引、外键和新增列。
- PostgreSQL 可显式 `DROP COLUMN`，但仍需先通过 switch gate。
- contract 完成后不能只回滚二进制，因为旧二进制需要已删除的列。

### 回滚

- Backfill 前后都保留旧字段原值，直到 contract。
- dual-read 版本回滚只需重新打开 legacy fallback。
- 不能把 Data Protection 密文批量解密回旧明文列作为常规回滚方法。
- contract 后只能恢复 contract 前数据库备份和对应 key ring，再部署旧二进制。

## 重复和危险边缘

1. `DbInitializer` 的手写 DDL和 EF migration 是两套 schema 来源。
任一方漏改都会造成新库与升级库差异。

2. `EnsureCreatedAsync` 不会应用后续 EF migrations：`src/OpenDeepWiki/Infrastructure/DbInitializer.cs:23-27`。
不能把 migration 文件存在当作运行时升级证据。

3. `RepositoryService.GetListAsync` 用 `HasPassword` 暴露旧语义：`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:302-327`。
contract 前前端和 API 都必须迁移到 `HasGitConnection`，否则删列后编译或行为失败。

4. 私有可见性只检查密码非空：`src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:398-407`。
新规则必须检查连接存在、启用和可解析，不能只检查外键非空。

5. 同一 PAT 可以换发，PAT 哈希不能作为唯一外部身份。
必须以 provider 验证返回的 external account ID 去重。

6. 若一个旧 PAT 已撤销，无法得到 external account ID。
该仓库不能自动创建不可信连接，必须进入人工修复清单并保留旧数据到 contract gate。

7. 迁移后的连接是 Shared Workspace 全局资源。
这会扩大私有仓库访问面，但符合已确认产品决定；审计必须记录迁移和后续使用。

8. 连接创建者软删除后，连接仍被全体用户使用，但只能由 Admin 维护。
迁移不能因为 owner 已软删除而丢弃仓库；应将此情况标为 Admin 托管。

## Phase 4 完成门禁

- `rg "AuthAccount|AuthPassword" src --glob '*.cs' --glob '!**/Migrations/*'` 没有运行时代码命中。
- 当前 snapshots 不再包含旧列。
- 两个 provider 的 contract migration 和升级路径都通过。
- 迁移服务可重复运行，结果稳定。
- 所有活动远程私有仓库都引用可用连接，或有明确阻断记录。
- connection-first 路径经过 clone、pull、branch list、增量更新和重新生成验证。
- canary PAT 不出现在数据库非秘密列、日志、审计、异常或 API JSON。
- contract 前数据库和 key ring 联合恢复演练成功。

## 未决风险

- 撤销或过期的旧 PAT 无法自动解析 external account ID，需要 Admin 处理。
- 现有运行时 schema 机制不是标准 EF migration 流程，SQLite contract 的表重建风险较高。
- 如果迁移时已有相同全局连接，原 owner 会失去维护权，但仍可使用；这是全局唯一和创建者维护规则的必然结果。
