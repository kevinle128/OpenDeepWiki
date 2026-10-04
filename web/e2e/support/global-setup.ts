import fs from "node:fs";

import { request as playwrightRequest } from "@playwright/test";

import { ensureUser, login } from "./api";
import { AUTH_DIR, WEB_URL, USERS, statePath, type UserKey } from "./env";

const TOKEN_KEY = "auth_token";
const TOKEN_COOKIE = "deepwiki_token";

async function waitForSeededAdmin(): Promise<void> {
  // The health route answers before the initializer has created the first administrator.
  const deadline = Date.now() + 120_000;
  let lastError = "";
  while (Date.now() < deadline) {
    const context = await playwrightRequest.newContext({ baseURL: WEB_URL });
    try {
      const response = await context.post("/api/auth/login", {
        data: { email: USERS.admin.email, password: USERS.admin.password },
      });
      if (response.ok()) return;
      lastError = `HTTP ${response.status()}`;
    } catch (error) {
      lastError = (error as Error).message;
    } finally {
      await context.dispose();
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`The seeded administrator could not sign in through the web proxy (${lastError}).`);
}

/** Stores a signed-in browser state: the token in local storage and the cookie, like the web client does. */
async function writeState(key: UserKey): Promise<void> {
  const session = await login(USERS[key]);
  const host = new URL(WEB_URL).hostname;
  const state = {
    cookies: [
      {
        name: TOKEN_COOKIE,
        value: session.token,
        domain: host,
        path: "/",
        expires: Math.floor(Date.now() / 1000) + 3600 * 12,
        httpOnly: false,
        secure: false,
        sameSite: "Lax" as const,
      },
    ],
    origins: [{ origin: WEB_URL, localStorage: [{ name: TOKEN_KEY, value: session.token }] }],
  };
  fs.writeFileSync(statePath(key), JSON.stringify(state), { mode: 0o600 });
}

export default async function globalSetup(): Promise<void> {
  fs.mkdirSync(AUTH_DIR, { recursive: true, mode: 0o700 });
  await waitForSeededAdmin();
  await ensureUser(USERS.alice);
  await ensureUser(USERS.bob);
  for (const key of Object.keys(USERS) as UserKey[]) {
    await writeState(key);
  }
}
