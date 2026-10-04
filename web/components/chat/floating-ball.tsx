"use client"

import * as React from "react"
import Image from "next/image"
import { useTranslations } from "next-intl"
import { MessageCircle, X } from "lucide-react"
import { cn } from "@/lib/utils"

/**
 * 悬浮球组件属性
 */
export interface FloatingBallProps {
  /** 是否启用 */
  enabled: boolean
  /** 自定义图标URL */
  iconUrl?: string
  /** 当前是否展开 */
  isOpen: boolean
  /** 切换展开/收起回调 */
  onToggle: () => void
  /** 自定义类名 */
  className?: string
}

/**
 * 悬浮球组件
 * 
 * 固定在页面右下角的圆形按钮，点击后展开对话面板
 * 
 * Requirements: 1.1, 1.2, 1.3, 1.4, 1.5
 */
export function FloatingBall({
  enabled,
  iconUrl,
  isOpen,
  onToggle,
  className,
}: FloatingBallProps) {
  const t = useTranslations("chat")
  
  // 如果功能未启用，不显示悬浮球
  if (!enabled) {
    return null
  }

  return (
    <button
      type="button"
      onClick={onToggle}
      className={cn(
        // 基础样式
        "fixed z-50 flex items-center justify-center",
        "w-14 h-14 rounded-full",
        "bg-primary text-primary-foreground",
        "shadow-lg",
        // 位置：右下角
        "right-6 bottom-6",
        // 过渡动画
        "transition-all duration-200 ease-in-out",
        // hover效果：放大1.1倍
        "hover:scale-110",
        // 点击效果
        "active:scale-95",
        // 焦点样式
        "focus:outline-none focus:ring-2 focus:ring-primary focus:ring-offset-2",
        className
      )}
      aria-label={isOpen ? t("panel.close") : t("assistant.title")}
      aria-expanded={isOpen}
    >
      {isOpen ? (
        // 展开状态显示关闭图标
        <X className="h-6 w-6" />
      ) : iconUrl ? (
        // 自定义图标
        <Image unoptimized width={32} height={32}
          src={iconUrl}
          alt={t("assistant.title")}
          className="h-8 w-8 rounded-full object-cover"
        />
      ) : (
        // 默认消息图标
        <MessageCircle className="h-6 w-6" />
      )}
    </button>
  )
}
