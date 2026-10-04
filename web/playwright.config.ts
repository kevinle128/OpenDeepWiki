import fs from "node:fs";

import { defineConfig, devices } from "@playwright/test";

import { API_LOG, API_PORT, API_PROXY_URL, API_WORK_DIR, RUN_DIR, WEB_LOG, WEB_PORT, WEB_URL } from "./e2e/support/env";
import { assertPortsFree } from "./e2e/support/port-guard";

// The runner process evaluates this file before it starts any server. Workers evaluate it again while the
// servers run, so the check belongs to the runner only.
if (process.env.TEST_WORKER_INDEX === undefined) {
  assertPortsFree([WEB_PORT, API_PORT]);
  // Server logs and session files of an earlier run must not leak into this one.
  fs.rmSync(RUN_DIR, { recursive: true, force: true });
  fs.mkdirSync(RUN_DIR, { recursive: true });
}

const quote = (value: string) => `'${value.replace(/'/g, `'\\''`)}'`;
const apiProject = "../tests/OpenDeepWiki.E2EHost";

export default defineConfig({
  testDir: "./e2e",
  testMatch: /.*\.spec\.ts/,
  // The specs share one application database and one fake provider, so they run in order in one worker.
  fullyParallel: false,
  workers: 1,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: process.env.CI ? [["list"], ["html", { open: "never" }]] : [["list"]],
  globalSetup: "./e2e/support/global-setup.ts",
  globalTeardown: "./e2e/support/global-teardown.ts",
  outputDir: "./test-results",
  use: {
    baseURL: WEB_URL,
    // Failure artifacts stay on disk and CI uploads them only for a failed run. A successful run keeps none.
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "off",
    locale: "en-US",
    timezoneId: "Asia/Saigon",
  },
  projects: [
    {
      name: "chromium",
      testIgnore: /gates\.spec\.ts/,
      use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } },
      teardown: "gates",
    },
    {
      // Runs after the browser project, even when it failed: it checks the runtime evidence of the whole run.
      name: "gates",
      testMatch: /gates\.spec\.ts/,
      use: { ...devices["Desktop Chrome"] },
    },
  ],
  webServer: [
    {
      name: "api",
      // The host deletes its own work directory at start and at shutdown. The shell replaces itself with the
      // host process (exec), so the stop signal reaches the host directly.
      command:
        `mkdir -p ${quote(RUN_DIR)} && dotnet build ${quote(apiProject)} -c Debug --nologo -v q > ${quote(`${RUN_DIR}/api-build.log`)} 2>&1 ` +
        `&& exec dotnet ${quote(`${apiProject}/bin/Debug/net10.0/OpenDeepWiki.E2EHost.dll`)} ` +
        `--port ${API_PORT} --work-dir ${quote(API_WORK_DIR)} > ${quote(API_LOG)} 2>&1`,
      url: `${API_PROXY_URL}/health`,
      reuseExistingServer: false,
      timeout: 300_000,
      gracefulShutdown: { signal: "SIGTERM", timeout: 20_000 },
    },
    {
      name: "web",
      // The proxy target is explicit and the browser never gets a direct API address (the public variable is empty).
      command:
        `mkdir -p ${quote(RUN_DIR)} ` +
        `&& ${process.env.E2E_SKIP_BUILD ? "true" : `npm run build > ${quote(`${RUN_DIR}/web-build.log`)} 2>&1`} ` +
        `&& exec npx next start -H 127.0.0.1 -p ${WEB_PORT} > ${quote(WEB_LOG)} 2>&1`,
      url: WEB_URL,
      reuseExistingServer: false,
      timeout: 600_000,
      gracefulShutdown: { signal: "SIGTERM", timeout: 10_000 },
      env: {
        API_PROXY_URL,
        NEXT_PUBLIC_API_PROXY_URL: "",
        NEXT_TELEMETRY_DISABLED: "1",
      },
    },
  ],
});
