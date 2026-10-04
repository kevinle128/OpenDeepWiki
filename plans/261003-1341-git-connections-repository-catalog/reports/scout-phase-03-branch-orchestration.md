# Phase 03 Scout：Repository 与 Branch Orchestration

## 目标与验收边界

本阶段把 provider catalog 项连接到现有 `Repository`、`RepositoryBranch`、full generation 和 incremental update 流程。
一个远端只能有一个 Connected Repository。
一个 Connected Repository 可一次选择多个 Indexed Branches。
所有已登录用户都可以给 Connected Repository 增加、生成、增量更新和移除 branch。
不同 branch 的任务独立。
移除 Processing branch 返回 409。
移除 branch 只删除该 branch 的索引和工作区。

## 当前依赖图

```text
RepositoryService.SubmitAsync
  -> Repository row Status=Pending
  -> RepositoryBranch + BranchLanguage
  -> RepositoryProcessingWorker
     -> repository-wide generation lock
     -> foreach branch sequentially
     -> RepositoryBranchProcessor

BranchGenerationEndpoints
  -> BranchGenerationTaskService
     -> repository-wide RepositoryGenerationLock
     -> BranchGenerationTask
  -> BranchGenerationWorker
     -> WikiGenerationCoordinator
     -> RepositoryBranchProcessor(forceFull=true)

IncrementalUpdateEndpoints
  -> IncrementalUpdateService.TriggerManualUpdateAsync
     -> IncrementalUpdateTask
  -> IncrementalUpdateWorker
     -> WikiGenerationCoordinator
     -> IncrementalUpdateService.ProcessIncrementalUpdateAsync
```

旧 submit 流程位于 `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:26-104`。
仓库 worker 的 dispatch 位于 `src/OpenDeepWiki/Services/Repositories/RepositoryProcessingWorker.cs:62-143`。
它在同文件 `:311-350` 顺序处理所有 branches。
共享 branch processor 位于 `src/OpenDeepWiki/Services/Repositories/RepositoryBranchProcessor.cs:29-142`。

full endpoint 位于 `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs:12-28`。
full task service 位于 `src/OpenDeepWiki/Services/Repositories/BranchGenerationTaskService.cs:32-300`。
full worker dispatch 位于 `src/OpenDeepWiki/Services/Repositories/BranchGenerationWorker.cs:51-116`。

incremental endpoint 位于 `src/OpenDeepWiki/Endpoints/IncrementalUpdateEndpoints.cs:23-46`。
incremental enqueue 位于 `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateService.cs:287-349`。
incremental execution 位于同文件 `:125-285`。
incremental worker dispatch 位于 `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateWorker.cs:69-235`。

## 必须修复的 orchestration 缺口

### 新导入不能走 repository Pending worker

`RepositoryService.CreateRepositoryAsync` 在 `RepositoryService.cs:611-650` 写入一个 branch，并把 repository 设为 `Pending`。
`RepositoryProcessingWorker` 对 repository 取一个 lock，然后顺序运行全部 branch。
这与独立 branch jobs 不符。
新 Connected Repository 应以非活动 repository 状态创建，再为每个新增 branch 建独立 `BranchGenerationTask`。
旧 archive、local 和 legacy submit 可继续走 repository worker。

### 当前 lock 实际是 repository-wide

`RepositoryGenerationLock` 只有 `RepositoryId`，见 `src/OpenDeepWiki.Entities/Repositories/RepositoryGenerationLock.cs:19-45`。
`MasterDbContext` 对该字段建唯一索引，见 `src/OpenDeepWiki.EFCore/MasterDbContext.cs:360-362`。
`RepositoryGenerationLockService.TryAcquireAsync` 只按 repository 查锁，见 `src/OpenDeepWiki/Services/Repositories/RepositoryGenerationLockService.cs:80-139`。
`Scope = Branch` 只是标签，不能形成 branch 隔离。

必须新增可空 `BranchId`。
repository lock 与该 repository 的全部 branch locks 互斥。
branch lock 只与相同 branch lock 互斥。
不同 branch locks 可以并存。
全局并发仍由 `WikiGenerationCoordinator` 和 generation slots 限制。

### full task 查重忽略 branch

