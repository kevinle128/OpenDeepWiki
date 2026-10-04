import fs from "node:fs";
import path from "node:path";

import { expect, test } from "@playwright/test";

import { hostInfo, hostRequests, providerCalls } from "./support/api";
import { API_LOG, API_PROXY_URL, API_WORK_DIR, CANARY, RUN_DIR, WEB_LOG } from "./support/env";
import { fileContains, filesContaining, walkFiles } from "./support/scan";

const KNOWN_PROVIDER_HOSTS = ["api.github.com", "gitlab.com", "git.public-selfhosted.test", "gitlab.corp-allowed.test"];

/**
 * Evidence about the whole run. The project runs after the browser project, even when that failed, while both
 * servers are still up.
 */
test.describe("run evidence", () => {
  test("every request that the web proxy forwarded reached the test API host, and only those", async () => {
    const log = fs.readFileSync(WEB_LOG, "utf8");
    expect(log, "the proxy must have an upstream").not.toContain("API_PROXY_URL_NOT_CONFIGURED");
    expect(log).not.toContain("环境变量未配置");

    const targets = [...log.matchAll(/转发目标: (\S+)/g)].map((match) => match[1]);
    expect(targets.length, "the proxy forwarded at least one request").toBeGreaterThan(0);
    const foreign = targets.filter((target) => !target.startsWith(`${API_PROXY_URL}/`));
    expect(foreign, "every forwarded request targets the fixed test API origin").toEqual([]);

    const key = (method: string, pathname: string) => `${method} ${pathname}`;
    const forwarded = new Map<string, number>();
    for (const match of log.matchAll(/\] ➡️\s+(\S+) (\S+)/g)) {
      const name = key(match[1], match[2].split("?")[0]);
      forwarded.set(name, (forwarded.get(name) ?? 0) + 1);
    }
    expect(forwarded.size, "the proxy log lists the requests it received").toBeGreaterThan(0);

    await expect
      .poll(
        async () => {
          const reached = new Map<string, number>();
          for (const entry of await hostRequests()) {
            if (!entry.path.startsWith("/api/")) continue;
            const name = key(entry.method, entry.path);
            reached.set(name, (reached.get(name) ?? 0) + 1);
          }
          const missing = [...forwarded].filter(([name, count]) => (reached.get(name) ?? 0) < count).map(([name]) => name);
          // The Next server also reads the API itself while it renders a page, with the same fixed origin. A browser
          // never does (each test checks that the browser only contacts the web origin). A change that reaches the
          // host without the proxy would be a bypass, so only reads may be unexplained.
          const unexplained = [...reached].filter(([name, count]) => (forwarded.get(name) ?? 0) < count).map(([name]) => name);
          return { missing, bypass: unexplained.filter((name) => !name.startsWith("GET ")) };
        },
        { timeout: 10_000, message: "the proxy log and the host log must list the same API requests" },
      )
      .toEqual({ missing: [], bypass: [] });
  });

  test("the fake provider served every provider call and the refused host was never contacted", async () => {
    const info = await hostInfo();
    expect(info.unfakedProviderCalls).toBe(0);
    const hosts = new Set((await providerCalls()).map((call) => call.host));
    expect(hosts.size).toBeGreaterThan(0);
    expect([...hosts].filter((host) => !KNOWN_PROVIDER_HOSTS.includes(host))).toEqual([]);
    expect(hosts.has("gitlab.corp-blocked.test")).toBe(false);
  });

  test("the temporary database is inside the run directory and holds no canary", async () => {
    const info = await hostInfo();
    expect(info.databasePath).not.toBeNull();
    const database = path.resolve(info.databasePath!);
    expect(database.startsWith(`${path.resolve(API_WORK_DIR)}${path.sep}`)).toBe(true);
    for (const suffix of ["", "-wal", "-shm"]) {
      const file = `${database}${suffix}`;
      if (fs.existsSync(file)) expect(fileContains(file, CANARY)).toBe(false);
    }
  });

  test("no server log, trace, screenshot or report of the run holds the canary token", async () => {
    const directories = [RUN_DIR, path.join(API_WORK_DIR, "logs"), path.resolve("test-results"), path.resolve("playwright-report")];
    expect(fs.existsSync(API_LOG)).toBe(true);
    expect(directories.flatMap((directory) => walkFiles(directory)).length).toBeGreaterThan(0);
    expect(filesContaining(directories, CANARY)).toEqual([]);
  });
});
