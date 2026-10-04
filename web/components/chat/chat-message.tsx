"use client"

import * as React from "react"
import Image from "next/image"
import { useTranslations } from "next-intl"
import { User, Bot, Wrench, ChevronDown, ChevronRight, Coins, Brain, Quote } from "lucide-react"
import { cn } from "@/lib/utils"
import { ChatMessage as ChatMessageType, ToolCall, ToolResult, ContentBlock, QuotedText } from "@/hooks/use-chat-history"
import ReactMarkdown from "react-markdown"
import remarkGfm from "remark-gfm"

const PreContext = React.createContext<boolean>(false)

/**
 * 消息组件属性
 */
export interface ChatMessageProps {
  message: ChatMessageType
}

/**
 * 引用文本显示组件
 */
function QuotedTextDisplay({ quotedText }: { quotedText: QuotedText }) {
  const t = useTranslations("chat")
  
  if (!quotedText || !quotedText.text) return null

  return (
    <div className="mb-2 rounded-lg border border-blue-500/30 bg-blue-500/5 overflow-hidden w-full">
      <div className="px-3 py-2">
        <div className="flex items-center gap-2 text-xs text-blue-600 dark:text-blue-400 mb-1">
          <Quote className="h-3 w-3" />
          <span className="font-medium">{t("message.quotedFrom")}{quotedText.title || t("message.currentPage")}</span>
        </div>
        <pre className="text-xs text-muted-foreground whitespace-pre-wrap break-words max-h-24 overflow-y-auto">
          {quotedText.text}
        </pre>
      </div>
    </div>
  )
}

/**
 * 思考内容显示组件
 */
