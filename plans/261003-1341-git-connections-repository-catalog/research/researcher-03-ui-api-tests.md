# Option C 前端、API 与测试研究

## 结论

Option C 应作为登录用户的主工作区，不应继续放在 `/admin` 下。
建议新路由为 `/repositories`，并继续使用 `AppLayout`。
页面保留三段式信息架构：连接、仓库目录、索引计划。
桌面端显示三列，窄屏使用“连接 / 仓库 / 计划”三个可键盘操作的标签页。

现有 GitHub 导入页面已经包含连接选择和仓库浏览的主要数据流，但它按“导入批次”设计，不能直接作为新的长期仓库工作区。
可以复用它的类型、数据加载逻辑和部分展示组件，但应创建新的工作区组件，不应继续扩展一个已经很大的导入组件。

后端需要一个通用 `GitConnection` 聚合。
当前 `GitHubAppInstallation` 只描述 GitHub App 安装，缺少创建人、禁用状态、健康状态、通用 provider、服务器地址和审计关联，见 `src/OpenDeepWiki.Entities/GitHub/GitHubAppInstallation.cs:9-61`。
仓库也需要保存连接 ID，否则同一仓库 URL、权限来源和连接生命周期不能可靠关联。

## 现状证据

### 前端入口和布局

- 用户侧导航由 `web/app/sidebar.tsx:55-89` 定义。
  工作区项目支持 `requireAuth`，未登录用户会被送到 `/auth`，见 `web/app/sidebar.tsx:107-139`。
- 用户侧标准外壳是 `AppLayout`。
  它组合侧边栏、页头和内容区，见 `web/components/app-layout.tsx:23-49`。
- 当前 `/private` 页面已经提供“GitHub 导入”和“添加私有仓库”入口，并复用 `RepositorySubmitForm`，见 `web/app/(main)/private/page.tsx:23-70`。
- 管理端布局会把非 Admin 用户送回首页，见 `web/app/admin/admin-layout-client.tsx:36-58`。
  因此“所有用户可添加和管理仓库分支”的新工作区不能放在该布局下。

### 可复用 UI

- `web/components/github/github-installation-list.tsx:11-63` 已有连接选择的基础界面和可插入操作区。
  该组件当前用可点击 `div`，见第 28-36 行。
  新工作区必须改为原生 `button` 或实现完整键盘语义。
- `web/components/github/github-repo-browser.tsx:36-75` 已定义 provider 仓库列表和导入数据类型。
  它已有搜索、语言、导入状态、分页与批量选择状态，见第 86-111 行和第 194-263 行。
  它会一次拉取全部 provider 仓库，见第 128-167 行。
  这不适合大型组织目录，新的 catalog API 必须服务端分页和筛选。
- `web/components/repo/repository-submit-form.tsx:46-118` 已有 Git URL、来源类型、语言、可见性、SKILL 和分支状态。
  它只支持一次提交一个分支，且凭据属于仓库表单。
  新连接流程应移除仓库级凭据输入，并从连接取得凭据。
- 管理仓库页已有搜索防抖、服务端分页、空态、加载态和表格操作模式，见 `web/app/admin/repositories/page.tsx:103-179`、`:350-483` 和 `:485-697`。
- `DataTableShell` 可复用加载骨架、空态、工具栏和分页外壳，见 `web/components/admin/data-table.tsx:17-98`。
  建议把它移到非 admin 专属目录，例如 `web/components/data-table-shell.tsx`，再更新旧引用。
- `StatusBadge` 是通用实现，但文件和翻译绑定在 admin 目录，见 `web/components/admin/status-badge.tsx:8-63` 和 `:65-87`。
  建议只抽出通用 `StatusBadge`，保留 `RepoStatusBadge` 的现有兼容导出。
- 现有 Radix UI 已安装 Dialog、AlertDialog、Checkbox、Select、Switch、Tabs、ScrollArea、Table 和 Toast。
  不需要新 UI 依赖，见 `web/package.json:13-55`。

### Option C 原型约束

- 原型定义四列桌面网格，其中产品内容是连接列、catalog 列和计划列，见 `mockups/designs/git-account-flow-option-c/index.html:8-18`。
- catalog 有搜索、可见性、索引状态和排序，见原型第 43-49 行。
- 计划区支持多分支、语言、SKILL、自动同步、估算、索引进度与管理动作，见原型第 72-75 行。
- 原型在 950px 以下改为三步标签页，并在 560px 以下隐藏次要表格列，见第 18-19 行。
- 原型连接对话框包含明文示例 token，见第 54 行。
  产品实现不得回填或返回 secret。

