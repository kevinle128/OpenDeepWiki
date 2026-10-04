"use client";

import * as React from "react";
import { AlertTriangle, CheckCircle2, CircleSlash, Loader2, Plus } from "lucide-react";

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
import { deleteConnection, setConnectionEnabled, testConnection } from "@/lib/git-connections-api";
import { cn } from "@/lib/utils";
import type { GitConnection } from "@/types/git-connection";
import { ConnectionDialog } from "./connection-dialog";
import { errorKey } from "./error-key";

interface ConnectionListProps {
  connections: GitConnection[];
  selectedId: string | null;
  loading?: boolean;
  /** True when the list request failed. */
  failed?: boolean;
  onRetry?: () => void;
  onSelect: (id: string) => void;
  onSaved: (connection: GitConnection, existing: boolean) => void;
  onUpdated: (connection: GitConnection) => void;
  onRemoved: (id: string) => void;
}

function StateIcon({ state }: { state: GitConnection["state"] }) {
  if (state === "Healthy") return <CheckCircle2 aria-hidden className="h-3.5 w-3.5 text-emerald-600" />;
  if (state === "Warning") return <AlertTriangle aria-hidden className="h-3.5 w-3.5 text-amber-600" />;
  return <CircleSlash aria-hidden className="text-muted-foreground h-3.5 w-3.5" />;
}

