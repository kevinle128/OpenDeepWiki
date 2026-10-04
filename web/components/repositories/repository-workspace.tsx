"use client";

import * as React from "react";

import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useTranslations } from "@/hooks/use-translations";
import { useNarrowLayout } from "@/hooks/use-narrow-layout";
import { findConnectedRepository, isAbortError, listConnections } from "@/lib/git-connections-api";
import type { ConnectedRepositoryRef, GitConnection, RemoteRepository } from "@/types/git-connection";
import { ConnectionList } from "./connection-list";
import { IndexPlanPanel } from "./index-plan-panel";
import { ManagedBranchesPanel } from "./managed-branches-panel";
import { RepositoryCatalog, type CatalogFilters } from "./repository-catalog";

/** Non-secret selection and filters. The page keeps them in the URL. */
export interface WorkspaceState {
  connectionId: string | null;
  /** Provider repository ID. */
  repositoryId: string | null;
  filters: CatalogFilters;
}

interface RepositoryWorkspaceProps {
  state: WorkspaceState;
  onStateChange: (state: WorkspaceState) => void;
}

type Step = "connections" | "repositories" | "plan";
type LinkState =
  | { status: "idle" | "loading" | "none" | "error" }
  | { status: "linked"; repository: ConnectedRepositoryRef };