`BranchGenerationTaskService.FindActiveBranchTaskAsync` 在 `BranchGenerationTaskService.cs:287-300` 接收 `branchId`，但查询没有使用它。
当前同 repository 的任一活动 task 都会阻止其他 branch。
查询必须增加 `item.BranchId == branchId`。
数据库已有 `(BranchId, Status, Mode)` 活动 task 过滤唯一索引，见 `MasterDbContext.cs:355-358`。

### incremental 缺少归属和认证保护

`IncrementalUpdateEndpoints.TriggerIncrementalUpdateAsync` 在 `IncrementalUpdateEndpoints.cs:55-135` 没有 `RequireAuthorization` 或 user check。
它只检查 branch 的 repository ID，但 endpoint group 本身未要求登录。
确认范围要求“所有已登录用户”，所以新策略是 authenticated-only，不是 owner/admin-only。

`IncrementalUpdateService.TriggerManualUpdateAsync` 在 `IncrementalUpdateService.cs:326-339` 没有确认 branch 存在且属于 repository。
`ProcessIncrementalUpdateAsync` 在同文件 `:138-148` 也分别加载 repository 和 branch，没有检查归属。
service 层必须保护这个 invariant，不能只依赖 endpoint。

`BranchGenerationEndpoints.AuthorizeRepositoryMutationAsync` 在 `BranchGenerationEndpoints.cs:101-130` 实施 owner/admin policy。
该 policy 与确认范围冲突。
Phase 3 应改为 authenticated-only，并保留 repository/branch 存在校验。
不要把 owner/admin helper 复制到新增 endpoints。

### branch removal 不存在

当前只有 admin 整仓库删除。
`AdminRepositoryService.DeleteRepositoryDataAsync` 在 `src/OpenDeepWiki/Services/Admin/AdminRepositoryService.cs:267-401` 列出相关数据，但会处理仓库级 assignment、bookmark 和 subscription。
它不能直接复用。

`BranchFullGenerationCleaner` 在 `src/OpenDeepWiki/Services/Repositories/BranchFullGenerationCleaner.cs:14-70` 只删除 catalog/doc 和未完成增量任务，然后把 branch 改为 Pending。
它可复用查询思路，但不能直接用于 removal。

### incremental diff 忽略删除文件

`RepositoryAnalyzer.GetChangedFilesBetweenCommits` 在 `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:1698-1742` 只返回 Added、Modified、Renamed 和 Copied。
Deleted 被明确忽略，见同文件 `:1734-1737`。
如果索引删除语义属于 Phase 3，则必须扩展 changed-file contract，使 wiki layer 能删除旧文档。
如果现有 `IncrementalUpdateAsync` 不能表达 delete，应在本阶段明确采用 branch full fallback。

## 推荐写入流程

```text
POST connected repository with selected branches
  -> resolve GitConnection owned/usable by caller
  -> fetch provider repository by stable ID
  -> validate every selected branch remotely
  -> begin DB transaction
  -> get-or-create Repository by stable remote key
  -> get-or-create RepositoryBranch rows
  -> create BranchLanguage rows
  -> create one BranchGenerationTask per new branch
  -> create one branch lock reservation per new task
  -> commit
```

整个多选请求必须原子。
任一 branch 不存在时不能留下部分 rows 或 tasks。
并发请求依赖数据库唯一索引解决，不依赖先查后写。
捕获唯一冲突后应重新读取已有 repository/branch/task，并返回幂等结果。

一个 repository 被另一个连接再次发现时，稳定 remote key 命中原记录。
服务可以更新可用 `GitConnectionId`，但必须先确认新连接有该 remote 的读取权限。

## 推荐移除流程

```text
DELETE indexed branch
  -> require authenticated user
  -> verify branch belongs to repository
  -> inspect full/incremental tasks and branch lock
     -> Processing => 409
     -> Pending => cancel and release
  -> transactionally remove branch-scoped data
  -> commit
  -> delete branch workspace safely
```

branch-scoped 数据包括以下实体。

