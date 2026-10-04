# 组件 lint 修复记录

Status: DONE

## 范围

仅修改 `web/components`，排除 `repo` 和 `repositories`。

修改文件：

- `web/components/animate-ui/primitives/animate/tooltip.tsx`
- `web/components/animate-ui/primitives/effects/highlight.tsx`
- `web/components/animate-ui/primitives/radix/accordion.tsx`
- `web/components/animate-ui/primitives/animate-state.test.tsx`
- `web/components/announcement-banner.tsx`
- `web/components/apps/app-form-dialog.tsx`
- `web/components/apps/app-logs-table.tsx`
- `web/components/apps/app-statistics-chart.tsx`
- `web/components/chat/chat-assistant.tsx`
- `web/components/chat/chat-message.tsx`
- `web/components/chat/chat-panel.tsx`
- `web/components/chat/embed-chat-widget.tsx`
- `web/components/chat/floating-ball.tsx`
- `web/components/chat/image-upload.tsx`
- `web/components/github/github-installation-list.tsx`
- `web/components/github/github-repo-browser.tsx`
- `web/components/integrations-dialog.tsx`
- `web/components/language-toggle.tsx`
- `web/components/theme-toggle.tsx`

## 行为

Highlight 的受控值直接来自属性。
非受控值只用 defaultValue 初始化，后续用户选择不会被 defaultValue 覆盖。
通知回调从状态更新函数移到用户操作函数，避免更新函数重试时重复通知。
Accordion 直接从共享值计算展开状态，单选模式使用完整字符串匹配。
删除无调用方的 setIsOpen 上下文成员。
Tooltip 在当前提示变化时调整本地保留状态，关闭时保留数据以完成退出动画。

主题和语言按钮用 useSyncExternalStore 保持服务端占位和客户端水合行为。
公告和聊天宽度通过 storage 外部状态读取，公告仍支持立即关闭，聊天仍支持拖动和保存宽度。
图片改用 Next Image，外部地址和 Base64 图片保留 unoptimized 行为及原显示尺寸。

表单在打开或 app 变化时重置本地编辑值，首次直接打开编辑模式也正确填充。
模型列表在供应商变化时清除，真实请求结果更新模型。
日志和统计在请求参数变化时进入加载状态，只在真实请求完成后写入响应状态，并忽略已清理请求的响应。
GitHub 组件直接计算默认部门，在安装或筛选变化时重置本地选择，在真实 API 完成后更新列表。
导入后的列表刷新仍进入加载状态。
集成弹窗在打开时进入加载状态，真实 API 完成后更新连接状态。

补全翻译 Hook 依赖，删除未用变量。
嵌入聊天消息的 useMemo 移到提前返回前，保证 Hook 调用顺序固定。

## 验证

`npx eslint components --format json`：所负责的 81 个文件，0 错误、0 警告。
`npx tsc --noEmit`：退出码 0。
`npm test -- components/animate-ui/primitives/animate-state.test.tsx components/chat/__tests__/image-upload.test.ts`：2 文件、12 测试通过。
新增测试覆盖受控 Highlight、非受控默认值和 Accordion 完整值匹配。

测试进程均已退出，无后台进程。
完整构建和 E2E 由主代理执行。
Vitest 输出了现有配置文件 CommonJS/ESM 警告，已通知主代理由配置所有者处理。

未解决问题：无。

## 安装切换回归修复

复审指出旧安装的分页响应可覆盖当前安装的仓库列表。
`github-repo-browser.tsx` 增加请求令牌和当前安装检查，effect 清理时使请求令牌失效。
首屏、每页读取前后、错误提示和 finally 状态更新均检查令牌。
旧安装的刷新函数也不能开始新请求。
旧响应不能停止当前安装的加载状态。

新增 `web/components/github/github-repo-browser.test.tsx`。
回归测试先等待安装 A 的第二页，再切换安装 B，随后完成 A 响应。
测试确认 A 仓库未出现、A 第三页未请求、B 保持加载，导入参数仅包含 B 安装和 B 仓库。
第二个测试确认 A 过期请求失败时不产生 toast，B 仍保持加载。

真实英文消息测试发现组件原有翻译占位符错误。
所有本组件的先翻译再 replace 调用改为给翻译函数传入参数，避免 FORMATTING_ERROR 和按钮显示消息键。
同一回归测试直接点击正确翻译后的导入按钮。

导入完成后同样检查安装 ID 和请求令牌，过期导入不能显示结果、发送提示、刷新列表或结束当前操作状态。
安装切换时重置 importing。
第三个回归测试覆盖 A 导入未完成时切换到 B，再切回 A，旧导入完成不能污染新会话。
测试同时确认 B 可导入、旧导入不触发列表刷新、加载状态不会卡住。
测试导入完成操作放在 await act 内，无 act 警告。

`npm test -- components/github/github-repo-browser.test.tsx`：3 测试通过。
`npx eslint components/github/github-repo-browser.tsx components/github/github-repo-browser.test.tsx`：退出码 0，无警告。
`npx tsc --noEmit`：退出码 0。
修改已冻结，未启动后台进程。
