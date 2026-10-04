# Phase 5 Scout：Option C Repository Workspace UI

## 阶段目标

为所有已登录用户提供共享 repository workspace。
工作区支持 GitHub、GitLab.com 和 self-hosted GitLab。
桌面端使用连接、repository catalog、index plan 三列。
窄屏使用连接、repository、计划三个标签。
连接 secret 永远不显示、不回填，也不进入浏览器可持久化状态。

## 当前入口和边界

- 主导航由 `web/app/sidebar.tsx:55-89` 定义。
  已有 workspace 分组和 `requireAuth` 机制。
- 未登录入口会跳到 `/auth`，见 `web/app/sidebar.tsx:107-139`。
- 用户页应使用 `AppLayout`，见 `web/components/app-layout.tsx:23-49`。
- `/admin` 布局只允许 Admin，见 `web/app/admin/admin-layout-client.tsx:36-58`。
  新工作区不能放在 `/admin`。
- 已接受 ADR 明确所有用户都可创建和使用连接，只有创建人或 Admin 可以替换凭据、禁用或删除连接，见 `docs/adr/0001-share-git-connections-in-one-workspace.md:5-20`。
- 已接受 ADR 明确 GitHub 和 GitLab token 只保存在服务端，并要求 self-hosted GitLab 使用有效 HTTPS，见 `docs/adr/0002-protect-shared-git-credentials.md:5-14`。

## 精确文件清单

### 新建

- `web/app/(main)/repositories/page.tsx`
  页面入口、认证加载态、选中连接和 repository 的 URL 参数协调。
- `web/components/repositories/repository-workspace.tsx`
  三列桌面布局和窄屏标签布局。
- `web/components/repositories/connection-list.tsx`
  provider、健康状态、禁用状态、创建人和 capability 操作。
- `web/components/repositories/connection-dialog.tsx`
  GitHub、GitLab.com 和 self-hosted GitLab 创建或编辑表单。
- `web/components/repositories/repository-catalog.tsx`
  服务端分页、搜索、可见性、索引状态、排序和选中状态。
- `web/components/repositories/index-plan-panel.tsx`
  branch 选择、语言、SKILL、自动同步、估算和提交。
- `web/components/repositories/managed-branches-panel.tsx`
  已索引 branch 的添加、移除、同步、重建和任务状态。
- `web/components/repositories/connection-activity-feed.tsx`
  安全审计摘要和 cursor 分页。
- `web/components/repositories/repository-workspace.test.tsx`
  三列和移动标签状态测试。
- `web/components/repositories/connection-list.test.tsx`
  键盘、权限和 disabled 测试。
- `web/components/repositories/connection-dialog.test.tsx`
  provider 条件字段、验证和 secret 不回填测试。
- `web/components/repositories/repository-catalog.test.tsx`
  防抖、分页、过滤、选择、空态和错误测试。
- `web/components/repositories/index-plan-panel.test.tsx`
  branch 选择、payload、部分成功和 disabled 测试。
- `web/lib/git-connections-api.ts`
  使用统一 `api` 客户端的类型化请求。
- `web/lib/__tests__/git-connections-api.test.ts`
  URL、请求体、错误码和 secret 防泄漏测试。
- `web/types/git-connection.ts`
  UI DTO、capability、provider、catalog、branch 和 audit 类型。
- `web/i18n/messages/{de,en,es,fr,ja,ko,pt-BR,zh}/repositories.json`
  工作区独立命名空间。

### 修改

- `web/app/sidebar.tsx`
  在 workspace 组加入 `/repositories`，并设置 `requireAuth: true`。
- `web/i18n/request.ts`
  在 `loadMessages` 中加载并返回 `repositories`。
  当前命名空间加载位于 `web/i18n/request.ts:10-44`。
- `web/hooks/use-translations.ts`
  注册 `repositories` translator、switch case 和 callback 依赖。
  当前集中分派位于 `web/hooks/use-translations.ts:8-86`。
- `web/types/i18n.d.ts`
  加入 repositories 类型。
  当前声明只覆盖四个旧命名空间，见 `web/types/i18n.d.ts:1-15`。
- `web/i18n/messages/{de,en,es,fr,ja,ko,pt-BR,zh}/sidebar.json`
  加入 repository workspace 导航文案。
