"use client";

import * as React from "react";
import { Switch } from "@/components/ui/switch";
import { Spinner } from "@/components/ui/spinner";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { Globe, Lock, Info } from "lucide-react";
import { cn } from "@/lib/utils";
import { updateRepositoryVisibility } from "@/lib/repository-api";
import { toast } from "sonner";
import { useTranslations } from "@/hooks/use-translations";

export interface VisibilityToggleProps {
  repositoryId: string;
  isPublic: boolean;
  hasPassword: boolean;
  onVisibilityChange: (newIsPublic: boolean) => void;
  disabled?: boolean;
}

export function VisibilityToggle({
  repositoryId,
  isPublic,
  hasPassword,
  onVisibilityChange,
  disabled = false,
}: VisibilityToggleProps) {
  const t = useTranslations();
  const [isLoading, setIsLoading] = React.useState(false);
  const [currentIsPublic, setCurrentIsPublic] = React.useState(isPublic);
  const [previousIsPublic, setPreviousIsPublic] = React.useState(isPublic);
  if (previousIsPublic !== isPublic) {
    setPreviousIsPublic(isPublic);
    setCurrentIsPublic(isPublic);
  }

  // 判断是否可以切换到私有
  // 只有当仓库有密码时，才能设为私有
  const canSetPrivate = hasPassword;

  // 判断开关是否应该被禁用
  // 1. 外部传入的 disabled
  // 2. 正在加载中
  // 3. 当前是公开状态且没有密码（不能切换到私有）
  const isDisabled = disabled || isLoading || (currentIsPublic && !canSetPrivate);

  const handleToggle = async (checked: boolean) => {
    // checked = true 表示公开，checked = false 表示私有
    const newIsPublic = checked;

    // 如果尝试设为私有但没有密码，阻止操作
    if (!newIsPublic && !canSetPrivate) {
      toast.error(t("home.private.visibility.noPasswordError"));
      return;
    }

    setIsLoading(true);

    try {
      const response = await updateRepositoryVisibility({
        repositoryId,
        isPublic: newIsPublic,
      });

      if (response.success) {
        setCurrentIsPublic(response.isPublic);
        onVisibilityChange(response.isPublic);
        toast.success(
          response.isPublic
            ? t("home.private.visibility.setPublicSuccess")
            : t("home.private.visibility.setPrivateSuccess")
        );
      } else {
        toast.error(response.errorMessage || t("home.private.visibility.updateError"));
      }
    } catch (error) {
      console.error("Failed to update visibility:", error);
      toast.error(t("home.private.visibility.updateError"));
    } finally {
      setIsLoading(false);
    }
  };

  // 渲染开关内容
  const renderSwitch = () => (
    <div className="flex items-center gap-2">
      {isLoading ? (
        <Spinner className="h-4 w-4" />
      ) : currentIsPublic ? (
        <Globe className="h-4 w-4 text-muted-foreground" />
      ) : (
        <Lock className="h-4 w-4 text-muted-foreground" />
      )}
      <Switch
        checked={currentIsPublic}
        onCheckedChange={handleToggle}
        disabled={isDisabled}
        aria-label={currentIsPublic ? t("home.private.visibility.public") : t("home.private.visibility.private")}
      />
      <span className="text-sm text-muted-foreground">
        {currentIsPublic ? t("home.private.visibility.public") : t("home.private.visibility.private")}
      </span>
    </div>
  );

  // 如果无法设为私有（没有密码），显示带有提示的开关
  if (currentIsPublic && !canSetPrivate) {
    return (
      <Popover>
        <PopoverTrigger asChild>
          <div
            className={cn(
              "flex items-center gap-1 cursor-help",
              isDisabled && "opacity-50"
            )}
          >
            {renderSwitch()}
            <Info className="h-3.5 w-3.5 text-muted-foreground" />
          </div>
        </PopoverTrigger>
        <PopoverContent className="w-64 p-3" side="top">
          <p className="text-sm text-muted-foreground">
            {t("home.private.visibility.noPasswordTooltip")}
          </p>
        </PopoverContent>
      </Popover>
    );
  }

  return (
    <div
      className={cn(
        "flex items-center",
        isDisabled && "opacity-50"
      )}
    >
      {renderSwitch()}
    </div>
  );
}
