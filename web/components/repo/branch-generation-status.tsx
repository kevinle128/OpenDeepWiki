"use client";

import * as React from "react";
import { CheckCircle2, CircleDashed, FileText, Loader2, XCircle } from "lucide-react";

import { useTranslations } from "@/hooks/use-translations";
import { cn } from "@/lib/utils";

const KNOWN_STATUSES = ["Pending", "Processing", "Completed", "Failed", "Cancelled"] as const;

/** Text and icon for a generation status, so the state never depends on colour alone. */
export function GenerationStatusBadge({ status, className }: { status?: string | null; className?: string }) {
  const t = useTranslations();
  const known = KNOWN_STATUSES.find((item) => item === status);
  const label = known ? t(`repositories.statuses.${known}`) : status ? status : t("repositories.statuses.None");

  return (
    <span className={cn("inline-flex items-center gap-1 text-xs font-medium", className)}>
      {(status === "Pending" || status === "Processing") && <Loader2 aria-hidden className="h-3 w-3 animate-spin text-sky-500" />}
      {status === "Failed" && <XCircle aria-hidden className="h-3 w-3 text-red-500" />}
      {status === "Completed" && <CheckCircle2 aria-hidden className="h-3 w-3 text-emerald-500" />}
      {!status && <CircleDashed aria-hidden className="h-3 w-3 text-muted-foreground" />}
      <span>{label}</span>
    </span>
  );
}

interface BranchGenerationStatusProps {
  branchId?: string;
  branchName: string;
  generationStatus?: string;
  lastGenerationTaskId?: string;
  lastGenerationError?: string;
}

export function BranchGenerationStatus({
  branchId,
  branchName,
  generationStatus,
  lastGenerationTaskId,
  lastGenerationError,
}: BranchGenerationStatusProps) {
  const t = useTranslations();
  const isFailed = generationStatus === "Failed";
  const shortTaskId = lastGenerationTaskId
    ? lastGenerationTaskId.length > 12
      ? `${lastGenerationTaskId.slice(0, 8)}...`
      : lastGenerationTaskId
    : null;

  if (!branchId) return null;

  return (
    <div className="mt-4 flex flex-col gap-2 rounded-md border p-3 text-xs">
      <div className="font-medium">{t("repositories.generation.title")}</div>

      <div aria-live="polite" className="flex flex-col gap-2">
        {generationStatus && (
          <div className="text-muted-foreground flex items-center justify-between">
            <span>{t("repositories.generation.status")}</span>
            <GenerationStatusBadge status={generationStatus} />
          </div>
        )}
        {isFailed && lastGenerationError && (
          <div className="text-red-500 mt-1 line-clamp-2" title={lastGenerationError}>
            {lastGenerationError}
          </div>
        )}
      </div>

      {shortTaskId && (
        <div className="text-muted-foreground flex items-center justify-between">
          <span>{t("repositories.generation.task")}</span>
          <span className="font-mono" title={lastGenerationTaskId}>{shortTaskId}</span>
        </div>
      )}

      {lastGenerationTaskId && (
        <div className="mt-1 inline-flex items-center gap-1 text-[10px] text-muted-foreground">
          <FileText aria-hidden className="h-3 w-3" />
          <span>{t("repositories.generation.logs", { branch: branchName, task: shortTaskId ?? "" })}</span>
        </div>
      )}
    </div>
  );
}
