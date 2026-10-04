import { expect, it } from "vitest";

import { defaultWikiLanguages, wikiLanguageCodes } from "@/i18n/config";
import { getLanguageOptions } from "./language-options";

it("offers English and Vietnamese for indexing and retains historical wiki language support", () => {
  expect(getLanguageOptions()).toEqual([
    { code: "en", label: "English" },
    { code: "vi", label: "Tiếng Việt" },
  ]);
  expect(defaultWikiLanguages).toBe("en,vi");
  expect(wikiLanguageCodes).toContain("vi");
  expect(wikiLanguageCodes).toContain("zh");
});