- `BranchLanguage`，其 FK 位于 `src/OpenDeepWiki.Entities/Repositories/BranchLanguage.cs:12-16`。
- `DocCatalog` 和 `DocFile`，通过 BranchLanguage 归属。
- `TranslationTask.RepositoryBranchId`，见 `TranslationTask.cs:46-50`。
- `IncrementalUpdateTask.BranchId`，见 `IncrementalUpdateTask.cs:50-55`。
- `BranchGenerationTask.BranchId`，见 `BranchGenerationTask.cs:22-28`。
- `GraphifyArtifact.RepositoryBranchId`，见 `GraphifyArtifact.cs:13-17`。
- `RepositoryProcessingLog.BranchId`，见 `RepositoryProcessingLog.cs:56-60`。
- `RepositoryGenerationLock.BranchId`，由本阶段新增。
- `RepositoryBranch` 本身。

保留 `Repository`、`GitConnection`、RepositoryAssignment、bookmark、subscription、repository statistics 和其他 branches。
删除最后一个 branch 后，Connected Repository 仍存在于 catalog。

工作区路径必须通过 `RepositoryWorkspacePath.ForBranch` 计算，见 `src/OpenDeepWiki/Services/Repositories/RepositoryWorkspacePath.cs:16-27`。
删除前必须验证最终路径仍在 configured repository root 下。
数据库成功后目录删除失败只记录并安排清理，不回滚数据库事务。

## 需要保护的函数与接口

- `ConnectedRepositoryService.ConnectAsync` 必须按 stable remote key 幂等。
- `ConnectedRepositoryService.AddIndexedBranchesAsync` 必须先验证全部 remote branches，再开事务。
- `BranchGenerationTaskService.EnqueueFullGenerationAsync` 必须保证 branch 属于 repository。
- `BranchGenerationTaskService.FindActiveBranchTaskAsync` 必须按 branch 查重。
- `IncrementalUpdateService.TriggerManualUpdateAsync` 必须保证 branch 存在且属于 repository。
- `IncrementalUpdateService.ProcessIncrementalUpdateAsync` 必须再次保证 branch 归属。
- `RepositoryGenerationLockService.TryAcquireAsync` 必须实现 repository/branch 冲突矩阵。
- `RepositoryGenerationLockService.ReleaseAsync`、`HeartbeatAsync` 和 `UnbindAsync` 必须带 BranchId 或以 owner identity 精确命中。
- `IndexedBranchRemovalService.RemoveAsync` 必须对 Processing 返回 409。
- full、incremental、add branch 和 remove branch endpoints 必须 `RequireAuthorization()`。
- confirmed policy 是任一 authenticated user，不得继续 owner/admin gate。
- `RepositoryAnalyzer.CloneRepositoryAsync` 和 `PullRepositoryAsync` 不得保留 `CertificateCheck = true` bypass，见 `RepositoryAnalyzer.cs:1461-1462` 和 `:1562-1564`。

## 文件操作清单

### 创建

- `src/OpenDeepWiki/Services/Repositories/IConnectedRepositoryService.cs`。
- `src/OpenDeepWiki/Services/Repositories/ConnectedRepositoryService.cs`。
- `src/OpenDeepWiki/Services/Repositories/IIndexedBranchRemovalService.cs`。
- `src/OpenDeepWiki/Services/Repositories/IndexedBranchRemovalService.cs`。
- `src/OpenDeepWiki/Endpoints/ConnectedRepositoryEndpoints.cs`。
- `src/OpenDeepWiki/Models/ConnectedRepositories/ConnectedRepositoryModels.cs`。
- `tests/OpenDeepWiki.Tests/Services/Repositories/ConnectedRepositoryServiceTests.cs`。
- `tests/OpenDeepWiki.Tests/Services/Repositories/IndexedBranchRemovalServiceTests.cs`。
- `tests/OpenDeepWiki.Tests/Endpoints/ConnectedRepositoryEndpointsTests.cs`。
- 一组 PostgreSQL migration、designer 和一组 SQLite migration、designer，用于 remote identity 和 branch locks。

### 修改

