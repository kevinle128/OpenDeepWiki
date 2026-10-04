"use client";

import * as React from "react";
import Link from "next/link";
import { CheckCircle2, Loader2, Lock, Globe } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useTranslations } from "@/hooks/use-translations";
import { isAbortError, listProviderRepositories } from "@/lib/git-connections-api";
import { cn } from "@/lib/utils";
import type { GitConnection, RemoteRepository } from "@/types/git-connection";
import { errorKey } from "./error-key";

export interface CatalogFilters {
  q: string;
  visibility: "all" | "public" | "private";
  sort: "updated" | "updatedAsc" | "name";
}

interface RepositoryCatalogProps {
  connection: GitConnection | null;
  /** Provider repository ID of the selected repository. */
  selectedId: string | null;
  onSelect: (repository: RemoteRepository) => void;
  /** Called when the loaded list contains the repository that the selection already names, for example after a reload. */
  onSelectedResolved?: (repository: RemoteRepository) => void;
  filters: CatalogFilters;
  onFiltersChange: (filters: CatalogFilters) => void;
}

const PAGE_SIZE = 50;

const selectClass =
  "border-input bg-background h-9 w-full rounded-md border px-2 text-sm shadow-xs outline-none focus-visible:ring-2 focus-visible:ring-ring";

/** A new connection, or a change of its enabled state, remounts the view so no list or error of the old one stays. */
export function RepositoryCatalog(props: RepositoryCatalogProps) {
  return <CatalogView key={`${props.connection?.id ?? ""}:${props.connection?.isEnabled ?? ""}`} {...props} />;
}

