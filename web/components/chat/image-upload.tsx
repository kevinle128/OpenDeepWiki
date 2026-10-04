"use client"

import * as React from "react"
import Image from "next/image"
import { useTranslations } from "next-intl"
import { ImagePlus, X } from "lucide-react"
import { cn } from "@/lib/utils"
import { Button } from "@/components/ui/button"
import {
  validateImageFile,
  fileToBase64,
  SUPPORTED_IMAGE_TYPES,
} from "@/lib/image-validation"

/**
 * 图片上传组件属性
 */
export interface ImageUploadProps {
  /** 已上传的图片列表 (Base64) */
  images: string[]
  /** 图片变更回调 */
  onImagesChange: (images: string[]) => void
  /** 错误回调 */
  onError?: (error: string) => void
  /** 是否禁用 */
  disabled?: boolean
  /** 最大图片数量 */
  maxImages?: number
  /** 自定义类名 */
  className?: string
}

/**
 * 图片上传组件
 * 
 * 支持PNG、JPG、GIF、WebP格式
 * 图片大小限制10MB
 * 图片预览功能
 * Base64编码
 * 
 * Requirements: 4.1, 4.2, 4.3, 4.4, 4.5
 */
export function ImageUpload({
  images,
  onImagesChange,
  onError,
  disabled = false,
  maxImages = 5,
  className,
}: ImageUploadProps) {
  const t = useTranslations("chat")
  const fileInputRef = React.useRef<HTMLInputElement>(null)

  // 处理文件选择
  const handleFileSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const files = e.target.files
    if (!files || files.length === 0) return

    const newImages: string[] = []
    const errors: string[] = []

    for (const file of Array.from(files)) {
      // 检查是否超过最大数量
      if (images.length + newImages.length >= maxImages) {
        errors.push(t("image.supportedFormats", { maxImages }))
        break
      }

      // 验证文件
      const validation = validateImageFile(file)
      if (!validation.valid) {
        errors.push(validation.error!)
        continue
      }

      try {
        const base64 = await fileToBase64(file)
        newImages.push(base64)
    } catch {
        errors.push(t("image.readFailed"))
      }
    }

    // 更新图片列表
    if (newImages.length > 0) {
      onImagesChange([...images, ...newImages])
    }

    // 报告错误
    if (errors.length > 0 && onError) {
      onError(errors.join("; "))
    }

    // 清空input以便重复选择同一文件
    e.target.value = ""
  }

  // 移除图片
  const handleRemove = (index: number) => {
    onImagesChange(images.filter((_, i) => i !== index))
  }

  // 触发文件选择
  const handleClick = () => {
    fileInputRef.current?.click()
  }

  return (
    <div className={cn("flex flex-col gap-2", className)}>
      {/* 图片预览 */}
      {images.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {images.map((img, index) => (
            <div key={index} className="relative group">
              <Image unoptimized width={64} height={64}
                src={img}
                alt={t("image.preview", { index: index + 1 })}
                className="h-16 w-16 rounded-md object-cover border border-border"
              />
              <button
                type="button"
                onClick={() => handleRemove(index)}
                disabled={disabled}
                className={cn(
                  "absolute -right-1 -top-1 rounded-full p-0.5",
                  "bg-destructive text-destructive-foreground",
                  "opacity-0 group-hover:opacity-100 transition-opacity",
                  "focus:opacity-100",
                  disabled && "cursor-not-allowed"
                )}
                aria-label={t("image.remove", { index: index + 1 })}
              >
                <X className="h-3 w-3" />
              </button>
            </div>
          ))}
        </div>
      )}

      {/* 上传按钮 */}
      <input
        ref={fileInputRef}
        type="file"
        accept={SUPPORTED_IMAGE_TYPES.join(",")}
        multiple
        className="hidden"
        onChange={handleFileSelect}
        disabled={disabled}
      />
      
      {images.length < maxImages && (
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={handleClick}
          disabled={disabled}
          className="w-fit"
        >
          <ImagePlus className="mr-2 h-4 w-4" />
          {t("image.upload")}
        </Button>
      )}

      {/* 提示信息 */}
      <p className="text-xs text-muted-foreground">
        {t("image.supportedFormats", { maxImages })}
      </p>
    </div>
  )
}

// 导出验证函数和常量供外部使用
export {
  validateImageFile,
  fileToBase64,
  validateBase64Image,
  getMimeTypeFromBase64,
  SUPPORTED_IMAGE_TYPES,
  MAX_IMAGE_SIZE,
} from "@/lib/image-validation"
