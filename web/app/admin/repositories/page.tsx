"use client";

import React, {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import Link from "next/link";
import {
  AlertTriangle,
  Check,
  ChevronDown,
  Eye,
  ExternalLink,
  GitFork,
  Globe,
  Loader2,
  Lock,
  MoreHorizontal,
  Plus,
  RefreshCw,
  RotateCcw,
  Search,
  SlidersHorizontal,
  Star,
  Trash2,
} from "lucide-react";
import { toast } from "sonner";
import { useLocale } from "next-intl";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Dialog,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { PageHeader } from "@/components/admin/page-header";
import { DataTableShell } from "@/components/admin/data-table";
import { TablePagination } from "@/components/admin/table-pagination";
import {
  RepoStatusBadge,
  REPO_STATUS_TONE,
  StatusBadge,
} from "@/components/admin/status-badge";
import {
  getRepositories,
  deleteRepository,
  updateRepositoryStatus,
  syncRepositoryStats,
  batchSyncRepositoryStats,
  batchRegenerateRepositories,
  batchDeleteRepositories,
  AdminRepository,
  RepositoryListResponse,
} from "@/lib/admin-api";
import {
  getRepositorySourceTypeLabelKey,
  isGitRepositorySource,
} from "@/lib/repository-source";
import { RepositorySubmitForm } from "@/components/repo/repository-submit-form";
import { useTranslations } from "@/hooks/use-translations";

const SEARCH_DEBOUNCE_MS = 400;

export default function AdminRepositoriesPage() {
  const [data, setData] = useState<RepositoryListResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [isSubmitDialogOpen, setIsSubmitDialogOpen] = useState(false);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const [searchInput, setSearchInput] = useState("");
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("all");
  const queryKey = JSON.stringify([page, pageSize, search, status]);
  const [loadingQuery, setLoadingQuery] = useState(queryKey);
  if (loadingQuery !== queryKey) {
    setLoadingQuery(queryKey);
    setLoading(true);
  }
  const [selectedRepo, setSelectedRepo] = useState<AdminRepository | null>(
    null
  );
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [syncing, setSyncing] = useState<string | null>(null);
  const [statusUpdatingId, setStatusUpdatingId] = useState<string | null>(null);
  const [batchSyncing, setBatchSyncing] = useState(false);
  const [batchRegenerating, setBatchRegenerating] = useState(false);
  const [batchDeleting, setBatchDeleting] = useState(false);
  const [showBatchRegenerateConfirm, setShowBatchRegenerateConfirm] = useState(false);
  const [showBatchDeleteConfirm, setShowBatchDeleteConfirm] = useState(false);
  const searchTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const t = useTranslations();
  const locale = useLocale();
  const dateLocale = locale === "zh" ? "zh-CN" : locale;

  const statusOptions = [
    { value: "all", label: t("admin.repositories.allStatus") },
    { value: "0", label: t("admin.repositories.pending") },
    { value: "1", label: t("admin.repositories.processing") },
    { value: "2", label: t("admin.repositories.completed") },
    { value: "3", label: t("admin.repositories.failed") },
  ];

  const statusLabels: Record<number, string> = useMemo(
    () => ({
      0: t("admin.repositories.pending"),
      1: t("admin.repositories.processing"),
      2: t("admin.repositories.completed"),
      3: t("admin.repositories.failed"),
    }),
    [t]
  );

  const loadData = useCallback(() => {
    return getRepositories(
        page,
        pageSize,
        search || undefined,
        status === "all" ? undefined : parseInt(status)
      ).then((result) => {
      setData(result);
      setSelectedIds(new Set());
    }).catch(error => {
      console.error("Failed to fetch repositories:", error);
      toast.error(t("admin.toast.fetchRepoFailed"));
    }).finally(() => {
      setLoading(false);
    });
  }, [page, pageSize, search, status, t]);

  const fetchData = useCallback(() => {
    setLoading(true);
    return loadData();
  }, [loadData]);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  // Debounced search: reset to page 1 when the keyword settles.
  const handleSearchInput = (value: string) => {
    setSearchInput(value);
    if (searchTimer.current) clearTimeout(searchTimer.current);
    searchTimer.current = setTimeout(() => {
      setPage(1);
      setSearch(value.trim());
    }, SEARCH_DEBOUNCE_MS);
  };

  const handleSubmitSuccess = useCallback(() => {
    setIsSubmitDialogOpen(false);
    fetchData();
  }, [fetchData]);

  const handleDelete = async () => {
    if (!deleteId) return;
    try {
      await deleteRepository(deleteId);
      toast.success(t("admin.toast.deleteSuccess"));
      setDeleteId(null);
      fetchData();
    } catch {
      toast.error(t("admin.toast.deleteFailed"));
    }
  };

  const handleStatusChange = async (
    id: string,
    newStatus: number,
    currentStatus?: number
  ) => {
    if (currentStatus === newStatus) return;
    setStatusUpdatingId(id);
    try {
      await updateRepositoryStatus(id, newStatus);
      toast.success(t("admin.toast.statusUpdateSuccess"));
      fetchData();
    } catch {
      toast.error(t("admin.toast.statusUpdateFailed"));
    } finally {
      setStatusUpdatingId((prev) => (prev === id ? null : prev));
    }
  };

  const handleSyncStats = async (id: string) => {
    setSyncing(id);
    try {
      const result = await syncRepositoryStats(id);
      if (result.success) {
        toast.success(
          `${t("admin.toast.syncSuccess")}: ${t("admin.repositories.star")} ${result.starCount}, ${t("admin.repositories.fork")} ${result.forkCount}`
        );
        fetchData();
      } else {
        toast.error(result.message || t("admin.toast.syncFailed"));
      }
    } catch {
      toast.error(t("admin.toast.syncFailed"));
    } finally {
      setSyncing(null);
    }
  };

  const selectedGitRepoIds = useMemo(() => {
    const items = data?.items ?? [];
    return items
      .filter(
        (item) =>
          selectedIds.has(item.id) &&
          isGitRepositorySource(item.sourceType, item.sourceTypeName)
      )
      .map((item) => item.id);
  }, [data, selectedIds]);

  const handleBatchSync = async () => {
    if (selectedIds.size === 0) {
      toast.warning(t("admin.repositories.selectFirst"));
      return;
    }
    if (selectedGitRepoIds.length === 0) {
      toast.warning(t("admin.repositories.syncStatsNotSupported"));
      return;
    }
    setBatchSyncing(true);
    try {
      const result = await batchSyncRepositoryStats(selectedGitRepoIds);
      if (selectedGitRepoIds.length < selectedIds.size) {
        toast.warning(
          t("admin.repositories.batchSyncSkippedNonGit", {
            count: selectedIds.size - selectedGitRepoIds.length,
          })
        );
      }
      toast.success(
        t("admin.repositories.batchSyncResult", {
          success: result.successCount,
          failed: result.failedCount,
        })
      );
      await fetchData();
    } catch {
      toast.error(t("admin.toast.syncFailed"));
    } finally {
      setBatchSyncing(false);
    }
  };

  const handleBatchRegenerate = async () => {
    if (selectedIds.size === 0) return;

    setBatchRegenerating(true);
    try {
      const result = await batchRegenerateRepositories(Array.from(selectedIds));
      const failedItems = result.results.filter((item) => !item.success);
      const resultMessage = t("admin.repositories.batchRegenerateResult", {
        success: result.successCount,
        failed: result.failedCount,
      });

      if (failedItems.length > 0) {
        console.warn("Some repositories failed to regenerate:", failedItems);
        toast.warning(resultMessage);
      } else {
        toast.success(resultMessage);
      }

      setShowBatchRegenerateConfirm(false);
      await fetchData();
    } catch (error) {
      console.error("Failed to regenerate repositories:", error);
      toast.error(t("admin.repositories.batchRegenerateFailed"));
    } finally {
      setBatchRegenerating(false);
    }
  };

  const handleBatchDelete = async () => {
    setBatchDeleting(true);
    try {
      const result = await batchDeleteRepositories(Array.from(selectedIds));
      toast.success(
        t("admin.repositories.batchDeleteResult", {
          success: result.successCount,
          failed: result.failedCount,
        })
      );
      setShowBatchDeleteConfirm(false);
      fetchData();
    } catch {
      toast.error(t("admin.toast.deleteFailed"));
    } finally {
      setBatchDeleting(false);
    }
  };

  const toggleSelectAll = () => {
    if (!data) return;
    if (selectedIds.size === data.items.length) {
      setSelectedIds(new Set());
    } else {
      setSelectedIds(new Set(data.items.map((r) => r.id)));
    }
  };

  const toggleSelect = (id: string) => {
    const newSet = new Set(selectedIds);
    if (newSet.has(id)) {
      newSet.delete(id);
    } else {
      newSet.add(id);
    }
    setSelectedIds(newSet);
  };

  const allSelected =
    data && data.items.length > 0 && selectedIds.size === data.items.length;
  const someSelected = selectedIds.size > 0 && !allSelected;
  const batchOperationInProgress =
    batchSyncing || batchRegenerating || batchDeleting;
  const isEmpty = !loading && (data?.items.length ?? 0) === 0;

  const toolbar = (
    <>
      <div className="relative min-w-[220px] flex-1 sm:max-w-xs">
        <Search className="absolute left-2.5 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          placeholder={t("admin.repositories.searchPlaceholder")}
          value={searchInput}
          onChange={(e) => handleSearchInput(e.target.value)}
          className="h-9 pl-8"
        />
      </div>
      <Select
        value={status}
        onValueChange={(v) => {
          setStatus(v);
          setPage(1);
        }}
      >
        <SelectTrigger size="sm" className="h-9 w-[150px]">
          <SlidersHorizontal className="mr-1 h-3.5 w-3.5 text-muted-foreground" />
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {statusOptions.map((opt) => (
            <SelectItem key={opt.value} value={opt.value}>
              {opt.label}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>

      {selectedIds.size > 0 && (
        <div className="flex items-center gap-2 rounded-md border bg-background px-2.5 py-1.5">
          <span className="text-xs text-muted-foreground">
            {t("admin.repositories.selectedCount", { count: selectedIds.size })}
          </span>
          <Button
            variant="outline"
            size="sm"
            className="h-7"
            onClick={handleBatchSync}
            disabled={batchOperationInProgress}
          >
            {batchSyncing ? (
              <Loader2 className="mr-1 h-3.5 w-3.5 animate-spin" />
            ) : (
              <RotateCcw className="mr-1 h-3.5 w-3.5" />
            )}
            {t("admin.repositories.batchSync")}
          </Button>
          <Button
            variant="outline"
            size="sm"
            className="h-7"
            onClick={() => setShowBatchRegenerateConfirm(true)}
            disabled={batchOperationInProgress}
          >
            {batchRegenerating ? (
              <Loader2 className="mr-1 h-3.5 w-3.5 animate-spin" />
            ) : (
              <RefreshCw className="mr-1 h-3.5 w-3.5" />
            )}
            {t("admin.repositories.batchRegenerate")}
          </Button>
          <Button
            variant="destructive"
            size="sm"
            className="h-7"
            onClick={() => setShowBatchDeleteConfirm(true)}
            disabled={batchOperationInProgress}
          >
            <Trash2 className="mr-1 h-3.5 w-3.5" />
            {t("admin.repositories.batchDelete")}
          </Button>
          <Button
            variant="ghost"
            size="sm"
            className="h-7"
            onClick={() => setSelectedIds(new Set())}
            disabled={batchOperationInProgress}
          >
            {t("admin.repositories.cancelSelect")}
          </Button>
        </div>
      )}

      <div className="ml-auto">
        <Button
          variant="ghost"
          size="icon"
          className="h-9 w-9"
          onClick={fetchData}
          disabled={loading}
          title={t("admin.common.refresh")}
        >
          <RefreshCw className={`h-4 w-4 ${loading ? "animate-spin" : ""}`} />
        </Button>
      </div>
    </>
  );

  return (
    <div className="space-y-5">
      <PageHeader
        title={t("admin.repositories.title")}
        actions={
          <Button onClick={() => setIsSubmitDialogOpen(true)}>
            <Plus className="mr-1.5 h-4 w-4" />
            {t("home.repository.submitTitle")}
          </Button>
        }
      />

      <DataTableShell
        toolbar={toolbar}
        loading={loading}
        empty={isEmpty}
        emptyTitle={t("admin.repositories.noReposForFilter")}
        footer={
          <TablePagination
            page={page}
            pageSize={pageSize}
            total={data?.total ?? 0}
            onPageChange={setPage}
            onPageSizeChange={(size) => {
              setPageSize(size);
              setPage(1);
            }}
          />
        }
      >
        <Table>
          <TableHeader>
            <TableRow className="bg-muted/40 hover:bg-muted/40">
              <TableHead className="w-10 pl-4">
                <Checkbox
                  checked={
                    allSelected ? true : someSelected ? "indeterminate" : false
                  }
                  onCheckedChange={toggleSelectAll}
                  aria-label={t("admin.common.selectAll")}
                />
              </TableHead>
              <TableHead>{t("admin.repositories.repository")}</TableHead>
              <TableHead>{t("admin.repositories.visibility")}</TableHead>
              <TableHead>{t("admin.repositories.status")}</TableHead>
              <TableHead>{t("admin.repositories.statistics")}</TableHead>
              <TableHead>{t("admin.repositories.createdAt")}</TableHead>
              <TableHead className="pr-4 text-right">
                {t("admin.repositories.operations")}
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {data?.items.map((repo) => (
              <TableRow
                key={repo.id}
                data-state={selectedIds.has(repo.id) ? "selected" : undefined}
              >
                <TableCell className="pl-4">
                  <Checkbox
                    checked={selectedIds.has(repo.id)}
                    onCheckedChange={() => toggleSelect(repo.id)}
                    aria-label={`Select ${repo.repoName}`}
                  />
                </TableCell>
                <TableCell>
                  <div className="min-w-0 max-w-md">
                    <div className="flex items-center gap-2">
                      <Link
                        href={`/admin/repositories/${repo.id}`}
                        className="truncate font-medium underline-offset-4 hover:text-primary hover:underline"
                        title={t("admin.repositories.manageRepo")}
                      >
                        {repo.orgName}/{repo.repoName}
                      </Link>
                      <Badge
                        variant="secondary"
                        className="shrink-0 px-1.5 py-0 text-[10px] font-normal text-muted-foreground"
                      >
                        {t(
                          `admin.repositories.${getRepositorySourceTypeLabelKey(repo.sourceType, repo.sourceTypeName)}`
                        )}
                      </Badge>
                    </div>
                    <p className="mt-0.5 truncate text-xs text-muted-foreground">
                      {repo.sourceLocation || repo.gitUrl}
                    </p>
                    {(repo.branchGenerationActiveCount > 0 ||
                      repo.branchGenerationFailedCount > 0) && (
                      <div className="mt-1.5 flex flex-wrap gap-1.5">
                        {repo.branchGenerationActiveCount > 0 && (
                          <Badge variant="secondary" className="gap-1 text-[10px]">
                            <Loader2 className="h-2.5 w-2.5 animate-spin" />
                            branch running {repo.branchGenerationActiveCount}
                          </Badge>
                        )}
                        {repo.branchGenerationFailedCount > 0 && (
                          <Badge variant="destructive" className="gap-1 text-[10px]">
                            <AlertTriangle className="h-2.5 w-2.5" />
                            branch failed {repo.branchGenerationFailedCount}
                          </Badge>
                        )}
                      </div>
                    )}
                  </div>
                </TableCell>
                <TableCell>
                  {repo.isPublic ? (
                    <span className="inline-flex items-center gap-1 text-sm text-emerald-600 dark:text-emerald-400">
                      <Globe className="h-3.5 w-3.5" />
                      {t("admin.repositories.public")}
                    </span>
                  ) : (
                    <span className="inline-flex items-center gap-1 text-sm text-muted-foreground">
                      <Lock className="h-3.5 w-3.5" />
                      {t("admin.repositories.private")}
                    </span>
                  )}
                </TableCell>
                <TableCell>
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <button
                        type="button"
                        disabled={statusUpdatingId === repo.id}
                        className="group inline-flex items-center gap-1 rounded-md outline-none disabled:opacity-60"
                      >
                        <RepoStatusBadge status={repo.status} />
                        {statusUpdatingId === repo.id ? (
                          <Loader2 className="h-3 w-3 animate-spin text-muted-foreground" />
                        ) : (
                          <ChevronDown className="h-3 w-3 text-muted-foreground opacity-0 transition-opacity group-hover:opacity-100" />
                        )}
                      </button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="start" className="w-[170px]">
                      <DropdownMenuLabel className="text-xs text-muted-foreground">
                        {t("admin.repositories.status")}
                      </DropdownMenuLabel>
                      {[0, 1, 2, 3].map((statusValue) => (
                        <DropdownMenuItem
                          key={statusValue}
                          disabled={repo.status === statusValue}
                          onClick={() =>
                            handleStatusChange(repo.id, statusValue, repo.status)
                          }
                          className="justify-between"
                        >
                          <StatusBadge
                            tone={REPO_STATUS_TONE[statusValue]}
                            label={statusLabels[statusValue]}
                            className="border-transparent bg-transparent px-0"
                          />
                          {repo.status === statusValue ? (
                            <Check className="h-3.5 w-3.5 text-primary" />
                          ) : null}
                        </DropdownMenuItem>
                      ))}
                    </DropdownMenuContent>
                  </DropdownMenu>
                </TableCell>
                <TableCell>
                  <div className="flex items-center gap-3 text-xs text-muted-foreground tabular-nums">
                    <span className="inline-flex items-center gap-1">
                      <Star className="h-3.5 w-3.5" />
                      {repo.starCount}
                    </span>
                    <span className="inline-flex items-center gap-1">
                      <GitFork className="h-3.5 w-3.5" />
                      {repo.forkCount}
                    </span>
                    <span className="inline-flex items-center gap-1">
                      <Eye className="h-3.5 w-3.5" />
                      {repo.viewCount}
                    </span>
                  </div>
                </TableCell>
                <TableCell className="text-sm text-muted-foreground whitespace-nowrap">
                  {new Date(repo.createdAt).toLocaleDateString(dateLocale)}
                </TableCell>
                <TableCell className="pr-4 text-right">
                  <div className="flex items-center justify-end gap-1">
                    <Button
                      asChild
                      variant="ghost"
                      size="sm"
                      className="h-8 px-2 text-xs"
                    >
                      <Link
                        href={`/admin/repositories/${repo.id}`}
                        title={t("admin.repositories.manageRepo")}
                      >
                        <ExternalLink className="mr-1 h-3.5 w-3.5" />
                        {t("admin.repositories.manageRepo")}
                      </Link>
                    </Button>
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button variant="ghost" size="icon" className="h-8 w-8">
                          <MoreHorizontal className="h-4 w-4" />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end" className="w-[180px]">
                        <DropdownMenuItem
                          onClick={() => setSelectedRepo(repo)}
                        >
                          <Eye className="mr-2 h-4 w-4" />
                          {t("admin.repositories.viewDetail")}
                        </DropdownMenuItem>
                        <DropdownMenuItem
                          disabled={
                            syncing === repo.id ||
                            !isGitRepositorySource(
                              repo.sourceType,
                              repo.sourceTypeName
                            )
                          }
                          onClick={() => handleSyncStats(repo.id)}
                        >
                          {syncing === repo.id ? (
                            <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                          ) : (
                            <RotateCcw className="mr-2 h-4 w-4" />
                          )}
                          {t("admin.repositories.syncStats")}
                        </DropdownMenuItem>
                        <DropdownMenuSeparator />
                        <DropdownMenuItem
                          variant="destructive"
                          onClick={() => setDeleteId(repo.id)}
                        >
                          <Trash2 className="mr-2 h-4 w-4" />
                          {t("admin.common.delete")}
                        </DropdownMenuItem>
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </div>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </DataTableShell>

      <Dialog open={isSubmitDialogOpen} onOpenChange={setIsSubmitDialogOpen}>
        <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-2xl">
          <RepositorySubmitForm onSuccess={handleSubmitSuccess} />
        </DialogContent>
      </Dialog>

      {/* 快速预览对话框 */}
      <Dialog open={!!selectedRepo} onOpenChange={() => setSelectedRepo(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t("admin.repositories.repoDetail")}</DialogTitle>
          </DialogHeader>
          {selectedRepo && (
            <div className="space-y-4">
              <div className="flex items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="truncate font-medium">
                    {selectedRepo.orgName}/{selectedRepo.repoName}
                  </p>
                  <p className="mt-0.5 truncate text-xs text-muted-foreground">
                    {selectedRepo.sourceLocation || selectedRepo.gitUrl}
                  </p>
                </div>
                <RepoStatusBadge status={selectedRepo.status} />
              </div>
              <div className="grid grid-cols-2 gap-3 text-sm">
                <div className="rounded-md border p-3">
                  <p className="text-xs text-muted-foreground">
                    {t("admin.repositories.sourceType")}
                  </p>
                  <p className="mt-1 font-medium">
                    {t(
                      `admin.repositories.${getRepositorySourceTypeLabelKey(selectedRepo.sourceType, selectedRepo.sourceTypeName)}`
                    )}
                  </p>
                </div>
                <div className="rounded-md border p-3">
                  <p className="text-xs text-muted-foreground">
                    {t("admin.repositories.visibility")}
                  </p>
                  <p className="mt-1 font-medium">
                    {selectedRepo.isPublic
                      ? t("admin.repositories.public")
                      : t("admin.repositories.private")}
                  </p>
                </div>
              </div>
              <div className="grid grid-cols-4 gap-3">
                {(
                  [
                    ["star", selectedRepo.starCount],
                    ["fork", selectedRepo.forkCount],
                    ["bookmark", selectedRepo.bookmarkCount],
                    ["view", selectedRepo.viewCount],
                  ] as const
                ).map(([key, count]) => (
                  <div key={key} className="rounded-md border p-3 text-center">
                    <p className="text-lg font-semibold tabular-nums">
                      {count}
                    </p>
                    <p className="text-xs text-muted-foreground">
                      {t(`admin.repositories.${key}`)}
                    </p>
                  </div>
                ))}
              </div>
              <p className="text-xs text-muted-foreground">
                {t("admin.repositories.createdAt")}:{" "}
                {new Date(selectedRepo.createdAt).toLocaleString(dateLocale)}
              </p>
            </div>
          )}
          <DialogFooter>
            {selectedRepo && (
              <Button asChild variant="default">
                <Link href={`/admin/repositories/${selectedRepo.id}`}>
                  <ExternalLink className="mr-1.5 h-4 w-4" />
                  {t("admin.repositories.manageRepo")}
                </Link>
              </Button>
            )}
            <Button variant="outline" onClick={() => setSelectedRepo(null)}>
              {t("admin.common.close")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* 删除确认对话框 */}
      <AlertDialog open={!!deleteId} onOpenChange={() => setDeleteId(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              {t("admin.repositories.confirmDelete")}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {t("admin.repositories.deleteWarning")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("admin.common.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              onClick={handleDelete}
              className="bg-red-600 hover:bg-red-700"
            >
              {t("admin.common.delete")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      {/* 批量重新生成确认对话框 */}
      <AlertDialog
        open={showBatchRegenerateConfirm}
        onOpenChange={(open) => {
          if (!batchRegenerating) {
            setShowBatchRegenerateConfirm(open);
          }
        }}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              {t("admin.repositories.confirmBatchRegenerate")}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {t("admin.repositories.batchRegenerateWarning", {
                count: selectedIds.size,
              })}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={batchRegenerating}>
              {t("admin.common.cancel")}
            </AlertDialogCancel>
            <AlertDialogAction
              onClick={(event) => {
                event.preventDefault();
                void handleBatchRegenerate();
              }}
              className="bg-amber-600 hover:bg-amber-700"
              disabled={batchRegenerating}
            >
              {batchRegenerating ? (
                <>
                  <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  {t("admin.repositories.batchRegenerating")}
                </>
              ) : (
                t("admin.repositories.batchRegenerate")
              )}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      {/* 批量删除确认对话框 */}
      <AlertDialog
        open={showBatchDeleteConfirm}
        onOpenChange={setShowBatchDeleteConfirm}
      >
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>
              {t("admin.repositories.confirmBatchDelete")}
            </AlertDialogTitle>
            <AlertDialogDescription>
              {t("admin.repositories.batchDeleteWarning", {
                count: selectedIds.size,
              })}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={batchDeleting}>
              {t("admin.common.cancel")}
            </AlertDialogCancel>
            <AlertDialogAction
              onClick={handleBatchDelete}
              className="bg-red-600 hover:bg-red-700"
              disabled={batchDeleting}
            >
              {batchDeleting ? (
                <>
                  <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  {t("admin.repositories.deleting")}
                </>
              ) : (
                t("admin.common.confirm")
              )}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}