function CatalogView({
  connection,
  selectedId,
  onSelect,
  onSelectedResolved,
  filters,
  onFiltersChange,
}: RepositoryCatalogProps) {
  const t = useTranslations();
  const connectionId = connection?.id ?? null;
  const usable = Boolean(connection?.isEnabled);
  const { q, visibility, sort } = filters;
  const [items, setItems] = React.useState<RemoteRepository[]>([]);
  const [nextCursor, setNextCursor] = React.useState<string | null>(null);
  const [requestStatus, setStatus] = React.useState<"idle" | "loading" | "ready" | "error">(
    connectionId && usable ? "loading" : "idle",
  );
  const [loadingMore, setLoadingMore] = React.useState(false);
  const [failure, setFailure] = React.useState("generic");
  const [reloads, setReloads] = React.useState(0);
  const requestKey = JSON.stringify([q, visibility, sort, reloads]);
  const [loadedKey, setLoadedKey] = React.useState<string | null>(null);
  const status = usable && loadedKey !== requestKey ? "loading" : requestStatus;
  // Every reset starts a new epoch, so an answer that belongs to an older epoch can never replace the list.
  const epochRef = React.useRef(0);
  const moreControllerRef = React.useRef<AbortController | null>(null);

  React.useEffect(() => {
    if (!connectionId || !usable) return;
    const epoch = ++epochRef.current;
    const controller = new AbortController();
    moreControllerRef.current?.abort();
    const timer = setTimeout(() => {
      setItems([]);
      setNextCursor(null);
      setLoadingMore(false);
      setStatus("loading");
      listProviderRepositories(connectionId, { pageSize: PAGE_SIZE, q, visibility, sort, signal: controller.signal })
      .then((page) => {
        if (controller.signal.aborted || epoch !== epochRef.current) return;
        setItems(page.items);
        setNextCursor(page.nextCursor ?? null);
        setLoadedKey(requestKey);
        setStatus("ready");
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted || epoch !== epochRef.current || isAbortError(error)) return;
        setFailure(errorKey(error));
        setLoadedKey(requestKey);
        setStatus("error");
      });
    }, q ? 250 : 0);

    return () => {
      clearTimeout(timer);
      controller.abort();
      moreControllerRef.current?.abort();
    };
  }, [connectionId, usable, reloads, q, visibility, sort, requestKey]);

  function retry() {
    setStatus("loading");
    setReloads((count) => count + 1);
  }

  async function loadMore() {
    if (!connectionId || !nextCursor || loadingMore || status !== "ready") return;
    const epoch = epochRef.current;
    const controller = new AbortController();
    moreControllerRef.current = controller;
    setLoadingMore(true);
    try {
      const page = await listProviderRepositories(connectionId, {
        cursor: nextCursor,
        pageSize: PAGE_SIZE,
        q,
        visibility,
        sort,
        signal: controller.signal,
      });
      if (controller.signal.aborted || epoch !== epochRef.current) return;
      setItems((current) => [...current, ...page.items]);
      setNextCursor(page.nextCursor ?? null);
    } catch (error) {
      if (controller.signal.aborted || epoch !== epochRef.current || isAbortError(error)) return;
      setFailure(errorKey(error));
      setStatus("error");
    } finally {
      if (epoch === epochRef.current) setLoadingMore(false);
    }
  }

  React.useEffect(() => {
    if (!selectedId || !onSelectedResolved) return;
    const match = items.find((item) => item.providerRepositoryId === selectedId);
    if (match) onSelectedResolved(match);
  }, [items, selectedId, onSelectedResolved]);

  const rows = status === "ready" ? items : [];
  const dateFormat = React.useMemo(() => new Intl.DateTimeFormat(undefined, { dateStyle: "medium" }), []);

  return (
    <section aria-labelledby="catalog-heading" className="flex min-h-0 flex-col">
      <div className="border-b px-4 py-3">
        <div className="flex items-end gap-2">
          <h2 id="catalog-heading" className="text-lg font-semibold">
            {t("repositories.catalog.title")}
          </h2>
          {status === "ready" && (
            <span className="text-muted-foreground mb-0.5 text-xs">
              {t("repositories.catalog.loaded", { count: items.length })}
            </span>
          )}
        </div>

        <div className="mt-3 grid grid-cols-1 gap-2 sm:grid-cols-2 xl:grid-cols-[minmax(10rem,1fr)_8rem_9rem]">
          <div className="flex flex-col gap-1 sm:col-span-2 xl:col-span-1">
            <label htmlFor="catalog-search" className="text-xs font-medium">
              {t("repositories.catalog.searchLabel")}
            </label>
            <Input
              id="catalog-search"
              type="search"
              value={filters.q}
              placeholder={t("repositories.catalog.searchPlaceholder")}
              autoComplete="off"
              onChange={(event) => onFiltersChange({ ...filters, q: event.target.value })}
            />
          </div>
          <div className="flex flex-col gap-1">
            <label htmlFor="catalog-visibility" className="text-xs font-medium">
              {t("repositories.catalog.visibilityLabel")}
            </label>
            <select
              id="catalog-visibility"
              className={selectClass}
              value={filters.visibility}
              onChange={(event) =>
                onFiltersChange({ ...filters, visibility: event.target.value as CatalogFilters["visibility"] })
              }
            >
              <option value="all">{t("repositories.catalog.visibilityAll")}</option>
              <option value="public">{t("repositories.catalog.visibilityPublic")}</option>
              <option value="private">{t("repositories.catalog.visibilityPrivate")}</option>
            </select>
          </div>
          <div className="flex flex-col gap-1">
            <label htmlFor="catalog-sort" className="text-xs font-medium">
              {t("repositories.catalog.sortLabel")}
            </label>
            <select
              id="catalog-sort"
              className={selectClass}
              value={filters.sort}
              onChange={(event) => onFiltersChange({ ...filters, sort: event.target.value as CatalogFilters["sort"] })}
            >
              <option value="updated">{t("repositories.catalog.sortUpdated")}</option>
              <option value="updatedAsc">{t("repositories.catalog.sortUpdatedAsc")}</option>
              <option value="name">{t("repositories.catalog.sortName")}</option>
            </select>
          </div>
        </div>
        <p className="text-muted-foreground mt-2 text-xs">{t("repositories.catalog.scopeNote")}</p>
      </div>

      <div className="flex-1 overflow-auto">
        {!connection && <p className="text-muted-foreground p-4 text-sm">{t("repositories.catalog.noConnection")}</p>}

        {connection && !connection.isEnabled && (
          <div className="flex flex-col items-start gap-2 p-4 text-sm">
            <p>{t("repositories.catalog.disabled")}</p>
            <Link href="/private" className="text-primary underline underline-offset-4">
              {t("repositories.catalog.viewDocs")}
            </Link>
          </div>
        )}

        {status === "loading" && (
          <div role="status" className="flex flex-col gap-2 p-4">
            <span className="sr-only">{t("repositories.catalog.loading")}</span>
            <Skeleton className="h-12 w-full" />
            <Skeleton className="h-12 w-full" />
            <Skeleton className="h-12 w-full" />
          </div>
        )}

        {status === "error" && (
          <div role="alert" className="flex flex-col items-start gap-2 p-4 text-sm">
            <p>{t("repositories.catalog.error")}</p>
            <p className="text-muted-foreground">{t(`repositories.errors.${failure}`)}</p>
            <Button type="button" size="sm" variant="outline" onClick={retry}>
              {t("repositories.catalog.retry")}
            </Button>
          </div>
        )}

        {status === "ready" && rows.length === 0 && (
          <p className="text-muted-foreground p-4 text-sm">
            {t(q.trim() || visibility !== "all" ? "repositories.catalog.emptyFiltered" : "repositories.catalog.empty")}
          </p>
        )}

        {rows.length > 0 && (
          <ul aria-label={t("repositories.catalog.listLabel")} className="divide-y">
            {rows.map((repository) => {
              const isSelected = repository.providerRepositoryId === selectedId;
              const isPublic = repository.visibility.toLowerCase() === "public";
              return (
                <li key={repository.providerRepositoryId}>
                  <button
                    type="button"
                    aria-current={isSelected ? "true" : undefined}
                    onClick={() => onSelect(repository)}
                    className={cn(
                      "hover:bg-accent focus-visible:ring-ring grid w-full grid-cols-[1fr_auto] items-center gap-3 border-l-[3px] px-4 py-3 text-left outline-none focus-visible:ring-2 focus-visible:ring-inset",
                      isSelected ? "border-primary bg-accent" : "border-transparent",
                    )}
                  >
                    <span className="min-w-0">
                      <strong className="block truncate text-sm">{repository.name}</strong>
                      <small className="text-muted-foreground block truncate font-mono text-xs">{repository.fullName}</small>
                      {repository.description && (
                        <small className="text-muted-foreground block truncate text-xs">{repository.description}</small>
                      )}
                    </span>
                    <span className="flex flex-col items-end gap-1 text-xs">
                      <span className="inline-flex items-center gap-1">
                        {isPublic ? <Globe aria-hidden className="h-3 w-3" /> : <Lock aria-hidden className="h-3 w-3" />}
                        {isPublic ? t("repositories.catalog.visibilityPublic") : t("repositories.catalog.visibilityPrivate")}
                      </span>
                      {repository.updatedAt && (
                        <span className="text-muted-foreground whitespace-nowrap">
                          {t("repositories.catalog.updated", { date: dateFormat.format(new Date(repository.updatedAt)) })}
                        </span>
                      )}
                      {isSelected && (
                        <span className="text-primary inline-flex items-center gap-1 font-medium">
                          <CheckCircle2 aria-hidden className="h-3 w-3" />
                          {t("repositories.catalog.selectedMark")}
                        </span>
                      )}
                    </span>
                  </button>
                </li>
              );
            })}
          </ul>
        )}

        {status === "ready" && nextCursor && (
          <div className="p-4">
            <Button type="button" variant="outline" disabled={loadingMore} onClick={loadMore}>
              {loadingMore && <Loader2 aria-hidden className="mr-2 h-4 w-4 animate-spin" />}
              {t(loadingMore ? "repositories.catalog.loadingMore" : "repositories.catalog.loadMore")}
            </Button>
          </div>
        )}
      </div>
    </section>
  );
}
