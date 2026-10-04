"use client";

import { useState, useEffect, useCallback } from "react";
import Link from "next/link";
import { AppLayout } from "@/components/app-layout";
import { useTranslations } from "@/hooks/use-translations";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Bookmark, Star, GitFork, Trash2, Loader2 } from "lucide-react";
import { useAuth } from "@/contexts/auth-context";
import { getUserBookmarks, removeBookmark, BookmarkItemResponse } from "@/lib/bookmark-api";
import { buildRepoBasePath } from "@/lib/repo-route";

export default function BookmarksPage() {
  const t = useTranslations();
  const { user, isAuthenticated, isLoading: authLoading } = useAuth();
  const [activeItem, setActiveItem] = useState(t("sidebar.bookmarks"));
  const [bookmarks, setBookmarks] = useState<BookmarkItemResponse[]>([]);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [removingId, setRemovingId] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [total, setTotal] = useState(0);
  const pageSize = 12;

  const fetchBookmarks = useCallback(() => {
    if (!user?.id) return;
    
    return getUserBookmarks(user.id, page, pageSize).then(response => {
      setBookmarks(response.items);
      setTotal(response.total);
      setError(null);
    }).catch(err => {
      setError(err instanceof Error ? err.message : t("home.bookmarks.loadError"));
    }).finally(() => {
      setIsLoading(false);
    });
  }, [user, page, t]);

  useEffect(() => {
    if (!authLoading && isAuthenticated) {
      fetchBookmarks();
    }
  }, [authLoading, isAuthenticated, fetchBookmarks]);

  const handleRemoveBookmark = async (repositoryId: string) => {
    if (!user?.id) return;
    
    setRemovingId(repositoryId);
    try {
      const response = await removeBookmark(repositoryId, user.id);
      if (response.success) {
        setBookmarks(prev => prev.filter(b => b.repositoryId !== repositoryId));
        setTotal(prev => prev - 1);
      } else {
        setError(response.errorMessage || t("home.bookmarks.removeError"));
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : t("home.bookmarks.removeError"));
    } finally {
      setRemovingId(null);
    }
  };

  const formatDate = (dateStr: string) => {
    const date = new Date(dateStr);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));
    
    if (diffDays === 0) return t("home.bookmarks.time.today");
    if (diffDays === 1) return t("home.bookmarks.time.yesterday");
    if (diffDays < 7) return t("home.bookmarks.time.daysAgo", { count: diffDays });
    if (diffDays < 30) return t("home.bookmarks.time.weeksAgo", { count: Math.floor(diffDays / 7) });
    return t("home.bookmarks.time.monthsAgo", { count: Math.floor(diffDays / 30) });
  };

  const formatNumber = (num: number) => {
    if (num >= 1000) return `${(num / 1000).toFixed(1)}k`;
    return num.toString();
  };

  // Show login prompt if not authenticated
  if (!authLoading && !isAuthenticated) {
    return (
      <AppLayout activeItem={activeItem} onItemClick={setActiveItem}>
        <div className="flex flex-1 flex-col items-center justify-center gap-4 p-4 md:p-6">
          <Bookmark className="h-16 w-16 text-muted-foreground" />
          <h2 className="text-xl font-semibold">{t("home.bookmarks.loginRequired")}</h2>
          <p className="text-muted-foreground">{t("home.bookmarks.loginHint")}</p>
        </div>
      </AppLayout>
    );
  }

  return (
    <AppLayout activeItem={activeItem} onItemClick={setActiveItem}>
      <div className="flex flex-1 flex-col gap-4 p-4 md:p-6">
        <div className="space-y-2">
          <h1 className="text-3xl font-bold tracking-tight">{t("home.bookmarks.pageTitle")}</h1>
          <p className="text-muted-foreground">
            {t("home.bookmarks.pageDescription")}
          </p>
        </div>

        {error && (
          <div className="bg-destructive/10 text-destructive px-4 py-2 rounded-md">
            {error}
          </div>
        )}

        {isLoading || authLoading ? (
          <div className="flex items-center justify-center py-12">
            <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
          </div>
        ) : bookmarks.length === 0 ? (
          <div className="flex flex-col items-center justify-center py-12 gap-4">
            <Bookmark className="h-16 w-16 text-muted-foreground" />
            <h2 className="text-xl font-semibold">{t("home.bookmarks.empty")}</h2>
            <p className="text-muted-foreground">{t("home.bookmarks.emptyHint")}</p>
          </div>
        ) : (
          <>
            <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
              {bookmarks.map((repo) => (
                <Link 
                  key={repo.bookmarkId} 
                  href={buildRepoBasePath(repo.orgName, repo.repoName)}
                  className="block"
                >
                  <Card className="hover:shadow-lg transition-shadow cursor-pointer h-full">
                    <CardHeader>
                      <div className="flex items-start justify-between">
                        <div className="flex-1 min-w-0">
                          <div className="flex items-center gap-2">
                            <Bookmark className="h-4 w-4 text-yellow-500 fill-yellow-500 flex-shrink-0" />
                            <CardTitle className="text-lg truncate">{repo.repoName}</CardTitle>
                          </div>
                          <CardDescription className="text-sm text-muted-foreground mt-1 truncate">
                            {repo.orgName}
                          </CardDescription>
                        </div>
                        <Button 
                          variant="ghost" 
                          size="sm" 
                          className="h-8 w-8 p-0 text-muted-foreground hover:text-destructive flex-shrink-0"
                          onClick={(e) => {
                            e.preventDefault();
                            e.stopPropagation();
                            handleRemoveBookmark(repo.repositoryId);
                          }}
                          disabled={removingId === repo.repositoryId}
                        >
                          {removingId === repo.repositoryId ? (
                            <Loader2 className="h-4 w-4 animate-spin" />
                          ) : (
                            <Trash2 className="h-4 w-4" />
                          )}
                        </Button>
                      </div>
                    </CardHeader>
                    <CardContent>
                      <p className="text-sm mb-3 line-clamp-2">{repo.description || t("home.bookmarks.noDescription")}</p>
                      <div className="flex items-center justify-between">
                        <div className="flex items-center gap-3 text-sm text-muted-foreground">
                          <div className="flex items-center gap-1">
                            <Star className="h-3 w-3" />
                            <span>{formatNumber(repo.starCount)}</span>
                          </div>
                          <div className="flex items-center gap-1">
                            <GitFork className="h-3 w-3" />
                            <span>{formatNumber(repo.forkCount)}</span>
                          </div>
                        </div>
                        <span className="text-xs text-muted-foreground">{formatDate(repo.bookmarkedAt)}</span>
                      </div>
                    </CardContent>
                  </Card>
                </Link>
              ))}
            </div>

            {/* Pagination */}
            {total > pageSize && (
              <div className="flex items-center justify-center gap-2 mt-4">
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => { setIsLoading(true); setError(null); setPage(p => Math.max(1, p - 1)); }}
                  disabled={page === 1}
                >
                  {t("home.bookmarks.previous")}
                </Button>
                <span className="text-sm text-muted-foreground">
                  {t("home.bookmarks.pageInfo", { current: page, total: Math.ceil(total / pageSize) })}
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => { setIsLoading(true); setError(null); setPage(p => p + 1); }}
                  disabled={page >= Math.ceil(total / pageSize)}
                >
                  {t("home.bookmarks.next")}
                </Button>
              </div>
            )}
          </>
        )}
      </div>
    </AppLayout>
  );
}
