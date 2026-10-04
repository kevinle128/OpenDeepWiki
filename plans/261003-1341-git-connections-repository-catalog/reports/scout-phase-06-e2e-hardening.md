# Phase 6 Scout：E2E、Hardening、Docs 与 Deployment

## 阶段目标

验证真实用户路径、权限、安全边界、响应式布局和生产部署。
本阶段不增加产品功能。
本阶段关闭测试、文档、CI 和运行配置缺口。

## 当前测试和 CI 基线

- 前端有 4 个测试文件和 18 个测试。
- 后端有 93 个 C# 测试文件和 557 个 `[Fact]`、`[Theory]` 或 `[Property]` 声明。
- 前端使用 Vitest、jsdom 和 Testing Library，见 `web/vitest.config.ts:5-17` 和 `web/vitest.setup.ts:1`。
- 前端 package scripts 只有 dev、build、start、lint、test 和 test:watch，见 `web/package.json:5-12`。
- 仓库没有 Playwright/Cypress 配置，也没有 E2E script。
- `Makefile` 的 `test` 只运行后端和 Vitest，见 `Makefile:115-134`。
- GitHub workflow 直接构建并推送 Docker image，没有先运行 lint、unit test、build test 或 E2E，见 `.github/workflows/docker-image.yml:7-74`。
- release workflow 同样直接构建和推送 image，见 `.github/workflows/release.yml:14-96` 和 `:98-152`。

## Playwright 决策

应增加 Playwright。

理由：

- 仓库规则要求先以接近终端用户的 E2E 重现和验证 UI。
- Option C 包含登录、三段状态联动、Dialog、服务端分页、后台任务、权限和两个响应式断点。
- jsdom 不能可靠验证真实 focus、Radix portal、CSS media query、sticky/overflow 和浏览器导航。
- 当前依赖没有可执行 E2E runner。
  Next 的 optional peer 声明不等于项目直接安装。

只增加 `@playwright/test` 一个开发依赖。
不要同时增加 Cypress、axe wrapper、visual regression SaaS 或测试管理平台。
Playwright 自带截图、trace、浏览器、locator 和 accessibility snapshot 基础能力，足够本阶段使用。

## 精确文件清单

### 新建

- `web/playwright.config.ts`
  固定 web 端口、API 端口、webServer 生命周期、trace、截图、重试和项目 viewport。
- `web/e2e/repository-workspace.spec.ts`
  主 happy path、连接切换、catalog、index plan 和 branch 管理。
- `web/e2e/repository-workspace-permissions.spec.ts`
  普通用户、creator、Admin 和匿名用户矩阵。
- `web/e2e/repository-workspace-responsive.spec.ts`
  1440px 三列、900px 三标签和 390px 手机布局。
- `web/e2e/repository-workspace-secrets.spec.ts`
  DOM、网络响应、浏览器存储、截图和错误 UI 中都没有 secret。
- `tests/OpenDeepWiki.Tests/Integration/GitConnectionWorkflowTests.cs`
  使用 SQLite 的完整后端 workflow 测试。
- `.github/workflows/quality.yml`
  PR 和 main push 的 restore、后端 test、前端 test、i18n、lint、build 和 E2E job。

如果 Phase 1-4 已经创建同名集成测试，则修改该文件，不再创建第二套 workflow 测试。

### 修改

- `web/package.json`
  增加 `test:e2e` 和 `test:e2e:ui` scripts，并增加 `@playwright/test` devDependency。
- `web/package-lock.json`
  由 npm 更新。
  不手工编辑。
- `Makefile`
  增加 `test-e2e` target，但不把它默认塞入每次快速 `make test`。
  CI quality gate 显式运行它。
- `README.md`
  在测试命令和用户功能说明中加入 shared repository workspace。
  当前测试命令位于 `README.md:168-174`。
- `docs/content/docs/getting-started/local-development.mdx`
  补充 Playwright 浏览器安装和 E2E 命令。
  当前前端测试说明位于 `docs/content/docs/getting-started/local-development.mdx:139-153`。
- `docs/content/docs/api-reference/repositories.mdx`
  记录 git connections、catalog、branch 管理、disabled error code 和认证规则。
  当前文档仍说明 repository 级 `authPassword`，见 `docs/content/docs/api-reference/repositories.mdx:18-73`。
  该内容必须标为 legacy 或按最终迁移合同更新。
- `docs/content/docs/api-reference/meta.json`
  只有在连接 API 拆成新页面时添加导航项。
