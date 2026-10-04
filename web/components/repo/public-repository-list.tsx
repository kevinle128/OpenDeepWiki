"use client";

import { useMemo, useState } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { useTranslations } from "@/hooks/use-translations";
import { PublicRepositoryCard } from "./public-repository-card";
import { RepositoryExplorerView } from "./repository-explorer-view";
import { LanguageTags } from "./language-tags";
import type { LanguageInfo } from "@/lib/recommendation-api";
import type { RepositoryItemResponse } from "@/types/repository";
import {
  GitBranch,
  XCircle,
  RefreshCw,
  Search,
  ChevronLeft,
  ChevronRight,
  LayoutGrid,
  ListTree,
} from "lucide-react";
import { cn } from "@/lib/utils";

interface PublicRepositoryListProps {
  keyword: string;
  repositories: RepositoryItemResponse[];
  languages: LanguageInfo[];
  loadError?: boolean;
  refreshing?: boolean;
  onRefresh?: () => void;
  className?: string;
}

const PAGE_SIZE = 12;

function matchesKeyword(repository: RepositoryItemResponse, keyword: string) {
  const query = keyword.trim().toLowerCase();
  if (!query) {
    return true;
  }

  return (
    repository.orgName.toLowerCase().includes(query) ||
    repository.repoName.toLowerCase().includes(query)
  );
}

function RepositoryGridSkeleton() {
  return (
    <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
      {[1, 2, 3, 4, 5, 6].map((i) => (
        <div key={i} className="p-4 border rounded-lg">
          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <Skeleton className="h-5 w-40" />
              <Skeleton className="h-6 w-20 rounded-full" />
            </div>
            <Skeleton className="h-4 w-32" />
          </div>
        </div>
      ))}
    </div>
  );
}