- `src/OpenDeepWiki.Entities/Repositories/Repository.cs:29-184` 增加 connection 和 stable remote identity 字段。
- `src/OpenDeepWiki.Entities/Repositories/RepositoryGenerationLock.cs:19-45` 增加 `BranchId`。
- `src/OpenDeepWiki.EFCore/MasterDbContext.cs:124-147` 增加 repository remote key 和 branch 索引策略。
- `src/OpenDeepWiki.EFCore/MasterDbContext.cs:355-362` 修改 task 和 lock 唯一约束。
- `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:133-146` 与 `:1419-1432` 改用 connection credential resolver。
- `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:1461-1462` 删除 clone TLS bypass。
- `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:1562-1564` 删除 fetch TLS bypass。
- `src/OpenDeepWiki/Services/Repositories/RepositoryGenerationLockService.cs:9-48` 修改 lock contract。
- `src/OpenDeepWiki/Services/Repositories/RepositoryGenerationLockService.cs:71-236` 实现 branch-aware get/acquire/release/heartbeat。
- `src/OpenDeepWiki/Services/Repositories/BranchGenerationTaskService.cs:37-145` 传递 branch lock identity。
- `src/OpenDeepWiki/Services/Repositories/BranchGenerationTaskService.cs:287-300` 修复 branch 查重。
- `src/OpenDeepWiki/Services/Repositories/BranchGenerationWorker.cs:78-107` 获取 branch lease。
- `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateService.cs:125-349` 补归属检查和 branch lock。
- `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateWorker.cs:120-233` 获取 branch lease。
- `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs:12-155` 改为 authenticated-only policy。
- `src/OpenDeepWiki/Endpoints/IncrementalUpdateEndpoints.cs:23-135` 增加 authentication 并统一错误码。
- `src/OpenDeepWiki/Program.cs:171-179` 注册 orchestration services。
- `src/OpenDeepWiki/Program.cs:396-411` 映射新 endpoints。
- 两个 provider 的 model snapshot。
- 现有 branch task、lock、incremental 和 analyzer tests。

### 删除

本阶段不删除文件。
`RepositoryProcessingWorker` 保留给 legacy submit、archive 和 local source。

## 数据库约束图

```text
Repository
  unique (Provider, ProviderBaseUrl, ProviderRepositoryId) where active
  1 -> many RepositoryBranch

RepositoryBranch
  unique (RepositoryId, BranchName) where active
  1 -> many BranchLanguage
  1 -> many BranchGenerationTask
  1 -> many IncrementalUpdateTask

RepositoryGenerationLock
  repository lock: unique RepositoryId where BranchId is null
  branch lock: unique (RepositoryId, BranchId) where BranchId is not null
```

PostgreSQL 和 SQLite 都支持项目当前 migration 中使用的 filtered indexes。
现有 branch task filtered unique index 位于 `src/EFCore/OpenDeepWiki.Postgresql/Migrations/20260704090000_AddBranchGenerationTasks.cs:148-153` 和 SQLite 同名 migration `:78-80`。
新 migration 应沿用 provider-specific filter quoting。

## 依赖与重复风险

不要建立第二套 branch job 表。
现有 `BranchGenerationTask` 和 `IncrementalUpdateTask` 已表达所需生命周期。
不要建立第二套 ConnectedRepository entity。
现有 `Repository` 被 docs、bookmarks、subscriptions、statistics、assignments 和 tasks 广泛引用。

`RepositoryProcessingWorker.cs:356-540` 保留了一套旧的私有 `ProcessBranchAsync/ProcessLanguageAsync` 实现，而当前主路径在 `:343-349` 已调用 `IRepositoryBranchProcessor`。
这段重复代码是维护风险。
Phase 3 不需要修改它才能交付，但若触及该文件，应删除不可达的重复方法，并以现有测试保护共享 processor。

`RepositoryService.CreateRepositoryAsync` 与 `UserGitHubImportService.ImportAsync` 都手工创建 repository、branch 和 language。
新路径应集中到 `ConnectedRepositoryService`，但不要强行迁移 archive/local 的不同 source 语义。

`AdminRepositoryService.DeleteRepositoryDataAsync` 与新 branch removal 会有清理查询重叠。
只提取真正 branch-scoped 的 helper。
不要让 branch removal 调用 repository-wide cleanup。

## 当前测试基线

相关选定目录共有 140 个测试。
直接覆盖 orchestration 的现有测试如下。

- `BranchGenerationTaskServiceTests.cs`：8 个。
- `IncrementalUpdateServiceTests.cs`：4 个。
- `IncrementalUpdateWorkerTests.cs`：10 个。
- `RepositoryGenerationLockServiceTests.cs`：3 个。
- `WikiGenerationCoordinatorTests.cs`：6 个。
- `RepositoryProcessingStatePropertyTests.cs`：9 个。
- `RepositoryAnalyzerSourceTests.cs`：16 个。
- `RepositoryWorkspacePathTests.cs`：6 个。