- `docs/content/docs/architecture/frontend.mdx`
  加入 `/repositories` 路由、`components/repositories` 和 E2E 结构。
  当前结构说明位于 `docs/content/docs/architecture/frontend.mdx:10-65`，测试说明位于 `:150-156`。
- `docs/content/docs/architecture/data-models.mdx`
  加入 GitConnection、Connected Repository、audit 和 ownership/capability 关系。
- `docs/content/docs/architecture/backend.mdx`
  加入 provider adapter、secret protection 和 disabled guard。
- `docs/content/docs/configuration/environment-variables.mdx`
  只在实现新增 encryption key、key ring 路径或 provider 配置环境变量时更新。
- `docs/content/docs/deployment/docker-compose.mdx`
  记录 secret key ring 持久化和升级迁移要求。
- `docs/content/docs/getting-started/docker-deployment.mdx`
  增加升级前备份和 migration 后 smoke test。
- `compose.yaml`
  如果 Data Protection key ring 落盘，则增加专用 volume 和环境路径。
  当前后端只有 `/data` 挂载，见 `compose.yaml:77-79`。
- `compose.pgsql.yaml`
  保持与 `compose.yaml` 相同的 key ring/runtime 配置。
- `scripts/sealos/sealos-template.yaml`
  如果新增必需 runtime 配置，则同步 deployment env/volume。
- `.github/workflows/docker-image.yml`
  让 Docker publish job 依赖 `quality.yml` 对应的可复用 job，或把 quality job 放在同一 workflow 并用 `needs`。
- `.github/workflows/release.yml`
  release image 只能在同一 commit/tag 的 quality gate 通过后发布。

### 删除

本阶段不删除旧 API、旧导入页面、旧环境变量或旧文档章节。
兼容清理需要独立弃用周期和迁移完成证据。

## E2E 拓扑和进程管理

推荐拓扑：

```text
Playwright worker
  -> Next.js web on deterministic 3100
       -> /api proxy
            -> ASP.NET Core test app on deterministic 5266
                 -> temporary SQLite database
                 -> fake in-process Git provider adapter
```

不得访问真实 GitHub 或 GitLab。
不得写入真实用户 token。
provider fake 应位于测试宿主依赖注入边界，不应把 fake 行为编译进生产代码路径。

Playwright config 应使用 `webServer` 管理前端进程。
后端测试宿主应由一个明确脚本启动，并在 test 结束后终止。
端口固定为每个工作区一组值。
遇到端口占用时先识别 stale owner，不自动递增端口。

不建议用 Docker Compose 作为每个 E2E 测试的默认启动器。
Compose 适合最终 smoke test，但反馈慢，且更容易留下孤儿进程。

## E2E 场景和最低测试数

至少 12 个 Playwright 测试：

### 主流程，4 个

1. 登录用户创建 GitHub 连接，浏览 catalog，选择多个 branch 并提交 index plan。
2. 登录用户使用 GitLab.com 连接完成相同流程。
3. 登录用户使用有效 HTTPS self-hosted GitLab 连接完成相同流程。
4. 已索引 repository 打开 branch 管理，添加、同步、重建和移除 branch。

### 权限和禁用，4 个

5. 匿名用户访问 `/repositories` 被送到 `/auth`。
6. 非 creator 用户可以发现 repository 和管理 branch，但看不到连接维护操作。
7. creator 可以编辑、测试和禁用连接，Admin 可以执行相同操作。
8. disabled 连接保留已生成文档，但阻止 discovery、新索引和同步，并显示明确原因。

### 安全和韧性，2 个

9. 创建和编辑后，token 不出现在 DOM、response body、localStorage、sessionStorage、URL、toast、console 或截图。
10. provider 超时、401、429 和 5xx 映射为稳定 UI 错误，旧连接数据不被错误响应覆盖。

### 响应式和无障碍，2 个

11. 1440px 为三列，900px 为三标签，390px 无关键状态丢失和横向溢出。
12. 仅用键盘完成连接选择、repository 选择、branch 选择、Dialog 保存和关闭。

每个失败保留 trace 和 screenshot。
CI 只在失败时上传 artifact，避免长期存储包含 repository 名称的成功截图。

## Hardening 检查表

### Secret

- 网络响应只有 `hasSecret`。
- 编辑表单 secret 初始为空。
- 浏览器 storage 无 secret。
- 服务端日志和 audit metadata 无 secret。
- provider 错误先清洗再返回。
- screenshot 和 trace 不包含输入后的 token。
  secret 用例应在提交后立即清空输入，并限制失败 artifact 的访问和保留期。

### 权限

