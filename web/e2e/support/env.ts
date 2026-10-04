import os from "node:os";
import path from "node:path";

/** Fixed ports of the end-to-end stack. A busy port stops the run; nothing picks another port. */
export const WEB_PORT = 4310;
export const API_PORT = 4311;
export const WEB_URL = `http://127.0.0.1:${WEB_PORT}`;
/** The origin that the web proxy must forward to. It is the only upstream the browser traffic may reach. */
export const API_PROXY_URL = `http://localhost:${API_PORT}`;
export const API_URL = API_PROXY_URL;

/**
 * Everything the run writes lives under one base directory. `E2E_BASE_DIR` moves it, for example into a
 * scratch directory. The names are fixed so a stale run can be found and removed.
 */
export const BASE_DIR = process.env.E2E_BASE_DIR ?? os.tmpdir();
/** The API host deletes and recreates this directory at start and removes it at shutdown. */
export const API_WORK_DIR = path.join(BASE_DIR, `opendeepwiki-e2e-${API_PORT}`);
/** Server logs and session files. Wiped at the start of each run. */
export const RUN_DIR = path.join(BASE_DIR, `opendeepwiki-e2e-run-${WEB_PORT}`);
export const API_LOG = path.join(RUN_DIR, "api.log");
export const WEB_LOG = path.join(RUN_DIR, "web.log");
export const AUTH_DIR = path.join(RUN_DIR, "auth");
/** Screenshots for manual review. Only written when `E2E_SCREENSHOT_DIR` is set. */
export const SCREENSHOT_DIR = process.env.E2E_SCREENSHOT_DIR ?? "";

/**
 * A fake token that is unique per run. The fake provider accepts `e2e-{account}-{suffix}` and no other form,
 * so the canary behaves like a real token while no real token ever exists. Workers inherit the value from the
 * runner process, which evaluates the configuration first.
 */
process.env.E2E_CANARY ??= `e2e-canary-${Math.random().toString(36).slice(2, 10)}${Date.now().toString(36)}`;
export const CANARY = process.env.E2E_CANARY;

export const PASSWORD = "E2e-Pass-123!";

export type UserKey = "alice" | "bob" | "admin";

export interface TestUser {
  key: UserKey;
  name: string;
  email: string;
  password: string;
}

export const USERS: Record<UserKey, TestUser> = {
  alice: { key: "alice", name: "e2ealice", email: "alice@e2e.test", password: PASSWORD },
  bob: { key: "bob", name: "e2ebob", email: "bob@e2e.test", password: PASSWORD },
  // Created by the application at first start.
  admin: { key: "admin", name: "admin", email: "admin@routin.ai", password: "Admin@123" },
};

export const statePath = (user: UserKey) => path.join(AUTH_DIR, `${user}.json`);
