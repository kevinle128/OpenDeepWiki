"use client";

import { useState, useSyncExternalStore } from "react";
import { X, Sparkles } from "lucide-react";
import { useTranslations } from "@/hooks/use-translations";
import { cn } from "@/lib/utils";

const STORAGE_KEY = "odw-announcement-routinai-v1";
const SPONSOR_URL = "https://routin.ai/";
const subscribe = (onChange: () => void) => {
  window.addEventListener("storage", onChange);
  return () => window.removeEventListener("storage", onChange);
};
const getVisible = () => {
  try {
    return window.localStorage.getItem(STORAGE_KEY) !== "1";
  } catch {
    return true;
  }
};

export function AnnouncementBanner({ className }: { className?: string }) {
  const t = useTranslations();
  const [dismissed, setDismissed] = useState(false);
  const visible = useSyncExternalStore(subscribe, getVisible, () => false) && !dismissed;

  const handleDismiss = () => {
    setDismissed(true);
    try {
      window.localStorage.setItem(STORAGE_KEY, "1");
    } catch {
      // ignore storage failures
    }
  };

  if (!visible) {
    return null;
  }

  return (
    <div
      role="region"
      aria-label={t("common.announcement.label")}
      className={cn(
        "relative z-30 w-full border-b border-sky-200/70 bg-sky-50 text-sky-950 dark:border-sky-400/15 dark:bg-sky-950/40 dark:text-sky-50",
        className
      )}
    >
      <div className="relative mx-auto flex min-h-10 max-w-[1400px] items-center justify-center gap-3 px-10 py-2 sm:px-12">
        <p className="flex flex-wrap items-center justify-center gap-x-1.5 gap-y-1 text-center text-[13px] leading-5">
          <Sparkles className="hidden size-3.5 shrink-0 text-sky-500 sm:inline dark:text-sky-300" />
          <span>{t("common.announcement.prefix")}</span>
          <a
            href={SPONSOR_URL}
            target="_blank"
            rel="noopener noreferrer"
            className="font-medium text-sky-600 underline-offset-2 transition-colors hover:text-sky-700 hover:underline dark:text-sky-300 dark:hover:text-sky-200"
          >
            {t("common.announcement.linkText")}
          </a>
          <span>{t("common.announcement.suffix")}</span>
        </p>

        <button
          type="button"
          onClick={handleDismiss}
          aria-label={t("common.announcement.dismiss")}
          className="absolute top-1/2 right-2 flex size-7 -translate-y-1/2 items-center justify-center rounded-md text-sky-700/70 transition-colors hover:bg-sky-100 hover:text-sky-900 dark:text-sky-200/70 dark:hover:bg-sky-900/60 dark:hover:text-sky-50 sm:right-3"
        >
          <X className="size-3.5" />
        </button>
      </div>
    </div>
  );
}
