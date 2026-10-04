"use client";

import * as React from "react";
import Link from "next/link";
import { Loader2 } from "lucide-react";

import { useTranslations } from "@/hooks/use-translations";
import { isAbortError, listConnections } from "@/lib/git-connections-api";
import type { GitConnection } from "@/types/git-connection";

interface PrivateConnectionPickerProps {
  /** Repository URL that the user typed. A connection is offered only when it belongs to the same Git server. */
  gitUrl: string;
  value: string;
  onChange: (connectionId: string) => void;
  error?: string;
}

/**
 * True when the repository URL has the HTTPS scheme, host and port of the connection, so a token never reaches
 * another host. The backend applies the same rule when it resolves the connection of a submit.
 */
export function isSameGitServer(gitUrl: string, serverUrl: string): boolean {
  try {
    const repository = new URL(gitUrl.trim());
    const server = new URL(serverUrl);
    return (
      repository.protocol === "https:" &&
      server.protocol === "https:" &&
      repository.host.toLowerCase() === server.host.toLowerCase()
    );
  } catch {
    return false;
  }
}

export function PrivateConnectionPicker({ gitUrl, value, onChange, error }: PrivateConnectionPickerProps) {
  const t = useTranslations();
  const [connections, setConnections] = React.useState<GitConnection[] | null>(null);
  const [failed, setFailed] = React.useState(false);

  React.useEffect(() => {
    const controller = new AbortController();
    listConnections(controller.signal)
      .then((list) => {
        if (!controller.signal.aborted) setConnections(list);
      })
      .catch((reason: unknown) => {
        if (!controller.signal.aborted && !isAbortError(reason)) setFailed(true);
      });
    return () => controller.abort();
  }, []);

  const eligible = (connections ?? []).filter((item) => item.isEnabled && isSameGitServer(gitUrl, item.serverUrl));
  const selected = eligible.some((item) => item.id === value) ? value : "";

  // A stored choice that no longer fits the URL must not be sent.
  React.useEffect(() => {
    if (connections && value && !selected) onChange("");
  }, [connections, value, selected, onChange]);

  return (
    <div className="space-y-3 rounded-xl border border-amber-500/20 bg-amber-500/5 p-4">
      <p className="text-xs font-medium text-amber-600 dark:text-amber-400">{t("home.repository.connectionHint")}</p>

      {connections === null && !failed && (
        <p role="status" className="text-muted-foreground flex items-center gap-2 text-xs">
          <Loader2 aria-hidden className="h-3 w-3 animate-spin" />
          {t("home.repository.connectionLoading")}
        </p>
      )}
      {failed && <p role="alert" className="text-destructive text-xs">{t("home.repository.connectionLoadError")}</p>}

      {connections !== null && eligible.length === 0 && (
        <p className="text-sm">
          {t("home.repository.connectionNone")}{" "}
          <Link href="/repositories" className="text-primary underline underline-offset-4">
            {t("home.repository.connectionManage")}
          </Link>
        </p>
      )}

      {eligible.length > 0 && (
        <div className="space-y-2">
          <label htmlFor="private-connection" className="text-sm font-medium">
            {t("home.repository.connectionLabel")}
          </label>
          <select
            id="private-connection"
            value={selected}
            aria-invalid={Boolean(error)}
            aria-describedby={error ? "private-connection-error" : undefined}
            onChange={(event) => onChange(event.target.value)}
            className="border-input bg-background h-11 w-full rounded-md border px-3 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <option value="">{t("home.repository.connectionPlaceholder")}</option>
            {eligible.map((item) => (
              <option key={item.id} value={item.id}>
                {item.displayName} ({item.accountLogin})
              </option>
            ))}
          </select>
          {error && (
            <p id="private-connection-error" className="text-destructive text-xs">
              {error}
            </p>
          )}
        </div>
      )}
    </div>
  );
}