### 当前 API 和权限

- 通用前端客户端已经处理 JSON、Bearer token、401 和结构化错误，见 `web/lib/api-client.ts:20-35`、`:66-148` 和 `:151-167`。
  新 API 文件应使用 `api`，不应复制 `fetchWithAuth`。
- 当前 `github-import-api.ts` 重复实现认证 fetch，见 `web/lib/github-import-api.ts:8-37`。
  新功能不应继续复制该模式。
- 用户 GitHub API 只提供状态、安装仓库分页和导入，见 `src/OpenDeepWiki/Endpoints/GitHubImportEndpoints.cs:11-60`。
- 管理 API 统一使用 `/api/admin` 和 `AdminOnly`，见 `src/OpenDeepWiki/Endpoints/Admin/AdminEndpoints.cs:8-24`。
- 管理仓库 API 使用 `{ success, data }` 包装，并以 404 表示缺失资源，见 `src/OpenDeepWiki/Endpoints/Admin/AdminRepositoryEndpoints.cs:18-72`。
- 公共仓库列表目前支持公开性、关键词、语言、owner、分页、排序和状态，见 `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:204-277`。
  返回值没有 connection ID、namespace、provider、disabled 或权限能力，见第 302-329 行。
- 当前仓库提交在 Git URL 已存在时添加一个分支，见 `src/OpenDeepWiki/Services/Repositories/RepositoryService.cs:26-59`。
  这段共享逻辑可以作为“添加 branch”的底层实现，但其请求仍接受仓库凭据。
- 当前 branch full-generation 权限只允许仓库 owner 或 Admin，见 `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs:101-131`。
  这与已确认的“所有登录用户可管理 repository branches”冲突。
- 当前增量更新变更端点没有读取 `IUserContext`，也没有认证或授权检查，见 `src/OpenDeepWiki/Endpoints/IncrementalUpdateEndpoints.cs:23-48` 和 `:55-100`。
  新计划必须先把 branch 变更统一到一个授权策略，避免同类端点权限分裂。
- `Repository` 当前以 `OwnerUserId` 表示个人所有权，并保存仓库级明文 `AuthPassword`，见 `src/OpenDeepWiki.Entities/Repositories/Repository.cs:29-81`。
  新连接 secret 必须加密或通过 secret provider 保存，API 只返回 `hasSecret`。
- 当前仓库唯一索引是 `(OrgName, RepoName)`，见 `src/OpenDeepWiki.EFCore/MasterDbContext.cs:124-126`。
  多连接或多 provider 可能产生相同 namespace/name，因此迁移必须调整唯一约束。

## 推荐前端文件计划

### 修改

- `web/app/sidebar.tsx`
  在 workspace 组增加需要登录的 `/repositories` 项目。
- `web/i18n/request.ts`
  加载新的 `repositories` 命名空间，或继续使用 `common`。
  建议使用独立命名空间，避免继续扩大 `admin.json`。
- `web/hooks/use-translations.ts`
  如果新增命名空间，则加入解析分支。
- `web/i18n/messages/{de,en,es,fr,ja,ko,pt-BR,zh}/sidebar.json`
  增加导航文案。
- `web/i18n/messages/{de,en,es,fr,ja,ko,pt-BR,zh}/repositories.json`
  增加工作区、权限、状态、错误和审计文案。
- `web/types/repository.ts`
  保留现有类型，并新增 catalog、branch plan 和 capability 类型。
- `web/lib/repository-api.ts`
  保持现有 API 兼容，仅在复用已有 branch task API 时补充强类型。
- `web/components/github/github-installation-list.tsx`
  只在旧页面也需要连接状态时扩展。
  不能把 Option C 的全部工作区状态塞入此组件。
- `web/components/admin/data-table.tsx` 和调用方
  若移动到通用目录，保留薄转发导出以减少一次性改动。

### 新建

- `web/app/(main)/repositories/page.tsx`
  只负责认证态、选中 ID、URL 查询参数和三列编排。
- `web/components/repositories/repository-workspace.tsx`
  负责桌面三列与移动标签页状态。
- `web/components/repositories/connection-list.tsx`
  显示 provider、健康状态、禁用状态和维护动作。
- `web/components/repositories/connection-dialog.tsx`
  创建和编辑连接。
  编辑时 secret 字段为空，并显示 `hasSecret`。
- `web/components/repositories/repository-catalog.tsx`
  提供服务端搜索、筛选、排序、分页、加载和空态。