export function RepositoryWorkspace({ state, onStateChange }: RepositoryWorkspaceProps) {
  const t = useTranslations();
  const narrow = useNarrowLayout();
  const [connections, setConnections] = React.useState<GitConnection[]>([]);
  const [listStatus, setListStatus] = React.useState<"loading" | "ready" | "error">("loading");
  const [reloads, setReloads] = React.useState(0);
  // A URL that names a repository or a connection opens the matching step on a narrow screen.
  const [step, setStep] = React.useState<Step>(() =>
    state.repositoryId ? "plan" : state.connectionId ? "repositories" : "connections",
  );
  const [selectedRepository, setRepository] = React.useState<RemoteRepository | null>(null);
  const [linkResult, setLinkResult] = React.useState<{ key: string; value: LinkState } | null>(null);

  const connection = connections.find((item) => item.id === state.connectionId) ?? null;
  // A repository counts only while the selection still names it.
  const repository =
    selectedRepository && selectedRepository.providerRepositoryId === state.repositoryId ? selectedRepository : null;
  const linkKey = connection && repository ? `${connection.id}:${repository.providerRepositoryId}` : null;
  const link: LinkState = !linkKey ? { status: "idle" } : linkResult?.key === linkKey ? linkResult.value : { status: "loading" };

  React.useEffect(() => {
    const controller = new AbortController();
    listConnections(controller.signal)
      .then((list) => {
        if (controller.signal.aborted) return;
        setConnections(list);
        setListStatus("ready");
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted || isAbortError(error)) return;
        setListStatus("error");
      });
    return () => controller.abort();
  }, [reloads]);

  // A connection in the URL that the backend does not list any more must not stay selected.
  React.useEffect(() => {
    if (listStatus === "ready" && state.connectionId && !connection) {
      onStateChange({ ...state, connectionId: null, repositoryId: null });
    }
  }, [listStatus, state, connection, onStateChange]);

  React.useEffect(() => {
    if (!linkKey || !connection || !repository) return;
    const controller = new AbortController();
    findConnectedRepository(connection.id, repository.cloneUrl, repository.name, controller.signal)
      .then((found) => {
        if (controller.signal.aborted) return;
        setLinkResult({ key: linkKey, value: found ? { status: "linked", repository: found } : { status: "none" } });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted || isAbortError(error)) return;
        setLinkResult({ key: linkKey, value: { status: "error" } });
      });
    return () => controller.abort();
    // The lookup depends on the identity of the selection, not on every refreshed connection object.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [linkKey]);

  const selectConnection = React.useCallback(
    (id: string) => {
      if (id !== state.connectionId) onStateChange({ ...state, connectionId: id, repositoryId: null });
      setRepository(null);
      setStep("repositories");
    },
    [state, onStateChange],
  );

  const selectRepository = React.useCallback(
    (next: RemoteRepository) => {
      setRepository(next);
      onStateChange({ ...state, repositoryId: next.providerRepositoryId });
      setStep("plan");
    },
    [state, onStateChange],
  );

  const resolveRepository = React.useCallback((next: RemoteRepository) => setRepository(next), []);

  const connectionsPane = (
    <ConnectionList
      connections={connections}
      selectedId={state.connectionId}
      loading={listStatus === "loading"}
      failed={listStatus === "error"}
      onRetry={() => {
        setListStatus("loading");
        setReloads((count) => count + 1);
      }}
      onSelect={selectConnection}
      onSaved={(saved) => {
        setConnections((current) => [...current.filter((item) => item.id !== saved.id), saved]);
        selectConnection(saved.id);
      }}
      onUpdated={(updated) => setConnections((current) => current.map((item) => (item.id === updated.id ? updated : item)))}
      onRemoved={(id) => {
        setConnections((current) => current.filter((item) => item.id !== id));
        if (id === state.connectionId) onStateChange({ ...state, connectionId: null, repositoryId: null });
      }}
    />
  );

  const catalogPane = (
    <RepositoryCatalog
      connection={connection}
      selectedId={state.repositoryId}
      onSelect={selectRepository}
      onSelectedResolved={resolveRepository}
      filters={state.filters}
      onFiltersChange={(filters) => onStateChange({ ...state, filters })}
    />
  );

  let planPane: React.ReactNode;
  if (!connection || !repository) {
    planPane = (
      <section aria-labelledby="plan-empty-heading" className="p-4">
        <h2 id="plan-empty-heading" className="text-sm font-semibold">
          {t("repositories.plan.title")}
        </h2>
        <p className="text-muted-foreground mt-2 text-sm">{t("repositories.plan.noRepository")}</p>
      </section>
    );
  } else if (link.status === "loading" || link.status === "idle") {
    planPane = (
      <p role="status" className="text-muted-foreground p-4 text-sm">
        {t("repositories.plan.lookup")}
      </p>
    );
  } else if (link.status === "linked") {
    planPane = (
      <ManagedBranchesPanel
        connection={connection}
        repository={repository}
        repositoryId={link.repository.repositoryId}
      />
    );
  } else {
    planPane = (
      <>
        {link.status === "error" && (
          <p role="alert" className="border-b p-3 text-sm">
            {t("repositories.plan.lookupError")}
          </p>
        )}
        <IndexPlanPanel
          connection={connection}
          repository={repository}
          onManage={(result) =>
            linkKey &&
            setLinkResult({
              key: linkKey,
              value: {
                status: "linked",
                repository: { repositoryId: result.repositoryId, orgName: result.orgName, repoName: result.repoName },
              },
            })
          }
        />
      </>
    );
  }

  // One tree for every width. Crossing the breakpoint changes classes and which chrome is shown, but never the
  // element types or positions, so the panes keep their state and an open dialog stays open.
  const paneProps = (value: Step) =>
    narrow
      ? { value, forceMount: true as const, hidden: step !== value }
      : // Wide screens show every pane. The tab semantics belong to the narrow layout only.
        { value, forceMount: true as const, hidden: false, role: undefined, tabIndex: undefined, "aria-labelledby": undefined };

  return (
    <Tabs
      value={step}
      onValueChange={(value) => setStep(value as Step)}
      className={
        narrow
          ? "min-h-0 flex-1 gap-0"
          : "grid min-h-0 flex-1 grid-cols-[16rem_minmax(0,1fr)_22rem] gap-0 overflow-hidden border-t xl:grid-cols-[17rem_minmax(0,1fr)_23rem]"
      }
    >
      {narrow && (
        <div
          role="region"
          aria-label={t("repositories.summary.label")}
          className="bg-muted/30 flex flex-wrap items-center gap-x-3 gap-y-1 border-t px-4 py-2 text-xs"
        >
          <span>
            {connection
              ? `${connection.displayName} · ${t(`repositories.connections.state.${connection.state}`)}`
              : t("repositories.summary.noConnection")}
          </span>
          <span className="text-muted-foreground">{repository ? repository.fullName : t("repositories.summary.noRepository")}</span>
        </div>
      )}
      {narrow && (
        <TabsList aria-label={t("repositories.steps.label")} className="grid h-12 w-full grid-cols-3 rounded-none">
          <TabsTrigger value="connections">{t("repositories.steps.connections")}</TabsTrigger>
          <TabsTrigger value="repositories" disabled={!connection}>
            {t("repositories.steps.repositories")}
          </TabsTrigger>
          <TabsTrigger value="plan" disabled={!repository}>
            {t("repositories.steps.plan")}
          </TabsTrigger>
        </TabsList>
      )}
      <TabsContent
        {...paneProps("connections")}
        className={narrow ? "min-h-0 overflow-auto" : "bg-muted/30 min-h-0 overflow-auto border-r"}
      >
        {connectionsPane}
      </TabsContent>
      <TabsContent {...paneProps("repositories")} className={narrow ? "min-h-0 overflow-auto" : "min-h-0 min-w-0 overflow-auto"}>
        {catalogPane}
      </TabsContent>
      <TabsContent
        {...paneProps("plan")}
        className={narrow ? "min-h-0 overflow-auto" : "bg-muted/20 min-h-0 overflow-auto border-l"}
      >
        {planPane}
      </TabsContent>
    </Tabs>
  );
}
