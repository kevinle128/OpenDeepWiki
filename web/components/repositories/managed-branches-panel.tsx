"use client";

import * as React from "react";
import { Loader2, Plus } from "lucide-react";

import { GenerationStatusBadge } from "@/components/repo/branch-generation-status";
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
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { useTranslations } from "@/hooks/use-translations";
import {
  isAbortError,
  listIndexedBranches,
  removeIndexedBranch,
  triggerBranchSync,
} from "@/lib/git-connections-api";
import {
  cancelBranchGenerationTask,
  enqueueBranchFullGeneration,
  retryBranchGenerationTask,
} from "@/lib/repository-api";
import type { GitConnection, IndexedBranchSummary, RemoteRepository } from "@/types/git-connection";
import { errorKey } from "./error-key";
import { IndexPlanPanel } from "./index-plan-panel";

interface ManagedBranchesPanelProps {
  connection: GitConnection;
  repository: RemoteRepository;
  repositoryId: string;
}

const POLL_INTERVAL_MS = 5000;

function isActive(branch: IndexedBranchSummary): boolean {
  return Boolean(branch.activeTaskId) || branch.generationStatus === "Pending" || branch.generationStatus === "Processing";
}

/** Another repository starts with an empty list, no message and no open dialog. */
export function ManagedBranchesPanel(props: ManagedBranchesPanelProps) {
  return <ManagedBranches key={props.repositoryId} {...props} />;
}