- `web/components/repositories/index-plan-panel.tsx`
  负责分支选择、语言、SKILL、自动同步、提交和 disabled 原因。
- `web/components/repositories/managed-branches-panel.tsx`
  负责已管理分支的添加、移除、同步、重建和任务状态。
- `web/components/repositories/activity-feed.tsx`
  展示审计事件。
- `web/lib/git-connections-api.ts`
  使用统一 `api` 客户端实现连接、catalog、branch 和 audit 请求。
- `web/types/git-connection.ts`
  放置 provider、连接、catalog、capability 和 audit DTO。

不要创建全局状态库。
页面级 state、URL 查询参数和已有 React hooks 足够完成该流程。

## 推荐 API 合同

所有端点都要求登录。
所有响应沿用 `{ success, data }`，失败沿用 `{ success: false, errorCode, message }`。
权限必须由后端返回 capability，前端不能从角色名猜权限。

### 连接

`GET /api/v1/git-connections`

返回：

```ts
interface GitConnectionSummary {
  id: string;
  provider: "GitHub" | "GitLab";
  displayName: string;
  serverUrl: string | null;
  accountLogin: string;
  repositoryCount: number;
  state: "Healthy" | "Warning" | "Disabled";
  lastCheckedAt: string | null;
  createdByUserId: string;
  canMaintain: boolean;
  hasSecret: boolean;
}
```

`POST /api/v1/git-connections`

请求包含 `provider`、`displayName`、可选 `serverUrl`、provider 授权材料。
返回 `201` 和不含 secret 的连接。

`PUT /api/v1/git-connections/{connectionId}`

只允许创建人或 Admin。
空 secret 表示保留现有 secret。
返回 `403 CONNECTION_MAINTENANCE_FORBIDDEN`、`404 CONNECTION_NOT_FOUND` 或更新后的连接。

`POST /api/v1/git-connections/{connectionId}/test`

只允许创建人或 Admin。
返回健康状态、延迟和 `checkedAt`，不返回 token 或 provider 原始错误体。

`POST /api/v1/git-connections/{connectionId}/disable` 和 `/enable`

只允许创建人或 Admin。
禁用是可恢复状态，不应删除连接或已有索引。
禁用后仍可浏览已有 catalog 和审计，但 provider refresh、添加 branch、同步和重建必须返回 `409 CONNECTION_DISABLED`。

### Catalog

`GET /api/v1/git-connections/{connectionId}/repositories?page=1&pageSize=50&search=&visibility=&indexState=&sortBy=updatedAt&sortOrder=desc`

返回：

```ts
interface RepositoryCatalogResponse {
  items: RepositoryCatalogItem[];
  total: number;
  page: number;
  pageSize: number;
  connectionState: "Healthy" | "Warning" | "Disabled";
}

interface RepositoryCatalogItem {
  providerRepositoryId: string;
  repositoryId: string | null;
  namespace: string;
  name: string;
  fullName: string;
  description: string | null;
  cloneUrl: string;
  visibility: "Public" | "Private" | "Internal";
  indexState: "NotIndexed" | "Pending" | "Processing" | "Completed" | "Failed";
  defaultBranch: string;
  updatedAt: string | null;
  managedBranchCount: number;
}
```

服务端必须校验 connection 可见性。
大型 provider 目录必须服务端分页，不使用现有 `GitHubRepoBrowser` 的全量抓取循环。

### 索引计划和分支

`GET /api/v1/git-connections/{connectionId}/repositories/{providerRepositoryId}/branches?search=&page=&pageSize=`

返回 provider branch、默认分支、是否已管理、最后 commit 和 generation state。

`POST /api/v1/git-connections/{connectionId}/repositories/{providerRepositoryId}/index-plans`

请求：

```ts
interface CreateIndexPlanRequest {
  branchNames: string[];
  languageCode: string;
  generateSkill: boolean;
  autoSync: boolean;
}
```

任何登录用户都可调用。
服务端必须在一个事务中创建或复用 repository，再对 branch 去重，并为每个新 branch 建立任务。
响应应逐 branch 返回 `Created`、`AlreadyManaged` 或 `Failed`，以支持部分成功显示。

`POST /api/v1/repositories/{repositoryId}/branches`

任何登录用户都可添加 branch。
请求含 `branchName`、`languageCode`、`generateSkill` 和 `autoSync`。

`DELETE /api/v1/repositories/{repositoryId}/branches/{branchId}`

任何登录用户都可移除 OpenDeepWiki 中的 branch 数据。
删除不能修改 provider branch。
有运行中任务时返回 `409 BRANCH_BUSY`。

