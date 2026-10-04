"use client"

import * as React from "react"
import { useTranslations } from "next-intl"
import { FloatingBall } from "./floating-ball"
import { ChatPanel } from "./chat-panel"
import { getChatConfig, DocContext, CatalogItem } from "@/lib/chat-api"

/**
 * 对话助手属性
 */
export interface ChatAssistantProps {
  /** 文档上下文 */
  context: DocContext
  /** 应用ID（嵌入模式） */
  appId?: string
  /** 自定义图标URL */
  iconUrl?: string
}

/**
 * 对话助手组件
 * 
 * 整合悬浮球和对话面板，管理展开/收起状态
 * 
 * Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 5.1, 5.2
 */
export function ChatAssistant({
  context,
  appId,
  iconUrl,
}: ChatAssistantProps) {
  const t = useTranslations("chat")
  const [isOpen, setIsOpen] = React.useState(false)
  const [isEnabled, setIsEnabled] = React.useState(false)
  const [isLoading, setIsLoading] = React.useState(true)

  // 加载配置检查是否启用
  React.useEffect(() => {
    const checkEnabled = async () => {
      try {
        const config = await getChatConfig()
        setIsEnabled(config.isEnabled)
      } catch (err) {
        console.error(t("assistant.loadConfigFailed"), err)
        setIsEnabled(false)
      } finally {
        setIsLoading(false)
      }
    }

    checkEnabled()
  }, [t])

  const handleToggle = React.useCallback(() => {
    setIsOpen(prev => !prev)
  }, [])

  const handleClose = React.useCallback(() => {
    setIsOpen(false)
  }, [])

  // 加载中不显示
  if (isLoading) {
    return null
  }

  return (
    <>
      <FloatingBall
        enabled={isEnabled}
        iconUrl={iconUrl}
        isOpen={isOpen}
        onToggle={handleToggle}
      />
      <ChatPanel
        isOpen={isOpen}
        onClose={handleClose}
        context={context}
        appId={appId}
      />
    </>
  )
}

/**
 * 从RepoTreeNode构建CatalogItem
 */
export interface RepoTreeNode {
  title: string
  slug: string
  children?: RepoTreeNode[]
}

export function buildCatalogMenu(nodes: RepoTreeNode[]): CatalogItem[] {
  return nodes.map(node => ({
    title: node.title,
    path: node.slug,
    children: node.children ? buildCatalogMenu(node.children) : undefined,
  }))
}
