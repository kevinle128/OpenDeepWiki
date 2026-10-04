"use client";

import * as React from "react";
import { CheckCircle2, CircleDashed, Loader2 } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Skeleton } from "@/components/ui/skeleton";
import { useTranslations } from "@/hooks/use-translations";
import { defaultWikiLanguage } from "@/i18n/config";
import {
  addIndexedBranches,
  connectRepository,
  getErrorBranches,
  isAbortError,
  listProviderBranches,
} from "@/lib/git-connections-api";
import type {
  ConnectedRepositoryResult,
  GitConnection,
  RemoteBranch,
  RemoteRepository,
} from "@/types/git-connection";
import { errorKey } from "./error-key";
import { getLanguageOptions } from "./language-options";

interface IndexPlanPanelProps {
  connection: GitConnection;
  repository: RemoteRepository;
  /** Set when the repository is already indexed. The panel then adds branches instead of connecting. */
  repositoryId?: string;
  /** Branch names that are already indexed. They are not offered again. */
  excludedBranches?: string[];
  onManage: (result: ConnectedRepositoryResult) => void;
}

const BRANCH_PAGE_SIZE = 100;

/** A new repository starts a new plan, so nothing of the previous plan stays selected or visible. */
export function IndexPlanPanel(props: IndexPlanPanelProps) {
  const { connection, repository, repositoryId } = props;
  const key = `${connection.id}:${connection.isEnabled}:${repository.providerRepositoryId}:${repositoryId ?? ""}`;
  return <IndexPlan key={key} {...props} />;
}

