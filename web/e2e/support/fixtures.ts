// Playwright passes a callback named `use` to fixtures. It is not a React hook, so the hooks rule does not apply.
/* eslint-disable react-hooks/rules-of-hooks */
import { expect, test as base, type BrowserContext, type Page, type Response } from "@playwright/test";

import { hostRequests } from "./api";
import { SCREENSHOT_DIR, WEB_URL, statePath, type UserKey } from "./env";

export interface OpenOptions {
  viewport?: { width: number; height: number };
}

export interface Opened {
  context: BrowserContext;
  page: Page;
  /** Console messages and uncaught page errors, as plain text. */
  consoleLines: string[];
  /** Every response body that the browser received from the web origin. Filled only when `captureBodies` is on. */
  bodies: { url: string; text: string }[];
  captureBodies(): void;
}

interface BrowserCall {
  method: string;
  path: string;
}

const WEB_ORIGIN = new URL(WEB_URL).origin;
/** The only third-party host that the application contacts: generated avatars. The tests answer it locally. */
const AVATAR_ORIGIN = "https://api.dicebear.com";
const EMPTY_SVG = '<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>';

export const test = base.extend<{ openAs: (user: UserKey | "anonymous", options?: OpenOptions) => Promise<Opened> }>({
  openAs: async ({ browser }, use, testInfo) => {
    const opened: { value: Opened; user: string }[] = [];
    const calls: BrowserCall[] = [];
    const foreign: string[] = [];

    await use(async (user, options = {}) => {
      const context = await browser.newContext({
        ...(user === "anonymous" ? {} : { storageState: statePath(user) }),
        baseURL: WEB_URL,
        viewport: options.viewport ?? { width: 1440, height: 900 },
        locale: "en-US",
        timezoneId: "Asia/Saigon",
      });
      // Contexts that the test creates follow the project's trace setting (retain-on-failure). A spec that handles a
      // token turns the setting off and starts and stops tracing itself.
      // The run is hermetic: no request leaves the machine. Avatars are stubbed, anything else is refused and reported.
      await context.route(
        (url) => url.protocol.startsWith("http") && url.origin !== WEB_ORIGIN,
        async (route) => {
          const origin = new URL(route.request().url()).origin;
          if (origin === AVATAR_ORIGIN) {
            await route.fulfill({ status: 200, contentType: "image/svg+xml", body: EMPTY_SVG });
            return;
          }
          foreign.push(`${route.request().method()} ${origin}`);
          await route.abort();
        },
      );
      const page = await context.newPage();
      const consoleLines: string[] = [];
      const bodies: { url: string; text: string }[] = [];
      let capture = false;

      page.on("console", (message) => consoleLines.push(`${message.type()}: ${message.text()}`));
      page.on("pageerror", (error) => consoleLines.push(`pageerror: ${error.message}`));
      page.on("response", (response: Response) => {
        const url = new URL(response.url());
        if (url.origin !== WEB_ORIGIN) return;
        if (url.pathname.startsWith("/api/")) calls.push({ method: response.request().method(), path: url.pathname });
        if (capture) {
          response
            .text()
            .then((text) => bodies.push({ url: url.pathname + url.search, text }))
            .catch(() => undefined);
        }
      });

      const value: Opened = { context, page, consoleLines, bodies, captureBodies: () => (capture = true) };
      opened.push({ value, user });
      return value;
    });

    const failed = testInfo.status !== testInfo.expectedStatus;
    for (const { value, user } of opened) {
      if (failed) {
        await value.page.screenshot({ path: testInfo.outputPath(`failure-${user}.png`) }).catch(() => undefined);
      }
      await value.context.close();
    }

    // Every API response the browser saw came through the web proxy and must be in the host's request log.
    expect(foreign, "The browser must only contact the web origin").toEqual([]);
    await expectCallsReachedHost(calls);
  },
});

/** Compares the browser's API calls with the host log as multisets of method and path. */
async function expectCallsReachedHost(calls: BrowserCall[]): Promise<void> {
  if (calls.length === 0) return;
  const key = (call: BrowserCall) => `${call.method} ${call.path}`;
  const wanted = new Map<string, number>();
  for (const call of calls) wanted.set(key(call), (wanted.get(key(call)) ?? 0) + 1);

  await expect
    .poll(
      async () => {
        const seen = new Map<string, number>();
        for (const entry of await hostRequests()) {
          const entryKey = `${entry.method} ${entry.path}`;
          seen.set(entryKey, (seen.get(entryKey) ?? 0) + 1);
        }
        return [...wanted].filter(([entryKey, count]) => (seen.get(entryKey) ?? 0) < count).map(([entryKey]) => entryKey);
      },
      { message: "Every proxied browser request must reach the test API host", timeout: 5_000 },
    )
    .toEqual([]);
}

/** Saves a screenshot for manual review when `E2E_SCREENSHOT_DIR` is set. Never used in token flows. */
export async function reviewShot(page: Page, name: string): Promise<void> {
  if (!SCREENSHOT_DIR) return;
  await page.screenshot({ path: `${SCREENSHOT_DIR}/${name}.png`, fullPage: false });
}

export { expect };