上述直接相关集合共 62 个测试。
当前 branch removal 测试为 0。
当前 multi-select branch connect 测试为 0。
当前 authenticated-any-user branch mutation 测试为 0。
当前同 repository 不同 branch lock 并存测试为 0。

`BranchGenerationTaskServiceTests.cs:120-205` 明确测试 owner/admin policy。
这些断言必须按已确认的 authenticated-only policy 更新。
匿名仍应返回 401。
任一已登录用户应通过 mutation authorization。

## 必补测试矩阵

| 范围 | 场景 | 预期 |
|---|---|---|
| Connect | stable key 已存在 | 复用同一 Repository |
| Connect | 两个连接并发连接同一 remote | 唯一一行，另一请求幂等返回 |
| Multi-select | 三个合法新 branch | 三 branch、三 language、三 full tasks |
| Multi-select | 混合已有与新 branch | 只为新 branch 建 task |
| Multi-select | 任一 branch 不存在 | 整批回滚 |
| Authorization | anonymous add/full/incremental/remove | 401 |
| Authorization | 任一 authenticated non-owner | 允许 |
| Ownership invariant | branch 不属于 repository | 404，不创建 task |
| Full dedupe | 同 branch 两次入队 | 一个活动 task |
| Full independence | 同 repository 两 branch 入队 | 两活动 tasks |
| Lock | 两个 branch locks | 可同时取得 |
| Lock | repository lock 后请求 branch lock | 冲突 |
| Lock | branch lock 后请求 repository lock | 冲突 |
| Lock | 同 branch full 与 incremental | 冲突 |
| Worker | 不同 branch 同时 claim | 独立 processing |
| Worker | cluster slots 满 | task 保持 Pending |
| Incremental | branch relation mismatch | 失败且不推进 commit |
| Incremental | deleted file | 删除索引或明确 full fallback |
| Removal | idle branch | 只删除该 branch 数据 |
| Removal | Pending full/incremental | 取消并删除 |
| Removal | Processing full | 409，数据不变 |
| Removal | Processing incremental | 409，数据不变 |
| Removal | 最后 branch | Repository 和 connection 保留 |
| Removal | workspace delete failure | DB 已删除，记录可重试清理 |
| TLS | clone/fetch invalid cert | 失败，不 bypass |
| Compatibility | legacy repository 无 connection | 旧凭据路径仍可运行 |

测试数据库约束时必须使用 SQLite in-memory，而不是只用 EF InMemory provider。
EF InMemory 不会验证 filtered unique indexes、FK cascade 或事务竞争。

## 建议验证命令

```bash
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~ConnectedRepositoryServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~IndexedBranchRemovalServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~BranchGenerationTaskServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~RepositoryGenerationLockServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~IncrementalUpdateServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~IncrementalUpdateWorkerTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~WikiGenerationCoordinatorTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
dotnet build OpenDeepWiki.sln
```

## 建议实现顺序

1. 先增加 stable remote identity 和 branch-aware lock migrations。
2. 用 SQLite tests 固定 unique 和 lock 冲突矩阵。
3. 修复 full task branch 查重和 worker lease identity。
4. 修复 incremental 归属、认证和 worker lease identity。
5. 实现幂等 Connected Repository 和多 branch 原子写入。
6. 实现 branch-only removal。
7. 移除 LibGit2Sharp TLS bypass，并验证 GitHub、GitLab.com 和 Self-Managed clone。
8. 更新 endpoints 和现有 owner/admin tests。
9. 运行全量 tests 与 build。

## 未解决问题

- deleted file 的增量 contract 是扩展 wiki generator，还是直接触发该 branch full generation。
- repository 级 regenerate 是否继续阻止全部 branch jobs。
- Pending removal 是自动取消，还是也返回 409。

Status: DONE
Summary: Phase 03 的 orchestration 调用链、独立 branch lock、authenticated-only policy、branch removal 和测试缺口已确认。
Concerns/Blockers: deleted-file 策略和 repository-level regenerate 的长期行为仍需在实施前固定。