function ManagedBranches({ connection, repository, repositoryId }: ManagedBranchesPanelProps) {
  const t = useTranslations();
  const [branches, setBranches] = React.useState<IndexedBranchSummary[]>([]);
  const [status, setStatus] = React.useState<"loading" | "ready" | "error">("loading");
  const [busyId, setBusyId] = React.useState<string | null>(null);
  const [message, setMessage] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [removeTarget, setRemoveTarget] = React.useState<IndexedBranchSummary | null>(null);
  const [adding, setAdding] = React.useState(false);
  const errorRef = React.useRef<HTMLDivElement>(null);
  const triggerRef = React.useRef<HTMLElement | null>(null);
  const addRef = React.useRef<HTMLButtonElement>(null);
  const dateFormat = React.useMemo(() => new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }), []);
  const usable = connection.isEnabled;

  const applyList = React.useCallback((list: IndexedBranchSummary[]) => {
    setBranches(list);
    setStatus("ready");
  }, []);
  const applyFailure = React.useCallback(() => setStatus((current) => (current === "ready" ? current : "error")), []);

  const refresh = React.useCallback(
    (signal?: AbortSignal) =>
      listIndexedBranches(repositoryId, signal)
        .then((list) => {
          if (!signal?.aborted) applyList(list);
        })
        .catch((reason: unknown) => {
          if (!signal?.aborted && !isAbortError(reason)) applyFailure();
        }),
    [repositoryId, applyList, applyFailure],
  );

  React.useEffect(() => {
    const controller = new AbortController();
    listIndexedBranches(repositoryId, controller.signal)
      .then((list) => {
        if (!controller.signal.aborted) applyList(list);
      })
      .catch((reason: unknown) => {
        if (!controller.signal.aborted && !isAbortError(reason)) applyFailure();
      });
    return () => controller.abort();
  }, [repositoryId, applyList, applyFailure]);

  const anyActive = branches.some(isActive);
  React.useEffect(() => {
    if (!anyActive) return;
    const controller = new AbortController();
    const timer = setInterval(() => void refresh(controller.signal), POLL_INTERVAL_MS);
    return () => {
      clearInterval(timer);
      controller.abort();
    };
  }, [anyActive, refresh]);

  React.useEffect(() => {
    if (error) errorRef.current?.focus();
  }, [error]);

  async function run(branch: IndexedBranchSummary, action: () => Promise<string>) {
    setBusyId(branch.branchId);
    setError(null);
    try {
      setMessage(await action());
      await refresh();
    } catch (reason) {
      setError(t(`repositories.errors.${errorKey(reason)}`));
    } finally {
      setBusyId(null);
    }
  }

  /** Task routes answer with a success flag instead of a failure status. */
  function assertTask(response: { success: boolean }) {
    if (!response.success) throw new Error("task-rejected");
  }

  const sync = (branch: IndexedBranchSummary) =>
    run(branch, async () => {
      assertTask(await triggerBranchSync(repositoryId, branch.branchId));
      return t("repositories.managed.syncStarted", { branch: branch.branchName });
    });

  const rebuild = (branch: IndexedBranchSummary) =>
    run(branch, async () => {
      assertTask(await enqueueBranchFullGeneration(repositoryId, branch.branchId));
      return t("repositories.managed.rebuildStarted", { branch: branch.branchName });
    });

  const retry = (branch: IndexedBranchSummary) =>
    run(branch, async () => {
      assertTask(await retryBranchGenerationTask(branch.lastGenerationTaskId ?? ""));
      return t("repositories.managed.retryStarted", { branch: branch.branchName });
    });

  const cancel = (branch: IndexedBranchSummary) =>
    run(branch, async () => {
      assertTask(await cancelBranchGenerationTask(branch.activeTaskId ?? ""));
      return t("repositories.managed.cancelled", { branch: branch.branchName });
    });

  const remove = (branch: IndexedBranchSummary) =>
    run(branch, async () => {
      await removeIndexedBranch(repositoryId, branch.branchId);
      setBranches((current) => current.filter((item) => item.branchId !== branch.branchId));
      return t("repositories.managed.removed", { branch: branch.branchName });
    });

  const finished = branches.filter((branch) => !isActive(branch)).length;

  return (
    <section aria-labelledby="managed-heading" className="flex min-h-0 flex-col">
      <div className="border-b px-4 py-3">
        <p className="text-primary text-[10px] font-bold tracking-widest uppercase">{t("repositories.plan.managedTitle")}</p>
        <h2 id="managed-heading" className="mt-1 font-mono text-sm font-bold break-all">
          {repository.fullName}
        </h2>
      </div>

      <div className="flex flex-col gap-4 overflow-auto p-4">
        {!usable && <p className="text-sm">{t("repositories.managed.disabledReason")}</p>}

        {error && (
          <div
            ref={errorRef}
            role="alert"
            tabIndex={-1}
            className="border-destructive/40 bg-destructive/10 text-destructive rounded-md border p-3 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {error}
          </div>
        )}

        <div role="status" aria-live="polite" className="text-muted-foreground flex flex-col gap-1 text-xs">
          {message && <span>{message}</span>}
          {status === "ready" && branches.length > 0 && (
            <span>{t("repositories.managed.progress", { done: finished, total: branches.length })}</span>
          )}
        </div>

        {status === "loading" && (
          <div role="status" className="flex flex-col gap-2">
            <span className="sr-only">{t("repositories.managed.loading")}</span>
            <Skeleton className="h-16 w-full" />
            <Skeleton className="h-16 w-full" />
          </div>
        )}

        {status === "error" && (
          <div role="alert" className="flex flex-col items-start gap-2 text-sm">
            <p>{t("repositories.managed.error")}</p>
            <Button type="button" size="sm" variant="outline" onClick={() => void refresh()}>
              {t("repositories.managed.retry")}
            </Button>
          </div>
        )}

        {status === "ready" && branches.length === 0 && (
          <p className="text-muted-foreground text-sm">{t("repositories.managed.empty")}</p>
        )}

        {branches.length > 0 && (
          <ul aria-label={t("repositories.managed.listLabel")} className="divide-y rounded-md border">
            {branches.map((branch) => {
              const busy = busyId === branch.branchId;
              const active = Boolean(branch.activeTaskId);
              return (
                <li key={branch.branchId} className="flex flex-col gap-2 p-3">
                  <div className="flex items-center justify-between gap-2">
                    <code className="truncate text-sm font-semibold">{branch.branchName}</code>
                    <GenerationStatusBadge status={branch.generationStatus} />
                  </div>
                  <div className="text-muted-foreground flex flex-wrap gap-x-3 text-xs">
                    {branch.languages.length > 0 && (
                      <span>{t("repositories.managed.languages", { languages: branch.languages.join(", ") })}</span>
                    )}
                    {branch.lastProcessedAt && (
                      <span>
                        {t("repositories.managed.lastProcessed", { date: dateFormat.format(new Date(branch.lastProcessedAt)) })}
                      </span>
                    )}
                    {active && <span>{t("repositories.managed.activeTask", { kind: branch.activeTaskKind ?? "-" })}</span>}
                  </div>
                  {branch.generationStatus === "Failed" && branch.lastGenerationError && (
                    <p className="text-destructive line-clamp-2 text-xs">{branch.lastGenerationError}</p>
                  )}
                  <div
                    role="group"
                    aria-label={t("repositories.managed.actionsFor", { branch: branch.branchName })}
                    className="flex flex-wrap gap-2"
                  >
                    <Button type="button" size="sm" variant="outline" disabled={!usable || busy || active} onClick={() => void sync(branch)}>
                      {busy && <Loader2 aria-hidden className="mr-1 h-3 w-3 animate-spin" />}
                      {t("repositories.managed.sync")}
                    </Button>
                    <Button type="button" size="sm" variant="outline" disabled={!usable || busy || active} onClick={() => void rebuild(branch)}>
                      {t("repositories.managed.rebuild")}
                    </Button>
                    {branch.generationStatus === "Failed" && branch.lastGenerationTaskId && !active && (
                      <Button type="button" size="sm" variant="outline" disabled={!usable || busy} onClick={() => void retry(branch)}>
                        {t("repositories.managed.retryTask")}
                      </Button>
                    )}
                    {/* The cancel route accepts a pending full generation task only. A sync task has no cancel route,
                        and a task that already runs cannot be cancelled. */}
                    {active && branch.activeTaskKind === "Full" && branch.generationStatus === "Pending" && (
                      <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => void cancel(branch)}>
                        {t("repositories.managed.cancel")}
                      </Button>
                    )}
                    <Button
                      type="button"
                      size="sm"
                      variant="outline"
                      disabled={busy}
                      onClick={(event) => {
                        triggerRef.current = event.currentTarget;
                        setRemoveTarget(branch);
                      }}
                    >
                      {t("repositories.managed.remove")}
                    </Button>
                  </div>
                </li>
              );
            })}
          </ul>
        )}

        {status === "ready" && usable && (
          <>
            <Button
              ref={addRef}
              type="button"
              variant="outline"
              aria-expanded={adding}
              onClick={() => setAdding((value) => !value)}
            >
              <Plus aria-hidden className="h-4 w-4" />
              {t(adding ? "repositories.managed.closeAdd" : "repositories.managed.add")}
            </Button>
            {adding && (
              <div className="rounded-md border">
                <IndexPlanPanel
                  connection={connection}
                  repository={repository}
                  repositoryId={repositoryId}
                  excludedBranches={branches.map((branch) => branch.branchName)}
                  onManage={() => {
                    setAdding(false);
                    void refresh();
                    addRef.current?.focus();
                  }}
                />
              </div>
            )}
          </>
        )}
      </div>

      <AlertDialog open={removeTarget !== null} onOpenChange={(open) => !open && setRemoveTarget(null)}>
        <AlertDialogContent
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            triggerRef.current?.focus();
          }}
        >
          <AlertDialogHeader>
            <AlertDialogTitle>{t("repositories.managed.removeTitle")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("repositories.managed.removeBody", { branch: removeTarget?.branchName ?? "" })}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("repositories.managed.removeKeep")}</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                if (removeTarget) void remove(removeTarget);
              }}
            >
              {t("repositories.managed.removeConfirm")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  );
}
