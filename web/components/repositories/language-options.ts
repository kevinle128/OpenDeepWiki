import { defaultWikiLanguages } from "@/i18n/config";

/** Documentation languages with their own native names. */
export function getLanguageOptions(): { code: string; label: string }[] {
  return defaultWikiLanguages.split(",").map((code) => {
    let label: string = code;
    try {
      label = new Intl.DisplayNames([code], { type: "language" }).of(code) ?? code;
    } catch {
      // The runtime has no name for this code, so the code itself is shown.
    }
    return { code, label };
  });
}