export function ConnectionList({
  connections,
  selectedId,
  loading,
  failed,
  onRetry,
  onSelect,
  onSaved,
  onUpdated,
  onRemoved,
}: ConnectionListProps) {
  const t = useTranslations();
  const selected = connections.find((item) => item.id === selectedId) ?? null;
  const [dialog, setDialog] = React.useState<"none" | "add" | "edit">("none");
  const [confirmDelete, setConfirmDelete] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const [status, setStatus] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const errorRef = React.useRef<HTMLDivElement>(null);
  const triggerRef = React.useRef<HTMLElement | null>(null);

  function open(kind: "add" | "edit" | "delete", event: React.MouseEvent<HTMLElement>) {
    triggerRef.current = event.currentTarget;
    if (kind === "delete") setConfirmDelete(true);
    else setDialog(kind);
  }

  React.useEffect(() => {
    if (error) errorRef.current?.focus();
  }, [error]);

  async function run(action: () => Promise<string>) {
    setBusy(true);
    setError(null);
    setStatus("");
    try {
      setStatus(await action());
    } catch (reason) {
      setError(t(`repositories.errors.${errorKey(reason)}`));
    } finally {
      setBusy(false);
    }
  }

  const handleTest = (connection: GitConnection) =>
    run(async () => {
      setStatus(t("repositories.connections.testing"));
      const health = await testConnection(connection.id);
      return health.ok
        ? t("repositories.connections.testOk", { ms: health.latencyMs })
        : t("repositories.connections.testFailed");
    });

  const handleToggle = (connection: GitConnection) =>
    run(async () => {
      const updated = await setConnectionEnabled(connection.id, !connection.isEnabled);
      onUpdated(updated);
      return t(updated.isEnabled ? "repositories.connections.enabled" : "repositories.connections.disabled");
    });

  const handleDelete = (connection: GitConnection) =>
    run(async () => {
      await deleteConnection(connection.id);
      onRemoved(connection.id);
      return t("repositories.connections.deleted");
    });

  return (
    <section aria-labelledby="connections-heading" className="flex min-h-0 flex-col">
      <div className="flex h-14 items-center justify-between gap-2 border-b px-4">
        <h2 id="connections-heading" className="text-xs font-semibold tracking-wider uppercase">
          {t("repositories.connections.title")}
        </h2>
        <Button type="button" size="sm" variant="outline" onClick={(event) => open("add", event)}>
          <Plus aria-hidden className="h-4 w-4" />
          {t("repositories.connections.add")}
        </Button>
      </div>

      <div className="flex-1 overflow-auto p-2">
        {loading && (
          <div role="status" className="flex flex-col gap-2 p-2">
            <span className="sr-only">{t("repositories.connections.loading")}</span>
            <Skeleton className="h-12 w-full" />
            <Skeleton className="h-12 w-full" />
          </div>
        )}

        {!loading && failed && (
          <div role="alert" className="flex flex-col items-start gap-2 p-3 text-sm">
            <p>{t("repositories.connections.loadError")}</p>
            {onRetry && (
              <Button type="button" size="sm" variant="outline" onClick={onRetry}>
                {t("repositories.connections.retry")}
              </Button>
            )}
          </div>
        )}

        {!loading && !failed && connections.length === 0 && (
          <p className="text-muted-foreground p-3 text-sm">{t("repositories.connections.empty")}</p>
        )}

        {connections.length > 0 && (
          <ul aria-label={t("repositories.connections.listLabel")} className="flex flex-col gap-1">
            {connections.map((connection) => {
              const isSelected = connection.id === selectedId;
              const selfHosted = connection.provider === "GitLab" && !/^https:\/\/(www\.)?gitlab\.com\/?$/i.test(connection.serverUrl);
              return (
                <li key={connection.id}>
                  <button
                    type="button"
                    aria-current={isSelected ? "true" : undefined}
                    onClick={() => onSelect(connection.id)}
                    className={cn(
                      "hover:bg-accent focus-visible:ring-ring grid w-full grid-cols-[2rem_1fr_auto] items-center gap-2 rounded-md border p-2.5 text-left outline-none focus-visible:ring-2",
                      isSelected ? "border-primary bg-accent" : "border-transparent",
                    )}
                  >
                    <span
                      aria-hidden
                      className="bg-foreground text-background grid h-8 w-8 place-items-center rounded-md text-[11px] font-bold"
                    >
                      {connection.provider === "GitHub" ? "GH" : "GL"}
                    </span>
                    <span className="min-w-0">
                      <strong className="block truncate text-sm">{connection.displayName}</strong>
                      <small className="text-muted-foreground block truncate text-xs">
                        {connection.provider}
                        {selfHosted ? ` · ${t("repositories.connections.selfHosted")}` : ""} ·{" "}
                        {t("repositories.connections.repositoryCount", { count: connection.repositoryCount })}
                      </small>
                    </span>
                    <span className="flex flex-col items-end gap-1 text-xs">
                      <span className="inline-flex items-center gap-1">
                        <StateIcon state={connection.state} />
                        {t(`repositories.connections.state.${connection.state}`)}
                      </span>
                      {isSelected && (
                        <span className="text-primary inline-flex items-center gap-1 font-medium">
                          <CheckCircle2 aria-hidden className="h-3 w-3" />
                          {t("repositories.connections.selectedMark")}
                        </span>
                      )}
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        )}

        {selected && (
          <div className="mt-3 flex flex-col gap-2 border-t p-2">
            {!selected.isEnabled && (
              <p className="text-muted-foreground text-xs">{t("repositories.connections.disabledNote")}</p>
            )}
            {selected.canMaintain ? (
              <div role="group" aria-label={t("repositories.connections.actionsLabel")} className="flex flex-wrap gap-2">
                <Button type="button" size="sm" variant="outline" disabled={busy} onClick={(event) => open("edit", event)}>
                  {t("repositories.connections.edit")}
                </Button>
                <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => handleTest(selected)}>
                  {t("repositories.connections.test")}
                </Button>
                <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => handleToggle(selected)}>
                  {t(selected.isEnabled ? "repositories.connections.disable" : "repositories.connections.enable")}
                </Button>
                <Button type="button" size="sm" variant="outline" disabled={busy} onClick={(event) => open("delete", event)}>
                  {t("repositories.connections.delete")}
                </Button>
              </div>
            ) : (
              <p className="text-muted-foreground text-xs">{t("repositories.connections.maintainerOnly")}</p>
            )}
          </div>
        )}

        <div role="status" aria-live="polite" className="text-muted-foreground flex items-center gap-2 p-2 text-xs">
          {busy && <Loader2 aria-hidden className="h-3 w-3 animate-spin" />}
          <span>{status}</span>
        </div>
        {error && (
          <div
            ref={errorRef}
            role="alert"
            tabIndex={-1}
            className="border-destructive/40 bg-destructive/10 text-destructive m-2 rounded-md border p-3 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            {error}
          </div>
        )}
      </div>

      <ConnectionDialog
        open={dialog !== "none"}
        onOpenChange={(open) => !open && setDialog("none")}
        connection={dialog === "edit" ? selected : null}
        returnFocusRef={triggerRef}
        onSaved={(connection, existing) => {
          setStatus(t(existing ? "repositories.connections.existing" : "repositories.connections.saved"));
          onSaved(connection, existing);
        }}
      />

      <AlertDialog open={confirmDelete} onOpenChange={setConfirmDelete}>
        <AlertDialogContent
          onCloseAutoFocus={(event) => {
            event.preventDefault();
            triggerRef.current?.focus();
          }}
        >
          <AlertDialogHeader>
            <AlertDialogTitle>{t("repositories.connections.deleteTitle")}</AlertDialogTitle>
            <AlertDialogDescription>{t("repositories.connections.deleteBody")}</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("repositories.connections.keep")}</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                if (selected) void handleDelete(selected);
              }}
            >
              {t("repositories.connections.deleteConfirm")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  );
}