export function PublicRepositoryList({
  keyword,
  repositories,
  languages,
  loadError = false,
  refreshing = false,
  onRefresh,
  className,
}: PublicRepositoryListProps) {
  const t = useTranslations();
  const [selectedLanguage, setSelectedLanguage] = useState<string | null>(null);
  const [viewMode, setViewMode] = useState<"tree" | "grid">("tree");
  const [page, setPage] = useState(1);

  const isTreeView = viewMode === "tree";

  const filteredRepositories = useMemo(() => {
    return repositories.filter((repository) => {
      if (!matchesKeyword(repository, keyword)) {
        return false;
      }

      if (selectedLanguage && repository.primaryLanguage !== selectedLanguage) {
        return false;
      }

      return true;
    });
  }, [keyword, repositories, selectedLanguage]);

  const total = filteredRepositories.length;
  const totalPages = isTreeView ? 1 : Math.max(1, Math.ceil(total / PAGE_SIZE));
  const [previousFilters, setPreviousFilters] = useState({ keyword, selectedLanguage, viewMode });
  if (previousFilters.keyword !== keyword || previousFilters.selectedLanguage !== selectedLanguage || previousFilters.viewMode !== viewMode) {
    setPreviousFilters({ keyword, selectedLanguage, viewMode });
    setPage(1);
  } else if (page > totalPages) {
    setPage(totalPages);
  }
  const pagedRepositories = isTreeView
    ? filteredRepositories
    : filteredRepositories.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE);

  const handleLanguageChange = (language: string | null) => {
    setSelectedLanguage(language);
  };

  const handlePrevPage = () => {
    if (page > 1) {
      setPage(page - 1);
    }
  };

  const handleNextPage = () => {
    if (page < totalPages) {
      setPage(page + 1);
    }
  };

  const pagination =
    !isTreeView && totalPages > 1 ? (
      <div className="flex items-center justify-center gap-4 mt-8">
        <Button
          variant="outline"
          size="sm"
          onClick={handlePrevPage}
          disabled={page === 1 || refreshing}
        >
          <ChevronLeft className="h-4 w-4 mr-1" />
          {t("home.bookmarks.previous")}
        </Button>
        <span className="text-sm text-muted-foreground">
          {t("home.bookmarks.pageInfo")
            .replace("{current}", page.toString())
            .replace("{total}", totalPages.toString())}
        </span>
        <Button
          variant="outline"
          size="sm"
          onClick={handleNextPage}
          disabled={page === totalPages || refreshing}
        >
          {t("home.bookmarks.next")}
          <ChevronRight className="h-4 w-4 ml-1" />
        </Button>
      </div>
    ) : null;

  if (loadError && repositories.length === 0) {
    return (
      <div className={cn("w-full", className)}>
        <h2 className="mb-4 text-lg font-semibold tracking-tight">
          {t("home.publicRepository.title")}
        </h2>
        <div className="flex flex-col items-center justify-center py-12 text-center">
          <XCircle className="h-12 w-12 text-destructive mb-4" />
          <p className="text-muted-foreground mb-4">
            {t("home.publicRepository.loadError")}
          </p>
          <Button variant="outline" onClick={onRefresh} disabled={refreshing}>
            <RefreshCw className={cn("mr-2 h-4 w-4", refreshing && "animate-spin")} />
            {t("home.repository.retry")}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className={cn("w-full", className)}>
      <div className="mb-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div className="min-w-0">
          <h2 className="text-lg font-semibold tracking-tight">
            {t("home.publicRepository.title")}
          </h2>
          {total > 0 && (
            <p className="mt-0.5 text-xs text-muted-foreground">
              {t("home.repository.tree.count").replace("{count}", total.toString())}
            </p>
          )}
        </div>
        <div className="flex w-full items-center gap-2 sm:w-auto">
          <div className="grid flex-1 grid-cols-2 rounded-lg border border-border/70 bg-muted/20 p-1 sm:flex sm:flex-none">
            <Button
              variant={viewMode === "tree" ? "secondary" : "ghost"}
              size="sm"
              className="h-8 gap-1.5 rounded-md"
              onClick={() => setViewMode("tree")}
            >
              <ListTree className="h-4 w-4" />
              {t("home.repository.view.tree")}
            </Button>
            <Button
              variant={viewMode === "grid" ? "secondary" : "ghost"}
              size="sm"
              className="h-8 gap-1.5 rounded-md"
              onClick={() => setViewMode("grid")}
            >
              <LayoutGrid className="h-4 w-4" />
              {t("home.repository.view.grid")}
            </Button>
          </div>
          <Button
            variant="ghost"
            size="icon"
            className="h-8 w-8 shrink-0 rounded-lg"
            onClick={onRefresh}
            disabled={refreshing}
          >
            <RefreshCw className={cn("h-4 w-4", refreshing && "animate-spin")} />
          </Button>
        </div>
      </div>

      <LanguageTags
        languages={languages}
        selectedLanguage={selectedLanguage}
        onLanguageChange={handleLanguageChange}
        className="mb-6"
      />

      {refreshing && repositories.length === 0 ? (
        <RepositoryGridSkeleton />
      ) : repositories.length === 0 && !keyword && !selectedLanguage ? (
        <div className="flex flex-col items-center justify-center py-12 text-center">
          <GitBranch className="h-12 w-12 text-muted-foreground mb-4" />
          <p className="text-muted-foreground">
            {t("home.publicRepository.empty")}
          </p>
        </div>
      ) : pagedRepositories.length === 0 ? (
        <div className="flex flex-col items-center justify-center py-12 text-center">
          <Search className="h-12 w-12 text-muted-foreground mb-4" />
          <p className="text-muted-foreground">
            {t("home.publicRepository.noResults")}
          </p>
        </div>
      ) : (
        <>
          {viewMode === "tree" ? (
            <RepositoryExplorerView
              repositories={pagedRepositories}
              emptyMessage={t("home.publicRepository.empty")}
              labels={{
                treeTitle: t("home.repository.tree.title"),
                allRepositories: t("home.repository.tree.all"),
                repositoryCount: (count) =>
                  t("home.repository.tree.count").replace("{count}", count.toString()),
                emptyFolder: t("home.repository.tree.emptyFolder"),
                expandFolder: t("home.repository.tree.expandFolder"),
                collapseFolder: t("home.repository.tree.collapseFolder"),
                filterPlaceholder: t("home.repository.tree.filterPlaceholder"),
                noMatch: t("home.repository.tree.noMatch"),
              }}
              renderRepository={(repo) => (
                <PublicRepositoryCard repository={repo} variant="tree" />
              )}
            />
          ) : (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
              {pagedRepositories.map((repo) => (
                <PublicRepositoryCard key={repo.id} repository={repo} />
              ))}
            </div>
          )}
          {pagination}
        </>
      )}
    </div>
  );
}
