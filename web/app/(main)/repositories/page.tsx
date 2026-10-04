"use client";

import { Suspense, useCallback, useEffect, useMemo } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";

import { AppLayout } from "@/components/app-layout";
import { RepositoryWorkspace, type WorkspaceState } from "@/components/repositories/repository-workspace";
import { Skeleton } from "@/components/ui/skeleton";
import { useAuth } from "@/contexts/auth-context";
import { useTranslations } from "@/hooks/use-translations";
import { parseWorkspaceState, serializeWorkspaceState } from "./workspace-url-state";

function RepositoriesContent() {
  const t = useTranslations();
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const { isAuthenticated, isLoading } = useAuth();
  const paramsKey = searchParams.toString();
  const state = useMemo(() => parseWorkspaceState(new URLSearchParams(paramsKey)), [paramsKey]);

  useEffect(() => {
    if (!isLoading && !isAuthenticated) router.replace("/auth");
  }, [isLoading, isAuthenticated, router]);

  const handleStateChange = useCallback(
    (next: WorkspaceState) => {
      const query = serializeWorkspaceState(next);
      router.replace(query ? `${pathname}?${query}` : pathname, { scroll: false });
    },
    [router, pathname],
  );

  const ready = !isLoading && isAuthenticated;

  return (
    <AppLayout activeItem={t("sidebar.repositories")}>
      <div className="flex min-h-0 flex-1 flex-col">
        <div className="px-4 py-4 md:px-6">
          <h1 className="text-2xl font-bold tracking-tight">{t("repositories.title")}</h1>
          <p className="text-muted-foreground text-sm">{t("repositories.description")}</p>
        </div>
        {ready ? (
          <RepositoryWorkspace state={state} onStateChange={handleStateChange} />
        ) : (
          <div role="status" className="flex flex-col gap-3 p-6">
            <span className="sr-only">{t("repositories.loadingPage")}</span>
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-64 w-full" />
          </div>
        )}
      </div>
    </AppLayout>
  );
}

export default function RepositoriesPage() {
  return (
    <Suspense fallback={null}>
      <RepositoriesContent />
    </Suspense>
  );
}
