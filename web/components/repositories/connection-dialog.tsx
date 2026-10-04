"use client";

import * as React from "react";
import { Loader2 } from "lucide-react";

import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { useTranslations } from "@/hooks/use-translations";
import { createConnection, isAbortError, updateConnection } from "@/lib/git-connections-api";
import type { GitConnection, GitProviderKind } from "@/types/git-connection";
import { errorKey } from "./error-key";

type ProviderChoice = "github" | "gitlab" | "gitlab-self-hosted";

interface ConnectionDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Present when the dialog edits a connection. Only `hasSecret` of it is shown, never a token. */
  connection?: GitConnection | null;
  onSaved: (connection: GitConnection, existing: boolean) => void;
  /** The control that opened the dialog. It gets the focus back on close, because the dialog has no trigger element. */
  returnFocusRef?: React.RefObject<HTMLElement | null>;
}

const PROVIDER_KIND: Record<ProviderChoice, GitProviderKind> = {
  github: "GitHub",
  gitlab: "GitLab",
  "gitlab-self-hosted": "GitLab",
};

const fieldClass =
  "border-input bg-background h-9 w-full rounded-md border px-3 text-sm shadow-xs outline-none focus-visible:ring-2 focus-visible:ring-ring disabled:opacity-60";

export function ConnectionDialog({ open, onOpenChange, connection, onSaved, returnFocusRef }: ConnectionDialogProps) {
  const t = useTranslations();
  const editing = Boolean(connection);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        className="sm:max-w-md"
        onCloseAutoFocus={(event) => {
          event.preventDefault();
          returnFocusRef?.current?.focus();
        }}
      >
        <DialogHeader>
          <DialogTitle>{t(editing ? "repositories.dialog.editTitle" : "repositories.dialog.addTitle")}</DialogTitle>
          <DialogDescription>{t("repositories.dialog.description")}</DialogDescription>
        </DialogHeader>
        {/* The form mounts with the content, so every opening starts from stored values and an empty token. */}
        <ConnectionForm connection={connection} onOpenChange={onOpenChange} onSaved={onSaved} />
      </DialogContent>
    </Dialog>
  );
}

interface ConnectionFormProps {
  connection?: GitConnection | null;
  onOpenChange: (open: boolean) => void;
  onSaved: (connection: GitConnection, existing: boolean) => void;
}

function initialProvider(connection?: GitConnection | null): ProviderChoice {
  if (!connection || connection.provider === "GitHub") return "github";
  return /^https:\/\/(www\.)?gitlab\.com\/?$/i.test(connection.serverUrl) ? "gitlab" : "gitlab-self-hosted";
}

