# Repo 组件 lint 修复

Status: DONE_WITH_CONCERNS

修复范围为 `web/components/repo`。
初始 ESLint JSON 显示 13 个错误，主要来自 effect 内同步更新状态，以及修改 memoized Map。
未禁用规则，未增加人为微任务。

外部 props 引起的计数、可见性、分页和树展开状态更新，改为有条件的 render 状态调整。
客户端挂载状态使用项目已有的 `useSyncExternalStore` 模式。
列表请求只在实际 Promise 回调中更新结果，刷新事件和轮询回调保持加载状态。
图表缩放由原生 ResizeObserver 回调更新。
Mermaid 全屏弹窗在关闭时卸载，以便再次打开时重置缩放。
Markdown 标题计数不再跨 render 共用可变 Map。
新增重复标题再次渲染时锚点稳定的检查。

内部登录跳转使用路由器。
语言切换保留整页导航，以保证 cookie 和 middleware 生效。
仓库头像使用固定尺寸的 Next Image，保留原来的远端直接加载行为。

验证命令为 `npx eslint components/repo`、`npx vitest run components/repo`、`npx tsc --noEmit`。
ESLint 无错误、无警告。
Vitest 9 个文件、64 个测试全部通过。
TypeScript 检查通过。

Concerns: Vitest 输出已有的 Vite native configLoader 迁移提示，配置文件归属其他工作者，未修改。
未决问题：无。
