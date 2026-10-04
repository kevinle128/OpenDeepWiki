"use client";

import { useState } from "react";
import Image from "next/image";
import { useRouter, usePathname } from "next/navigation";
import { GitBranch, Star, GitFork, Code, Plus, Loader2, ExternalLink, Home } from "lucide-react";
import { Button } from "@/components/ui/button";
import { submitRepository } from "@/lib/repository-api";
import { useAuth } from "@/contexts/auth-context";
import { useTranslations } from "@/hooks/use-translations";
import { defaultWikiLanguage } from "@/i18n/config";
import type { GitRepoCheckResponse } from "@/types/repository";

interface RepositoryNotFoundProps {
  owner: string;
  repo: string;
  gitHubInfo: GitRepoCheckResponse | null;
}

export function RepositoryNotFound({ owner, repo, gitHubInfo }: RepositoryNotFoundProps) {
  const router = useRouter();
  const pathname = usePathname();
  const t = useTranslations();
  const { isAuthenticated } = useAuth();
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleSubmit = async () => {
    if (!gitHubInfo?.gitUrl || !gitHubInfo.defaultBranch) return;

    // 检查是否已登录
    if (!isAuthenticated) {
      // 未登录，跳转到登录页并携带当前URL
      const returnUrl = encodeURIComponent(pathname);
      router.push(`/auth?returnUrl=${returnUrl}`);
      return;
    }
    
    setIsSubmitting(true);
    setError(null);
    
    try {
      await submitRepository({
        gitUrl: gitHubInfo.gitUrl,
        repoName: repo,
        orgName: owner,
        branchName: gitHubInfo.defaultBranch,
        languageCode: defaultWikiLanguage,
        isPublic: true,
        generateSkill: true,
      });
      
      // 刷新页面以显示处理状态
      router.refresh();
    } catch (err) {
      setError(err instanceof Error ? err.message : t("common.submitFailed"));
    } finally {
      setIsSubmitting(false);
    }
  };

  // GitHub上也不存在
  if (!gitHubInfo?.exists) {
    return (
      <div className="flex min-h-[80vh] items-center justify-center p-4">
        <div className="w-full max-w-md text-center">
          <div className="rounded-full bg-muted/50 p-4 w-20 h-20 mx-auto mb-6 flex items-center justify-center">
            <GitBranch className="h-10 w-10 text-muted-foreground" />
          </div>
          <h1 className="text-2xl font-bold mb-2">{t("common.repository.notFound.title")}</h1>
          <p className="text-muted-foreground mb-6">
            {t("common.repository.notFound.description", { owner, repo })}
          </p>
          <p className="text-sm text-muted-foreground mb-6">
            {t("common.repository.notFound.privateOrNotExist")}
          </p>
          <Button variant="outline" onClick={() => router.push("/")}>
            <Home className="h-4 w-4 mr-2" />
            {t("common.backToHome")}
          </Button>
        </div>
      </div>
    );
  }

  // GitHub上存在，可以提交生成
  return (
    <div className="flex min-h-[80vh] items-center justify-center p-4">
      <div className="w-full max-w-lg">
        <div className="rounded-xl border bg-card p-6 shadow-sm">
          {/* 仓库头部 */}
          <div className="flex items-start gap-4 mb-6">
            {gitHubInfo.avatarUrl && (
              <Image
                src={gitHubInfo.avatarUrl}
                alt={owner}
                width={64}
                height={64}
                unoptimized
                className="w-16 h-16 rounded-lg"
              />
            )}
            <div className="flex-1 min-w-0">
              <h1 className="text-xl font-bold truncate">
                {owner}/{repo}
              </h1>
              {gitHubInfo.description && (
                <p className="text-sm text-muted-foreground mt-1 line-clamp-2">
                  {gitHubInfo.description}
                </p>
              )}
            </div>
          </div>

          {/* 统计信息 */}
          <div className="flex items-center gap-4 text-sm text-muted-foreground mb-6">
            <div className="flex items-center gap-1">
              <Star className="h-4 w-4" />
              <span>{gitHubInfo.starCount.toLocaleString()}</span>
            </div>
            <div className="flex items-center gap-1">
              <GitFork className="h-4 w-4" />
              <span>{gitHubInfo.forkCount.toLocaleString()}</span>
            </div>
            {gitHubInfo.language && (
              <div className="flex items-center gap-1">
                <Code className="h-4 w-4" />
                <span>{gitHubInfo.language}</span>
              </div>
            )}
            {gitHubInfo.defaultBranch && (
              <div className="flex items-center gap-1">
                <GitBranch className="h-4 w-4" />
                <span>{gitHubInfo.defaultBranch}</span>
              </div>
            )}
          </div>

          {/* 提示信息 */}
          <div className="bg-blue-500/10 border border-blue-500/20 rounded-lg p-4 mb-6">
            <p className="text-sm text-blue-600 dark:text-blue-400">
              {t("common.repository.notFound.existsButNoDoc")}
            </p>
          </div>

          {/* 错误信息 */}
          {error && (
            <div className="bg-red-500/10 border border-red-500/20 rounded-lg p-4 mb-6">
              <p className="text-sm text-red-600 dark:text-red-400">{error}</p>
            </div>
          )}

          {/* 操作按钮 */}
          <div className="flex gap-3">
            <Button
              className="flex-1"
              onClick={handleSubmit}
              disabled={isSubmitting}
            >
              {isSubmitting ? (
                <>
                  <Loader2 className="h-4 w-4 mr-2 animate-spin" />
                  {t("common.submitting")}
                </>
              ) : (
                <>
                  <Plus className="h-4 w-4 mr-2" />
                  {t("common.repository.notFound.generateWiki")}
                </>
              )}
            </Button>
            <Button
              variant="outline"
              onClick={() => window.open(gitHubInfo.gitUrl ?? "", "_blank")}
            >
              <ExternalLink className="h-4 w-4 mr-2" />
              GitHub
            </Button>
          </div>

          {/* 返回首页按钮 */}
          <div className="mt-4 pt-4 border-t">
            <Button
              variant="ghost"
              className="w-full"
              onClick={() => router.push("/")}
            >
              <Home className="h-4 w-4 mr-2" />
              {t("common.backToHome")}
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