function ThinkingDisplay({ thinking }: { thinking: string }) {
  const t = useTranslations("chat")
  const [isExpanded, setIsExpanded] = React.useState(false)

  if (!thinking) return null

  return (
    <div className="mb-2 rounded-lg border border-purple-500/30 bg-purple-500/5 overflow-hidden w-full">
      <button
        type="button"
        onClick={() => setIsExpanded(!isExpanded)}
        className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-purple-600 dark:text-purple-400 hover:bg-purple-500/10 transition-colors"
      >
        {isExpanded ? (
          <ChevronDown className="h-4 w-4" />
        ) : (
          <ChevronRight className="h-4 w-4" />
        )}
        <Brain className="h-4 w-4" />
        <span className="font-medium">{t("message.thinking")}</span>
      </button>
      {isExpanded && (
        <div className="px-3 pb-3 pt-1">
          <div className="prose prose-sm dark:prose-invert max-w-full prose-p:my-1 text-muted-foreground">
            <ReactMarkdown remarkPlugins={[remarkGfm]}>
              {thinking}
            </ReactMarkdown>
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * 工具调用显示组件
 */
function ToolCallDisplay({ toolCall }: { toolCall: ToolCall }) {
  const [isExpanded, setIsExpanded] = React.useState(false)

  return (
    <div className="my-2 rounded-md border border-border bg-muted/50 p-2 text-sm w-full">
      <button
        type="button"
        onClick={() => setIsExpanded(!isExpanded)}
        className="flex w-full items-center gap-1 text-left text-muted-foreground hover:text-foreground"
      >
        {isExpanded ? (
          <ChevronDown className="h-4 w-4" />
        ) : (
          <ChevronRight className="h-4 w-4" />
        )}
        <Wrench className="h-4 w-4" />
        <span className="font-medium">{toolCall.name}</span>
      </button>
      {isExpanded && toolCall.arguments && Object.keys(toolCall.arguments).length > 0 && (
        <pre className="mt-2 overflow-auto rounded bg-background p-2 text-xs">
          {JSON.stringify(toolCall.arguments, null, 2)}
        </pre>
      )}
    </div>
  )
}

/**
 * 工具结果显示组件
 */
function ToolResultDisplay({ toolResult }: { toolResult: ToolResult }) {
  const t = useTranslations("chat")
  const [isExpanded, setIsExpanded] = React.useState(false)

  return (
    <div
      className={cn(
        "my-2 rounded-md border p-2 text-sm w-full",
        toolResult.isError
          ? "border-destructive/50 bg-destructive/10"
          : "border-border bg-muted/50"
      )}
    >
      <button
        type="button"
        onClick={() => setIsExpanded(!isExpanded)}
        className="flex w-full items-center gap-1 text-left text-muted-foreground hover:text-foreground"
      >
        {isExpanded ? (
          <ChevronDown className="h-4 w-4" />
        ) : (
          <ChevronRight className="h-4 w-4" />
        )}
        <span className="font-medium">
          {toolResult.isError ? t("message.toolError") : t("message.toolResult")}
        </span>
      </button>
      {isExpanded && (
        <pre className="mt-2 max-h-40 overflow-auto rounded bg-background p-2 text-xs whitespace-pre-wrap">
          {toolResult.result}
        </pre>
      )}
    </div>
  )
}

/**
 * Markdown 代码渲染组件
 */
interface MarkdownCodeProps extends React.HTMLAttributes<HTMLElement> {
  className?: string
  children?: React.ReactNode
}

function MarkdownCode({ className, children, ...props }: MarkdownCodeProps) {
  const isInPre = React.useContext(PreContext)

  if (!isInPre) {
    // 行内代码
    return (
      <code className={cn("rounded px-1.5 py-0.5 text-xs text-zinc-200 font-mono break-all !border-0", className)} style={{ backgroundColor: '#27272a' }} {...props}>
        {children}
      </code>
    )
  }
  // 代码块内的 code
  return (
    <code className={cn("text-sm text-zinc-100 font-mono block !bg-transparent !p-0 !border-0", className)} style={{ backgroundColor: 'transparent', padding: 0, border: 'none' }} {...props}>
      {children}
    </code>
  )
}

/**
 * 文本内容显示组件
 */
function TextContentDisplay({ content }: { content: string }) {
  return (
    <div className="rounded-lg px-3 py-2 w-full overflow-hidden bg-muted/50 text-foreground">
      <div className="prose prose-sm dark:prose-invert max-w-none prose-p:my-1 prose-pre:my-2 prose-pre:p-0 prose-pre:bg-transparent break-words">
        <ReactMarkdown 
          remarkPlugins={[remarkGfm]}
          components={{
            pre: ({ children }) => (
              <PreContext.Provider value={true}>
                <pre className="overflow-x-auto rounded-lg p-4 text-sm my-2 max-w-full wiki-scrollbar" style={{ backgroundColor: '#18181b' }}>
                  {children}
                </pre>
              </PreContext.Provider>
            ),
            code: MarkdownCode,
          }}
        >
          {content}
        </ReactMarkdown>
      </div>
    </div>
  )
}

/**
 * 内容块渲染组件
 */
function ContentBlockRenderer({ block, index }: { block: ContentBlock; index: number }) {
  switch (block.type) {
    case "thinking":
      return <ThinkingDisplay key={`thinking-${index}`} thinking={block.content || ""} />
    case "tool_call":
      return block.toolCall ? <ToolCallDisplay key={`tool-${index}`} toolCall={block.toolCall} /> : null
    case "tool_result":
      return block.toolResult ? <ToolResultDisplay key={`tool-result-${index}`} toolResult={block.toolResult} /> : null
    case "text":
      return block.content ? <TextContentDisplay key={`text-${index}`} content={block.content} /> : null
    default:
      return null
  }
}

/**
 * 聊天消息组件
 * 
 * 支持显示用户消息、AI回复、工具调用信息
 * 按 contentBlocks 顺序渲染，保持调用顺序
 * 
 * Requirements: 2.3, 2.4, 2.5, 2.6
 */
export function ChatMessageItem({ message }: ChatMessageProps) {
  const t = useTranslations("chat")
  const isUser = message.role === "user"
  const isTool = message.role === "tool"
  
  // 判断是否使用 contentBlocks 渲染
  const hasContentBlocks = message.contentBlocks && message.contentBlocks.length > 0

  return (
    <div
      className={cn(
        "flex gap-3 p-3 w-full",
        isUser ? "flex-row-reverse" : "flex-row"
      )}
    >
      {/* 头像 */}
      <div
        className={cn(
          "flex h-8 w-8 shrink-0 items-center justify-center rounded-full",
          isUser
            ? "bg-primary text-primary-foreground"
            : isTool
            ? "bg-amber-500 text-white"
            : "bg-muted text-muted-foreground"
        )}
      >
        {isUser ? (
          <User className="h-4 w-4" />
        ) : isTool ? (
          <Wrench className="h-4 w-4" />
        ) : (
          <Bot className="h-4 w-4" />
        )}
      </div>

      {/* 消息内容 */}
      <div
        className={cn(
          "flex flex-col overflow-hidden",
          isUser 
            ? "items-end max-w-[85%]" 
            : "items-start min-w-0 flex-1"
        )}
      >
        {/* 引用文本（用户消息） */}
        {isUser && message.quotedText && (
          <QuotedTextDisplay quotedText={message.quotedText} />
        )}

        {/* 图片预览 */}
        {message.images && message.images.length > 0 && (
          <div className="mb-2 flex flex-wrap gap-2">
            {message.images.map((img, index) => (
              <Image unoptimized width={128} height={128}
                key={index}
                src={img.startsWith("data:") ? img : `data:image/png;base64,${img}`}
                alt={t("message.uploadedImage")}
                className="max-h-32 max-w-32 rounded-md object-cover"
              />
            ))}
          </div>
        )}

        {/* 按 contentBlocks 顺序渲染（AI 消息） */}
        {!isUser && hasContentBlocks ? (
          <div className="w-full space-y-1 overflow-hidden">
            {message.contentBlocks!.map((block, index) => (
              <ContentBlockRenderer key={index} block={block} index={index} />
            ))}
          </div>
        ) : (
          <>
            {/* 兼容旧结构：思考内容 */}
            {!isUser && message.thinking && !hasContentBlocks && (
              <ThinkingDisplay thinking={message.thinking} />
            )}

            {/* 兼容旧结构：工具调用 */}
            {!hasContentBlocks && !isUser && message.toolCalls && message.toolCalls.length > 0 && (
              <div className="mb-1 w-full">
                {message.toolCalls.map((toolCall) => (
                  <ToolCallDisplay key={toolCall.id} toolCall={toolCall} />
                ))}
              </div>
            )}

            {/* 文本内容 */}
            {message.content && (
              <div
                className={cn(
                  "rounded-2xl px-3 py-2 overflow-hidden",
                  isUser
                    ? "bg-primary text-primary-foreground rounded-br-md"
                    : "bg-muted/50 text-foreground w-full rounded-bl-md"
                )}
              >
                {isUser ? (
                  <p className="whitespace-pre-wrap break-words text-sm">{message.content}</p>
                ) : !hasContentBlocks ? (
                  <div className="prose prose-sm dark:prose-invert max-w-none prose-p:my-1 prose-pre:my-2 prose-pre:p-0 prose-pre:bg-transparent break-words">
                    <ReactMarkdown 
                      remarkPlugins={[remarkGfm]}
                      components={{
                        pre: ({ children }) => (
                          <PreContext.Provider value={true}>
                            <pre className="overflow-x-auto rounded-lg p-4 text-sm my-2 max-w-full wiki-scrollbar" style={{ backgroundColor: '#18181b' }}>
                              {children}
                            </pre>
                          </PreContext.Provider>
                        ),
                        code: MarkdownCode,
                      }}
                    >
                      {message.content}
                    </ReactMarkdown>
                  </div>
                ) : null}
              </div>
            )}
          </>
        )}

        {/* 工具结果 */}
        {message.toolResult && (
          <div className="mt-1 w-full">
            <ToolResultDisplay toolResult={message.toolResult} />
          </div>
        )}

        {/* 时间戳和Token统计 */}
        <div className={cn(
          "mt-1 flex items-center gap-2 text-xs text-muted-foreground",
          isUser ? "flex-row-reverse" : "flex-row"
        )}>
          <span>{new Date(message.timestamp).toLocaleTimeString()}</span>
          {message.tokenUsage && (
            <span className="flex items-center gap-1">
              <Coins className="h-3 w-3" />
              {message.tokenUsage.inputTokens + message.tokenUsage.outputTokens} {t("message.tokens")}
              <span className="text-muted-foreground/70">
                ({t("message.inputTokens")}: {message.tokenUsage.inputTokens}, {t("message.outputTokens")}: {message.tokenUsage.outputTokens})
              </span>
            </span>
          )}
        </div>
      </div>
    </div>
  )
}