现有 generation task API 可保留路径和返回类型，见 `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs:14-28` 和 `:182-229`。
必须把 `AuthorizeRepositoryMutationAsync` 改成统一的登录用户 branch capability，而不是 owner/Admin 判断。

### 审计

`GET /api/v1/git-connections/{connectionId}/audit-events?cursor=&pageSize=30`

事件至少包含 `id`、`action`、`actorUserId`、`actorDisplayName`、`connectionId`、可选 `repositoryId`、可选 `branchId`、`occurredAt` 和安全的 metadata。
必须记录连接创建、编辑、测试、启用、禁用、branch 添加、branch 移除、同步和重建。

不要复用 `UserActivity` 作为审计表。
它只支持推荐行为类型，见 `src/OpenDeepWiki.Entities/Statistics/UserActivity.cs:9-35`，字段也没有操作结果或连接关联，见第 41-94 行。

## 数据和迁移影响

建议新增：

- `GitConnection`：provider、显示名、规范化 server URL、provider account ID/login、创建人、状态、健康字段、加密 secret 引用和时间字段。
- `GitConnectionAuditEvent`：连接、actor、action、目标 ID、结果和安全 metadata。
- `Repository.GitConnectionId`：先允许 null，以兼容旧数据。
- `RepositoryBranch.AutoSync`：用于计划中的自动同步开关。

建议约束：

- 连接对 `(Provider, NormalizedServerUrl, ProviderAccountId)` 建唯一索引。
- 仓库对 `(GitConnectionId, OrgName, RepoName)` 建唯一索引。
- 审计对 `(GitConnectionId, CreatedAt)` 建索引。
- secret 不进入审计、日志、响应或前端 state 快照。

需要同时生成 SQLite 和 PostgreSQL migration，并更新两个 model snapshot。
仓库当前已对两个 provider 保持成对 migration，文件结构可见 `src/EFCore/OpenDeepWiki.Sqlite/Migrations/` 和 `src/EFCore/OpenDeepWiki.Postgresql/Migrations/`。

旧 GitHub 安装迁移有两种可行方式。
推荐保留 `GitHubAppInstallation` 作为 GitHub provider 专属安装信息，并新增一对一 `GitConnectionId`。
这样可以复用现有 GitHub App token 获取逻辑，并避免把 GitLab PAT 字段硬塞到 GitHub 实体。
旧 installation 在迁移后为每条记录创建一条 `GitConnection`，创建人未知时使用系统主体，并只允许 Admin 维护，直到有人认领。

## 无障碍和响应式验收

- 连接项和仓库行必须能用 Tab 聚焦，并支持 Enter 与 Space 选择。
- 选中状态使用 `aria-current` 或 `aria-selected`，不能只靠青色背景。
- 三个移动标签必须使用 Radix Tabs 或正确的 `tablist`、`tab` 和 `tabpanel` 语义。
- Dialog 必须有可见标题、说明、初始焦点、焦点锁定和关闭后焦点恢复。
- icon-only 操作必须有本地化 `aria-label`。
- 状态不能只靠颜色，必须同时有文本或图标。
- 后台进度使用 `aria-live="polite"`，错误使用可聚焦 alert。
- 表格在窄屏不能仅隐藏权限和状态。
  应改成卡片或在名称单元格内保留这些关键状态。
- 桌面三列必须允许中间列收缩，并限制右侧计划面板最小宽度。
- 950px 以下使用三步标签。
  560px 以下控件单列或两列换行。
- 连接禁用时，禁用按钮必须有可见原因，不能只设置 `disabled`。

## 测试优先矩阵

