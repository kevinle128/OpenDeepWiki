"use client";

import { useState, useEffect } from "react";
import { useTranslations } from "@/hooks/use-translations";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Loader2, Search, ChevronLeft, ChevronRight } from "lucide-react";
import {
  getAppLogs,
  PaginatedChatLogsDto,
} from "@/lib/apps-api";

interface AppLogsTableProps {
  appId: string;
}

export function AppLogsTable({ appId }: AppLogsTableProps) {
  const t = useTranslations();
  const [logs, setLogs] = useState<PaginatedChatLogsDto | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [keyword, setKeyword] = useState("");
  const [searchKeyword, setSearchKeyword] = useState("");
  const [page, setPage] = useState(1);
  const pageSize = 10;
  const requestKey = `${appId}:${searchKeyword}:${page}`;
  const [lastRequestKey, setLastRequestKey] = useState(requestKey);
  if (requestKey !== lastRequestKey) {
    setLastRequestKey(requestKey);
    setIsLoading(true);
    setError(null);
  }

  useEffect(() => {
    let active = true;
    getAppLogs(appId, {
        keyword: searchKeyword || undefined,
        page,
        pageSize,
      }).then((data) => {
        if (active) setLogs(data);
      }).catch((err: unknown) => {
        if (active) setError(err instanceof Error ? err.message : "Failed to load logs");
      }).finally(() => {
        if (active) setIsLoading(false);
      });
    return () => { active = false; };
  }, [appId, searchKeyword, page]);

  const handleSearch = () => {
    setSearchKeyword(keyword);
    setPage(1);
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") {
      handleSearch();
    }
  };

  const formatDateTime = (dateStr: string) => {
    const date = new Date(dateStr);
    return date.toLocaleString();
  };

  const truncateText = (text: string, maxLength: number) => {
    if (text.length <= maxLength) return text;
    return text.slice(0, maxLength) + "...";
  };

  if (isLoading && !logs) {
    return (
      <Card>
        <CardContent className="flex items-center justify-center py-12">
          <Loader2 className="h-8 w-8 animate-spin text-muted-foreground" />
        </CardContent>
      </Card>
    );
  }

  if (error) {
    return (
      <Card>
        <CardContent className="py-12">
          <div className="text-center text-destructive">{error}</div>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardContent className="pt-6">
        {/* Search */}
        <div className="flex items-center gap-2 mb-4">
          <div className="relative flex-1 max-w-md">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
            <Input
              value={keyword}
              onChange={(e) => setKeyword(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder={t("apps.logs.searchPlaceholder")}
              className="pl-9"
            />
          </div>
          <Button onClick={handleSearch} disabled={isLoading}>
            {isLoading ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              t("common.search")
            )}
          </Button>
        </div>

        {/* Table */}
        {!logs || logs.items.length === 0 ? (
          <div className="text-center text-muted-foreground py-12">
            {t("apps.logs.noLogs")}
          </div>
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full">
                <thead>
                  <tr className="border-b">
                    <th className="text-left py-3 px-2 font-medium text-sm">
                      {t("apps.logs.question")}
                    </th>
                    <th className="text-left py-3 px-2 font-medium text-sm">
                      {t("apps.logs.answer")}
                    </th>
                    <th className="text-left py-3 px-2 font-medium text-sm">
                      {t("apps.logs.model")}
                    </th>
                    <th className="text-left py-3 px-2 font-medium text-sm">
                      {t("apps.logs.tokens")}
                    </th>
                    <th className="text-left py-3 px-2 font-medium text-sm">
                      {t("apps.logs.domain")}
                    </th>
                    <th className="text-left py-3 px-2 font-medium text-sm">
                      {t("apps.logs.time")}
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {logs.items.map((log) => (
                    <tr key={log.id} className="border-b hover:bg-muted/50">
                      <td className="py-3 px-2 text-sm max-w-[200px]">
                        <span title={log.question}>
                          {truncateText(log.question, 50)}
                        </span>
                      </td>
                      <td className="py-3 px-2 text-sm max-w-[200px] text-muted-foreground">
                        <span title={log.answerSummary || ""}>
                          {log.answerSummary
                            ? truncateText(log.answerSummary, 50)
                            : "-"}
                        </span>
                      </td>
                      <td className="py-3 px-2 text-sm">
                        {log.modelUsed || "-"}
                      </td>
                      <td className="py-3 px-2 text-sm">
                        <span className="text-muted-foreground">
                          {log.inputTokens} / {log.outputTokens}
                        </span>
                      </td>
                      <td className="py-3 px-2 text-sm text-muted-foreground">
                        {log.sourceDomain || "-"}
                      </td>
                      <td className="py-3 px-2 text-sm text-muted-foreground whitespace-nowrap">
                        {formatDateTime(log.createdAt)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            {/* Pagination */}
            {logs.totalPages > 1 && (
              <div className="flex items-center justify-between mt-4">
                <div className="text-sm text-muted-foreground">
                  {t("common.admin.total", { count: logs.totalCount })}
                </div>
                <div className="flex items-center gap-2">
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => setPage((p) => Math.max(1, p - 1))}
                    disabled={page === 1 || isLoading}
                  >
                    <ChevronLeft className="h-4 w-4" />
                  </Button>
                  <span className="text-sm">
                    {page} / {logs.totalPages}
                  </span>
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => setPage((p) => p + 1)}
                    disabled={page >= logs.totalPages || isLoading}
                  >
                    <ChevronRight className="h-4 w-4" />
                  </Button>
                </div>
              </div>
            )}
          </>
        )}
      </CardContent>
    </Card>
  );
}
