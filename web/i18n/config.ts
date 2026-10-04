/**
 * Shared i18n configuration — single source of truth for UI locales and wiki languages.
 *
 * Two distinct concepts live here:
 * - `uiLocales`: interface languages (BCP-47 style, e.g. `pt-BR`)
 * - `wikiLanguageCodes`: repository documentation generation languages (lower-case, e.g. `pt-br`)
 *
 * They are intentionally separated so that adding a UI language does not
 * implicitly enable it as a document-generation language on the backend.
 */

// ---------------------------------------------------------------------------
// UI locales (frontend interface language)
// ---------------------------------------------------------------------------

export const uiLocales = [
  "zh",
  "en",
  "ko",
  "ja",
  "es",
  "fr",
  "de",
  "pt-BR",
] as const;

export type UiLocale = (typeof uiLocales)[number];

export const defaultUiLocale: UiLocale = "en";

export const uiLocaleNames: Record<UiLocale, string> = {
  zh: "简体中文",
  en: "English",
  ko: "한국어",
  ja: "日本語",
  es: "Español",
  fr: "Français",
  de: "Deutsch",
  "pt-BR": "Português (Brasil)",
};

// ---------------------------------------------------------------------------
// Wiki / document-generation languages (sent to backend as `languageCode`)
// ---------------------------------------------------------------------------

export const wikiLanguageCodes = [
  "en",
  "zh",
  "zh-tw",
  "ja",
  "ko",
  "es",
  "fr",
  "de",
  "pt-br",
  "pl",
  "ru",
  "ar",
  "vi",
] as const;

export type WikiLanguageCode = (typeof wikiLanguageCodes)[number];

export const defaultWikiLanguage: WikiLanguageCode = "en";

/**
 * Default auto-translation target list. Keep in sync with
 * `WikiLanguageNames.DefaultLanguages` on the backend.
 */
export const defaultWikiLanguages = "en,vi";

/**
 * Map a UI locale to a supported wiki language code.
 * Falls back to `defaultWikiLanguage` when the UI locale has no direct
 * document-generation equivalent.
 */
export function resolveWikiLanguageFromUiLocale(locale: string): WikiLanguageCode {
  const lower = locale.toLowerCase();
  if (wikiLanguageCodes.includes(lower as WikiLanguageCode)) {
    return lower as WikiLanguageCode;
  }
  if (lower === "zh-cn" || lower === "zh-hans") {
    return "zh";
  }
  if (lower === "zh-hant") {
    return "zh-tw";
  }
  if (lower === "pt" || lower === "pt-pt") {
    return "pt-br";
  }
  return defaultWikiLanguage;
}

/**
 * Map a wiki language code to a UI locale when the interface has a matching pack.
 * Returns `null` when the wiki language has no dedicated UI translation.
 */
export function resolveUiLocaleFromWikiLanguage(languageCode: string): UiLocale | null {
  if (isUiLocale(languageCode)) {
    return languageCode;
  }

  const lower = languageCode.toLowerCase();
  if (lower === "pt-br" || lower === "pt" || lower === "pt-pt") {
    return "pt-BR";
  }
  if (lower === "zh-cn" || lower === "zh-hans" || lower === "zh-tw" || lower === "zh-hant") {
    return "zh";
  }

  const match = uiLocales.find((locale) => locale.toLowerCase() === lower);
  return match ?? null;
}

/**
 * Check whether a string is a valid UI locale.
 */
export function isUiLocale(value: string): value is UiLocale {
  return (uiLocales as readonly string[]).includes(value);
}