function IndexPlan({ connection, repository, repositoryId, excludedBranches, onManage }: IndexPlanPanelProps) {
  const t = useTranslations();
  const languages = React.useMemo(() => getLanguageOptions(), []);
  const connectionId = connection.id;
  const providerRepositoryId = repository.providerRepositoryId;
  const usable = connection.isEnabled;
  const [branches, setBranches] = React.useState<RemoteBranch[]>([]);
  const [nextCursor, setNextCursor] = React.useState<string | null>(null);
  const [loadStatus, setLoadStatus] = React.useState<"idle" | "loading" | "ready" | "error">(usable ? "loading" : "idle");
  const [loadingMore, setLoadingMore] = React.useState(false);
  // Null until the user changes the selection. Then the default branch is the selection.
  const [chosen, setChosen] = React.useState<Set<string> | null>(null);
  const [language, setLanguage] = React.useState<string>(defaultWikiLanguage);
  const [generateSkill, setGenerateSkill] = React.useState(true);
  const [submitting, setSubmitting] = React.useState(false);
  const [result, setResult] = React.useState<ConnectedRepositoryResult | null>(null);
  const [failure, setFailure] = React.useState<{ message: string; branches: string[] } | null>(null);
  const alertRef = React.useRef<HTMLDivElement>(null);
  const moreControllerRef = React.useRef<AbortController | null>(null);
  const excludedKey = (excludedBranches ?? []).join("\n");
  const excluded = React.useMemo(() => new Set(excludedKey ? excludedKey.split("\n") : []), [excludedKey]);

  React.useEffect(() => {
    if (!usable) return;
    const controller = new AbortController();
    listProviderBranches(connectionId, providerRepositoryId, { pageSize: BRANCH_PAGE_SIZE, signal: controller.signal })
      .then((page) => {
        if (controller.signal.aborted) return;
        setBranches(page.items);
        setNextCursor(page.nextCursor ?? null);
        setLoadStatus("ready");
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted || isAbortError(error)) return;
        setFailure({ message: t(`repositories.errors.${errorKey(error)}`), branches: [] });
        setLoadStatus("error");
      });
    return () => controller.abort();
    // `t` changes with the locale only. The plan must not reload for it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectionId, providerRepositoryId, usable]);

  // A page that arrives after the panel is gone must not set state.
  React.useEffect(() => () => moreControllerRef.current?.abort(), []);

  React.useEffect(() => {
    if (failure) alertRef.current?.focus();
  }, [failure]);

  async function loadMore() {
    if (!nextCursor || loadingMore) return;
    const controller = new AbortController();
    moreControllerRef.current = controller;
    setLoadingMore(true);
    try {
      const page = await listProviderBranches(connectionId, providerRepositoryId, {
        cursor: nextCursor,
        pageSize: BRANCH_PAGE_SIZE,
        signal: controller.signal,
      });
      if (controller.signal.aborted) return;
      setBranches((current) => [...current, ...page.items]);
      setNextCursor(page.nextCursor ?? null);
    } catch (error) {
      if (controller.signal.aborted || isAbortError(error)) return;
      setFailure({ message: t(`repositories.errors.${errorKey(error)}`), branches: [] });
    } finally {
      if (!controller.signal.aborted) setLoadingMore(false);
    }
  }

  const visibleBranches = branches.filter((branch) => !excluded.has(branch.name));
  const defaultBranch = visibleBranches.find((branch) => branch.isDefault);
  const selected = chosen ?? new Set(defaultBranch ? [defaultBranch.name] : []);

  function toggle(name: string, checked: boolean) {
    setChosen(() => {
      const next = new Set(selected);
      if (checked) next.add(name);
      else next.delete(name);
      return next;
    });
  }

  async function submit() {
    if (submitting || selected.size === 0) return;
    const names = branches.filter((branch) => selected.has(branch.name)).map((branch) => branch.name);
    setSubmitting(true);
    setFailure(null);
    setResult(null);
    try {
      const response = repositoryId
        ? await addIndexedBranches(repositoryId, { branches: names, languageCode: language })
        : await connectRepository({
            connectionId,
            providerRepositoryId,
            branches: names,
            languageCode: language,
            generateSkill,
          });
      setResult(response);
    } catch (error) {
      setFailure({ message: t(`repositories.errors.${errorKey(error)}`), branches: getErrorBranches(error) });
    } finally {
      setSubmitting(false);
    }
  }

  const created = result?.branches.filter((item) => item.created).length ?? 0;
  const existing = (result?.branches.length ?? 0) - created;

  return (
    <section aria-labelledby="plan-heading" className="flex min-h-0 flex-col">
      <div className="border-b px-4 py-3">
        <p className="text-primary text-[10px] font-bold tracking-widest uppercase">{t("repositories.plan.title")}</p>
        <h2 id="plan-heading" className="mt-1 font-mono text-sm font-bold break-all">
          {repository.fullName}
        </h2>
      </div>

      <div className="flex flex-col gap-4 overflow-auto p-4">
        {!usable && <p className="text-sm">{t("repositories.plan.disabledReason")}</p>}

        {failure && (
          <div
            ref={alertRef}
            role="alert"
            tabIndex={-1}
            className="border-destructive/40 bg-destructive/10 text-destructive rounded-md border p-3 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <p>{failure.message}</p>
            {failure.branches.length > 0 && (
              <p className="mt-1">{t("repositories.plan.failedBranches", { branches: failure.branches.join(", ") })}</p>
            )}
          </div>
        )}

        {usable && loadStatus === "loading" && (
          <div role="status" className="flex flex-col gap-2">
            <span className="sr-only">{t("repositories.plan.branchesLoading")}</span>
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-9 w-full" />
          </div>
        )}

        {usable && loadStatus === "ready" && (
          <fieldset className="flex flex-col gap-2">
            <legend className="flex w-full items-center justify-between text-xs font-semibold">
              <span>{t("repositories.plan.branchesLabel")}</span>
              <span className="text-muted-foreground font-normal" aria-live="polite">
                {t("repositories.plan.selectedCount", { count: selected.size })}
              </span>
            </legend>
            {visibleBranches.length === 0 && (
              <p className="text-muted-foreground text-sm">{t("repositories.plan.branchesEmpty")}</p>
            )}
            <ul className="divide-y rounded-md border">
              {visibleBranches.map((branch, index) => {
                const id = `plan-branch-${index}`;
                return (
                  <li key={branch.name} className="flex items-center gap-3 px-3 py-2">
                    <Checkbox
                      id={id}
                      checked={selected.has(branch.name)}
                      disabled={submitting}
                      onCheckedChange={(value) => toggle(branch.name, value === true)}
                    />
                    <label htmlFor={id} className="flex min-w-0 flex-1 items-center justify-between gap-2 text-sm">
                      <code className="truncate">{branch.name}</code>
                      <span className="text-muted-foreground flex shrink-0 items-center gap-2 font-mono text-[10px]">
                        {branch.isDefault && <span className="font-sans">{t("repositories.plan.defaultBranch")}</span>}
                        {branch.commitSha?.slice(0, 7)}
                      </span>
                    </label>
                  </li>
                );
              })}
            </ul>
            {nextCursor && (
              <Button type="button" size="sm" variant="outline" disabled={loadingMore} onClick={loadMore}>
                {loadingMore && <Loader2 aria-hidden className="mr-2 h-4 w-4 animate-spin" />}
                {t("repositories.plan.loadMoreBranches")}
              </Button>
            )}
          </fieldset>
        )}

        <div className="flex flex-col gap-1.5">
          <label htmlFor="plan-language" className="text-xs font-semibold">
            {t("repositories.plan.language")}
          </label>
          <select
            id="plan-language"
            className="border-input bg-background h-9 w-full rounded-md border px-2 text-sm shadow-xs outline-none focus-visible:ring-2 focus-visible:ring-ring"
            value={language}
            disabled={!usable || submitting}
            onChange={(event) => setLanguage(event.target.value)}
          >
            {languages.map((option) => (
              <option key={option.code} value={option.code}>
                {option.label}
              </option>
            ))}
          </select>
        </div>

        {!repositoryId && (
          <div className="flex items-start gap-3">
            <Checkbox
              id="plan-skill"
              className="mt-0.5"
              checked={generateSkill}
              disabled={!usable || submitting}
              onCheckedChange={(value) => setGenerateSkill(value === true)}
              aria-describedby="plan-skill-help"
            />
            <div className="flex flex-col gap-0.5">
              <label htmlFor="plan-skill" className="text-xs font-semibold">
                {t("repositories.plan.generateSkill")}
              </label>
              <p id="plan-skill-help" className="text-muted-foreground text-xs">
                {t("repositories.plan.generateSkillHelp")}
              </p>
            </div>
          </div>
        )}

        <div className="flex flex-col gap-2">
          {usable && loadStatus === "ready" && selected.size === 0 && (
            <p className="text-muted-foreground text-xs">{t("repositories.plan.noBranchSelected")}</p>
          )}
          <Button type="button" disabled={!usable || loadStatus !== "ready" || selected.size === 0 || submitting} onClick={submit}>
            {submitting && <Loader2 aria-hidden className="mr-2 h-4 w-4 animate-spin" />}
            {t(submitting ? "repositories.plan.submitting" : repositoryId ? "repositories.plan.submitAdd" : "repositories.plan.submit")}
          </Button>
          <p className="text-muted-foreground text-center text-xs">{t("repositories.plan.providerNote")}</p>
        </div>

        {result && (
          <div className="flex flex-col gap-2" role="status" aria-live="polite">
            <h3 className="text-sm font-semibold">{t("repositories.plan.resultsTitle")}</h3>
            <p className="text-xs">{t("repositories.plan.resultSummary", { created, existing })}</p>
            <ul aria-label={t("repositories.plan.resultsTitle")} className="divide-y rounded-md border">
              {result.branches.map((item) => (
                <li key={item.branchId} className="flex items-center justify-between gap-2 px-3 py-2 text-sm">
                  <code className="truncate">{item.branchName}</code>
                  <span className="inline-flex shrink-0 items-center gap-1 text-xs">
                    {item.created ? (
                      <CheckCircle2 aria-hidden className="h-3.5 w-3.5 text-emerald-600" />
                    ) : (
                      <CircleDashed aria-hidden className="text-muted-foreground h-3.5 w-3.5" />
                    )}
                    {item.created
                      ? t("repositories.plan.resultCreated", { status: item.taskStatus ?? "-" })
                      : t("repositories.plan.resultExisting")}
                  </span>
                </li>
              ))}
            </ul>
            <Button type="button" variant="outline" onClick={() => onManage(result)}>
              {t("repositories.plan.manage")}
            </Button>
          </div>
        )}
      </div>
    </section>
  );
}