- 所有 `/api/v1/git-connections*` 和 branch mutation 端点要求认证。
- creator/Admin 维护连接。
- 所有认证用户发现 repository 和管理 branch。
- capability 来自后端，UI 不从 `roles` 自行推断。
- direct API 越权测试必须和 UI 隐藏测试同时存在。

### 并发和 stale state

- 快速切换 connection 时取消旧 catalog 请求或忽略旧 request ID。
- 重复 branch 提交幂等。
- 禁用和 index plan 竞态以事务或明确 409 收敛。
- 运行任务中的 branch 删除返回 409。
- provider 429 尊重 retry-after，但 UI 不自动无限重试。

### 可观测性

- health endpoint 仍可用，见 `src/OpenDeepWiki/Program.cs:407`。
- 日志包含 connection ID、provider、action、result 和 duration，不含 secret。
- audit 记录成功和失败变更。
- provider latency 和 error count 可用结构化日志观测。
- 前端代理当前会记录完整 URL，见 `web/app/api/[...path]/route.ts:90-114`。
  新 API 禁止把 token 放到 query string，否则会进入日志。

## CI 依赖图

```text
restore
  -> backend-unit-integration
  -> frontend-unit + i18n + lint + build
  -> playwright-e2e
  -> docker-build-smoke
  -> image-publish
```

`docker-image.yml` 当前 publish 时没有质量 gate，见 `.github/workflows/docker-image.yml:7-74`。
`release.yml` 当前也没有测试依赖，见 `.github/workflows/release.yml:14-152`。

质量 workflow 应缓存 NuGet 和 npm。
前端必须用 `npm ci`。
E2E job 安装 Chromium 即可，除非发现 Firefox/WebKit 专属问题。
不要默认运行三个浏览器，因为本阶段的主要风险是应用流程和响应式，不是跨引擎兼容。

## Deployment 验证

### Build gate

```sh
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
dotnet build OpenDeepWiki.sln
cd web && npm ci
cd web && npm test
cd web && node scripts/check-i18n.js
cd web && npm run lint
cd web && npm run build
cd web && npx playwright install --with-deps chromium
cd web && npm run test:e2e
cd docs && npm ci && npm run lint && npm run build
docker compose build
```

### Runtime smoke

```sh
docker compose up -d
curl --fail http://localhost:18081/health
curl --fail http://localhost:8090/
docker compose ps
docker compose logs --no-color opendeepwiki web
docker compose down
```

`compose.yaml` 当前暴露后端 `18081` 和前端 `8090`，见 `compose.yaml:7-8` 和 `:81-92`。
PostgreSQL compose 已有后端和数据库 healthcheck，见 `compose.pgsql.yaml:10-18` 和 `:86-102`。
默认 `compose.yaml` 没有 healthcheck 或 `depends_on`。
Phase 6 应为默认 compose 加后端 healthcheck，并让 web 等待后端健康，避免首次启动竞态。

## 文档影响边界

必须更新 API、架构、开发和部署文档，因为本功能改变用户流程、认证合同、数据模型、secret 处理和 migration 运维。
ADR 0001 和 0002 已接受，不应重写决策。
实现若符合 ADR，只需要补充链接和实际接口。
只有实现偏离 ADR 时才新增 ADR。

不要修改 `CHANGELOG.md`。
不要把阶段号、审计 finding ID 或计划 ID写进产品代码、测试名或 migration 名。

## Phase 6 验收退出条件

- 12 个 E2E 场景稳定通过三次。
- 后端 557 个现有测试声明对应的完整 suite 通过，并通过新增连接 workflow 测试。
- 前端现有 18 个测试和 Phase 5 新增测试全部通过。
- lint、web build、docs build 和 solution build 通过。
- SQLite 和 PostgreSQL migration smoke test 通过。
- Docker SQLite 和 PostgreSQL 两套启动通过。
- secret 搜索在响应 artifact、日志和浏览器 storage 中为零命中。
- 390px、900px 和 1440px 截图人工检查通过。
- keyboard-only 主流程通过。
- 文档链接和命令实际执行过。

## 未解决问题

- CI registry publish workflow 是否允许改成 reusable workflow。
  如果仓库权限不允许，则在两个 publish workflow 中重复最小 `needs: quality` job。
- encryption key ring 的最终路径取决于后端 Phase 2 实现。
  如果使用外部 KMS，则 compose volume 修改不需要存在。

Status: DONE

Summary: 已列出 Phase 6 的 E2E 依据、精确文件、12 个场景、hardening、CI、文档和部署退出条件。

Concerns/Blockers: quality workflow 复用方式和 encryption key ring 部署方式需与最终后端实现对齐。