- `web/types/repository.ts`
  只在 branch task 返回值需要共享时扩展。
  保持 `RepositoryItemResponse` 和现有 branch task 类型兼容，见 `web/types/repository.ts:107-132` 和 `:228-262`。
- `web/lib/repository-api.ts`
  复用现有 generation task 方法，见 `web/lib/repository-api.ts:376-418`。
  不在此文件复制连接 CRUD。
- `web/components/admin/data-table.tsx`
  若工作区复用它，则将通用实现移到 `web/components/data-table-shell.tsx`，并在旧路径保留转发导出。
  当前通用行为位于 `web/components/admin/data-table.tsx:17-98`。
- `web/components/admin/table-pagination.tsx`
  若工作区复用它，则将实现移到 `web/components/table-pagination.tsx`，并保留旧路径转发导出。
  同时本地化硬编码的分页 aria-label，见 `web/components/admin/table-pagination.tsx:102-165`。
- `web/components/admin/status-badge.tsx`
  只抽取通用 `StatusBadge` 到非 admin 目录。
  保留 `RepoStatusBadge` 兼容导出，见 `web/components/admin/status-badge.tsx:8-87`。
- `web/components/repo/branch-generation-status.tsx`
  可复用状态展示，但必须移除硬编码英文并增加 live status。
  当前硬编码位于 `web/components/repo/branch-generation-status.tsx:32-65`。

### 删除

本阶段不应删除产品文件。
旧 `/private/github-import` 和 `/admin/github-import` 仍需要兼容。
旧入口可在后续弃用周期结束后删除，不属于 Phase 5。

## 组件依赖图

```text
app/(main)/repositories/page.tsx
  -> AppLayout
  -> RepositoryWorkspace
       -> ConnectionList
       -> RepositoryCatalog
       -> IndexPlanPanel | ManagedBranchesPanel
       -> ConnectionActivityFeed

ConnectionList / ConnectionDialog
  -> git-connections-api
  -> Button, Dialog, AlertDialog, Badge

RepositoryCatalog
  -> git-connections-api
  -> DataTableShell, TablePagination, StatusBadge
  -> Input, Select, Table, Skeleton

IndexPlanPanel / ManagedBranchesPanel
  -> git-connections-api
  -> existing repository-api branch task methods
  -> Checkbox, Select, Switch, Progress, AlertDialog
```

`RepositoryWorkspace` 应拥有选中 ID 和移动标签状态。
子组件应接收 DTO 和 callback，不应各自读取同一 catalog。
连接 ID、repository ID、过滤器和页码可放入 URL search params。
secret、token 和 provider 原始错误不得放入 URL、localStorage、sessionStorage 或 React Query cache。

## 可复用组件和受保护接口

### 必须保护

- `apiClient<T>` 的认证和 `ApiError` 行为，见 `web/lib/api-client.ts:20-35` 和 `:66-148`。
- `RepositoryItemResponse` 的现有必填字段。
- `BranchGenerationTaskResponse` 和 `BranchGenerationErrorResponse`，见 `web/types/repository.ts:228-262`。
- `enqueueBranchFullGeneration`、`getBranchGenerationTask`、`retryBranchGenerationTask` 和 `cancelBranchGenerationTask` 的路径与返回类型，见 `web/lib/repository-api.ts:376-418`。
- `AppLayout` 的 sidebar/header 组合，避免 Option C 创建第二套应用外壳。
- 旧 GitHub 导入页面和其 `GitHubInstallationList`/`GitHubRepoBrowser` public props，见 `web/app/(main)/private/github-import/page.tsx:121-151`。

### 可直接复用

- Radix Tabs 已提供正确键盘语义，见 `web/components/ui/tabs.tsx:8-66`。
- Dialog、AlertDialog、Checkbox、Select、Switch、Progress、Table、ScrollArea 和 Sonner 均已安装。
- `wikiLanguageCodes` 和 `defaultWikiLanguage` 是唯一语言来源，见 `web/i18n/config.ts:46-69`。
- `DataTableShell` 的加载、空态、工具栏和 footer 结构可复用。
- `TablePagination` 的页码收敛逻辑可复用。

### 不能直接复用

- `GitHubRepoBrowser` 明确绑定 GitHub DTO，并通过循环抓取全部页，见 `web/components/github/github-repo-browser.tsx:36-75` 和 `:128-167`。
  新 catalog 必须使用通用 provider DTO 和服务端分页。