| 层级 | 先写的失败测试 | 主要断言 |
|---|---|---|
| EF 模型 | `GitConnectionModelTests` | 唯一索引、Repository FK、审计索引、级联策略、secret 字段不为响应 DTO |
| migration | SQLite 临时数据库迁移测试 | 旧 GitHub installation 被映射、旧 repository 可读、重复数据处理确定、down/rollback 策略明确 |
| connection service | 创建、更新、禁用、测试连接 | creator/Admin 成功，普通用户 403，secret 不回传，空 secret 保留，禁用为软状态 |
| catalog service | 分页、筛选、排序、disabled | provider 大目录不全量加载，连接隔离，禁用连接只读，越权不可见 |
| branch service | 任意登录用户添加和移除 branch | 匿名 401，登录用户成功，重复 branch 幂等，运行任务 409，disabled 409 |
| audit service | 每个变更动作 | actor、目标、结果和 UTC 时间完整，metadata 无 secret，失败操作也有结果事件 |
| endpoint | 最小集成测试 | HTTP 状态、errorCode、包装格式、取消 token、Admin 和 creator policy |
| frontend API | `git-connections-api.test.ts` | query 编码、Bearer 复用、DTO 解析、401/403/409 错误保留 |
| component | `connection-list.test.tsx` | 键盘选择、选中语义、禁用状态、仅 capability 为真时显示维护操作 |
| component | `repository-catalog.test.tsx` | 防抖、服务端分页、筛选重置页码、加载、空态、错误、窄屏状态可见 |
| component | `index-plan-panel.test.tsx` | 无 branch 时禁用、提交 payload、部分成功、disabled 原因、进行中状态 |
| component | `connection-dialog.test.tsx` | label、焦点、secret 不回填、provider 条件字段、验证错误 |
| E2E | 登录普通用户完整流程 | 选择连接、搜索仓库、选择多 branch、创建计划、观察任务、移除 branch |
| E2E | 权限与禁用 | 普通用户不能维护连接，creator 可禁用，禁用后 branch 动作被 UI 和 API 阻止，Admin 可恢复 |
| E2E | 响应式和键盘 | 1440px 三列、900px 三标签、390px 无横向丢失、纯键盘完成主流程 |

现有前端测试使用 Vitest、jsdom 和 Testing Library，见 `web/vitest.config.ts:5-17`、`web/vitest.setup.ts:1` 和 `web/components/repo/branch-generation-status.test.tsx:1-24`。
后端测试使用 xUnit、FsCheck、Moq、EF InMemory 和 SQLite，见 `tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj:1-35`。

仓库没有 Playwright 或 Cypress 配置，也没有直接 E2E script。
lockfile 中的 Next 可选 peer 不构成可执行 E2E 基础设施。
由于仓库规则要求 bug 和 UI 流程贴近最终用户验证，本功能应增加 Playwright 作为开发依赖、`web/playwright.config.ts`、`web/e2e/repository-workspace.spec.ts` 和 `test:e2e` script。
E2E 应使用本地 API 测试宿主或可重复 seed，不应调用真实 provider 或保存真实 token。

## 验证命令

按以下顺序运行：

```sh
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter "FullyQualifiedName~GitConnection|FullyQualifiedName~RepositoryCatalog|FullyQualifiedName~RepositoryBranch"
cd web && npm test -- git-connections-api connection-list repository-catalog index-plan-panel connection-dialog
cd web && node scripts/check-i18n.js
cd web && npm run lint
cd web && npm run build
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
dotnet build OpenDeepWiki.sln
cd web && npm run test:e2e
```

现有 script 只有 `dev`、`build`、`lint`、`test` 和 `test:watch`，见 `web/package.json:5-12`。
`check-i18n.js` 会以英文为基线检查所有 locale 的文件和键，见 `web/scripts/check-i18n.js:27-99`。
当前 UI locale 共八个，见 `web/i18n/config.ts:16-40`。

## 合同兼容策略

- 不删除 `/api/github/*` 或 `/api/admin/github/*`。
  旧导入页和管理页继续可用，直到新工作区稳定。
- 不改变现有 `RepositoryItemResponse` 必填字段。
  新字段先设为可选，避免 SSR 和 sitemap 调用破坏。
- 保留现有 branch generation task 路径和 response。
  只替换授权策略，并增加 connection disabled guard。
- 新 API 从第一天使用稳定 `errorCode`。
  UI 文案从 i18n 映射，不显示后端原始异常。
- 新连接 API 绝不返回 secret。
  编辑响应只返回 `hasSecret`。
- 连接禁用使用软状态，不级联删除 repository、branch、文档或任务历史。

## 未解决问题

- GitLab.com 和 self-hosted GitLab 是否已明确进入首发范围。
  原型包含它们，但现有后端只有 GitHub App 集成。
- “所有用户可管理 branch”是否包括匿名用户。
  本报告按“所有已登录用户”解释，因为审计需要 actor，且现有 workspace 导航有登录门槛。
- 非创建人是否可以查看连接健康详情和审计中的 actor 名称。
  建议可以查看安全摘要，但不能查看 provider 错误详情或任何 secret。
- 禁用连接后是否允许取消已经运行的任务。
  建议允许取消，阻止新增、重试、同步和重建。

Status: DONE

Summary: 已确认 Option C 的可复用前端、现有 API 与权限冲突、迁移面、无障碍要求和测试优先矩阵。

Concerns/Blockers: GitLab 首发范围和匿名用户含义仍需产品确认。