function ConnectionForm({ connection, onOpenChange, onSaved }: ConnectionFormProps) {
  const t = useTranslations();
  const editing = Boolean(connection);
  const [provider, setProvider] = React.useState<ProviderChoice>(() => initialProvider(connection));
  const [serverUrl, setServerUrl] = React.useState(connection?.serverUrl ?? "");
  const [displayName, setDisplayName] = React.useState(connection?.displayName ?? "");
  const [token, setToken] = React.useState("");
  const [saving, setSaving] = React.useState(false);
  const [summary, setSummary] = React.useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = React.useState<Record<string, string>>({});
  const summaryRef = React.useRef<HTMLDivElement>(null);

  React.useEffect(() => {
    if (summary) summaryRef.current?.focus();
  }, [summary]);

  const selfHosted = provider === "gitlab-self-hosted";

  function validate(): Record<string, string> {
    const errors: Record<string, string> = {};
    if (!displayName.trim()) errors.displayName = t("repositories.dialog.required");
    if (!editing && !token.trim()) errors.token = t("repositories.dialog.required");
    if (!editing && selfHosted) {
      if (!serverUrl.trim()) errors.serverUrl = t("repositories.dialog.required");
      else if (!/^https:\/\//i.test(serverUrl.trim())) errors.serverUrl = t("repositories.dialog.serverUrlHttps");
    }
    return errors;
  }

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    if (saving) return;

    const errors = validate();
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) {
      setSummary(t("repositories.dialog.errorSummary"));
      return;
    }

    setSaving(true);
    setSummary(null);
    try {
      if (connection) {
        const saved = await updateConnection(connection.id, {
          displayName: displayName.trim(),
          token: token.trim() || undefined,
        });
        setToken("");
        onSaved(saved, false);
      } else {
        const result = await createConnection({
          provider: PROVIDER_KIND[provider],
          displayName: displayName.trim(),
          serverUrl: selfHosted ? serverUrl.trim() : undefined,
          token: token.trim(),
        });
        setToken("");
        onSaved(result.connection, result.existing);
      }
      onOpenChange(false);
    } catch (error) {
      if (isAbortError(error)) return;
      // The token field is cleared after a failure so a secret never lingers in the form.
      setToken("");
      setSummary(t(`repositories.errors.${errorKey(error)}`));
    } finally {
      setSaving(false);
    }
  }

  return (
    <form onSubmit={handleSubmit} noValidate className="flex flex-col gap-4">
      {summary && (
        <div
          ref={summaryRef}
          role="alert"
          tabIndex={-1}
          className="border-destructive/40 bg-destructive/10 text-destructive rounded-md border p-3 text-sm outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          {summary}
        </div>
      )}

      <div className="flex flex-col gap-1.5">
        <label htmlFor="connection-provider" className="text-sm font-medium">
          {t("repositories.dialog.provider")}
        </label>
        <select
          id="connection-provider"
          className={fieldClass}
          value={provider}
          disabled={editing}
          onChange={(event) => setProvider(event.target.value as ProviderChoice)}
        >
          <option value="github">{t("repositories.dialog.providerGitHub")}</option>
          <option value="gitlab">{t("repositories.dialog.providerGitLab")}</option>
          <option value="gitlab-self-hosted">{t("repositories.dialog.providerGitLabSelfHosted")}</option>
        </select>
      </div>

      {selfHosted && (
        <div className="flex flex-col gap-1.5">
          <label htmlFor="connection-server-url" className="text-sm font-medium">
            {t("repositories.dialog.serverUrl")}
          </label>
          <Input
            id="connection-server-url"
            value={serverUrl}
            readOnly={editing}
            autoComplete="off"
            inputMode="url"
            aria-invalid={Boolean(fieldErrors.serverUrl)}
            aria-describedby="connection-server-url-hint"
            onChange={(event) => setServerUrl(event.target.value)}
          />
          <p id="connection-server-url-hint" className="text-muted-foreground text-xs">
            {fieldErrors.serverUrl ?? t("repositories.dialog.serverUrlHint")}
          </p>
        </div>
      )}

      <div className="flex flex-col gap-1.5">
        <label htmlFor="connection-name" className="text-sm font-medium">
          {t("repositories.dialog.displayName")}
        </label>
        <Input
          id="connection-name"
          value={displayName}
          autoComplete="off"
          aria-invalid={Boolean(fieldErrors.displayName)}
          aria-describedby={fieldErrors.displayName ? "connection-name-error" : undefined}
          onChange={(event) => setDisplayName(event.target.value)}
        />
        {fieldErrors.displayName && (
          <p id="connection-name-error" className="text-destructive text-xs">
            {fieldErrors.displayName}
          </p>
        )}
      </div>

      <div className="flex flex-col gap-1.5">
        <label htmlFor="connection-token" className="text-sm font-medium">
          {t(editing ? "repositories.dialog.tokenReplace" : "repositories.dialog.token")}
        </label>
        <Input
          id="connection-token"
          type="password"
          value={token}
          autoComplete="new-password"
          spellCheck={false}
          aria-invalid={Boolean(fieldErrors.token)}
          aria-describedby="connection-token-hint"
          onChange={(event) => setToken(event.target.value)}
        />
        <p id="connection-token-hint" className="text-muted-foreground text-xs">
          {fieldErrors.token ??
            (editing && connection?.hasSecret
              ? t("repositories.dialog.tokenSaved")
              : t("repositories.dialog.tokenNewHint"))}
        </p>
      </div>

      <DialogFooter>
        <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
          {t("repositories.dialog.cancel")}
        </Button>
        <Button type="submit" disabled={saving}>
          {saving && <Loader2 aria-hidden className="mr-2 h-4 w-4 animate-spin" />}
          {t(saving ? "repositories.dialog.saving" : "repositories.dialog.submit")}
        </Button>
      </DialogFooter>
    </form>
  );
}