- `GitHubInstallationList` 使用 click-only `div`，见 `web/components/github/github-installation-list.tsx:26-36`。
  新连接项必须使用 `button`，并提供 `aria-current` 或 `aria-selected`。
- `RepositorySubmitForm` 把凭据放在 repository 表单 state 中，见 `web/components/repo/repository-submit-form.tsx:94-118`。
  共享连接模式不能复用这部分。
- `useIsMobile` 的断点是 768px，见 `web/hooks/use-mobile.ts:1-18`。
  原型在 950px 切换三标签，不能直接用这个 hook 控制布局。
  应优先用 CSS container/media query，或创建工作区专用 `matchMedia('(max-width: 949px)')`。

## 重复和风险

- `web/lib/admin-api.ts:5-33` 和 `web/lib/github-import-api.ts:8-37` 各自复制 auth fetch。
  新模块必须只用 `web/lib/api-client.ts`。
- 管理表格、分页和状态组件放在 admin 目录，但其实现是通用的。
  直接跨域引用会固化错误边界。
  用兼容转发导出完成一次小迁移。
- `RepositorySubmitForm` 已较大，并管理来源、branch、语言、可见性、凭据和异步探测。
  不要把 Option C 追加到该组件。
- provider 枚举不能只写 GitHub/GitLab.com。
  self-hosted GitLab 必须由 `provider: GitLab` 加规范化 `serverUrl` 表达，避免第三套重复逻辑。
- 连接编辑 DTO 只能有 `hasSecret`。
  不允许 API 返回 masked token，因为 masked 值仍可能泄漏长度或格式，也可能被误提交覆盖。
- catalog 搜索要取消或忽略过期响应。
  否则快速切换连接时，旧连接结果可能覆盖新连接结果。
- branch 批量提交应显示逐项结果。
  不要把部分失败压成一个成功 toast。

## 响应式和无障碍验收

- `>= 950px` 显示三列。
- `768px-949px` 仍应显示三标签，不能因现有 mobile hook 而落入不一致状态。
- `< 560px` 使用单列控件，catalog 项改为卡片或把权限和状态合并到名称单元格。
- 标签使用 Radix Tabs，不自行模拟 tab 键盘行为。
- 连接、repository 和 branch 都必须能用 Tab、Enter 和 Space 操作。
- 选中状态不能只用颜色。
- 状态必须有文字和图标。
- 进度区域使用 `aria-live="polite"`。
- 错误摘要使用 `role="alert"`，并在提交失败后可聚焦。
- icon-only 按钮有本地化 `aria-label`。
- disabled 控件旁必须显示原因，例如 `CONNECTION_DISABLED`。
- Dialog 关闭后焦点回到触发按钮。

## Phase 5 测试缺口和最低测试数

当前前端只有 4 个测试文件和 18 个 `it` 测试。
当前只有 1 个 repository UI 测试，且只检查只读 generation status，见 `web/components/repo/branch-generation-status.test.tsx:6-24`。
没有页面、导航、表格、Dialog、响应式或权限 capability 测试。

Phase 5 至少新增 6 个测试文件和 24 个测试：

- API 客户端 5 个。
- workspace 布局 4 个。
- connection list 4 个。
- connection dialog 4 个。
- catalog 4 个。
- index plan/managed branch 3 个。

这些数量是最低行为覆盖，不是逐组件追求覆盖率。
每个测试应直接保护一个用户可见合同或安全边界。

## Phase 5 验证命令

```sh
cd web && npm test -- git-connections-api repository-workspace connection-list connection-dialog repository-catalog index-plan-panel
cd web && node scripts/check-i18n.js
cd web && npm run lint
cd web && npm run build
```

`web/package.json:5-12` 已有 test、lint 和 build script。
`web/scripts/check-i18n.js:27-99` 会检查八个 locale 的文件和 key 对齐。

## 未解决问题

- API 是否支持 repository catalog 的稳定 cursor。
  Phase 5 可以先用 page/pageSize，但 provider 数据变化会影响跨页一致性。
- 连接 health warning 的可见错误摘要粒度需要和后端威胁模型保持一致。

Status: DONE

Summary: 已列出 Phase 5 的精确文件、依赖图、受保护接口、复用边界、风险和最低测试覆盖。

Concerns/Blockers: catalog 分页一致性和安全错误摘要仍需与后端合同对齐。
