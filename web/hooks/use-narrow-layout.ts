"use client";

import { useSyncExternalStore } from "react";

/** The workspace switches from three columns to three tabs below this width. */
export const NARROW_LAYOUT_QUERY = "(max-width: 949px)";

function subscribe(onChange: () => void) {
  if (typeof window === "undefined" || typeof window.matchMedia !== "function") return () => {};
  const query = window.matchMedia(NARROW_LAYOUT_QUERY);
  query.addEventListener("change", onChange);
  return () => query.removeEventListener("change", onChange);
}

function getSnapshot() {
  return typeof window !== "undefined" && typeof window.matchMedia === "function"
    ? window.matchMedia(NARROW_LAYOUT_QUERY).matches
    : false;
}

/** The server renders the desktop layout. The client corrects it after hydration. */
export function useNarrowLayout(): boolean {
  return useSyncExternalStore(subscribe, getSnapshot, () => false);
}
