import fs from "node:fs";
import path from "node:path";

import type { BrowserContext, Page, Request } from "@playwright/test";

import { apiAs, hostInfo, hostRequests, login, providerCalls, setProviderRules, type Session } from "./support/api";
import { API_LOG, API_WORK_DIR, CANARY, RUN_DIR, USERS, WEB_LOG } from "./support/env";
import { expect, test, type Opened } from "./support/fixtures";
import { fileContains, filesContaining } from "./support/scan";
import { WorkspacePage } from "./support/workspace";

/**
 * BLOCKING GATE. A canary token goes through the create, error, edit and rotate flows. It may appear in exactly one
 * place: the body of the request that sends it. Nowhere else, in no form the browser, the proxy, the host, the
 * database, the logs or a Playwright trace can hold.
 *
 * Playwright's own tracing is off for this file. The test starts and stops tracing itself and stops it before a token
 * is typed, because a trace records the values that a test types.
 */
test.use({ trace: "off", screenshot: "off", video: "off" });

const ROTATED = `${CANARY}-rotated`;
const REVOKED = `${CANARY}-revoked`;
const DISPLAY_NAME = `Canary ${CANARY.slice(-6)}`;

let alice: Session;
test.beforeAll(async () => {
  alice = await login(USERS.alice);
});
test.afterEach(async () => {
  await setProviderRules([]);
});

interface Trace {
  context: BrowserContext;
  files: string[];
  start(): Promise<void>;
  stop(label: string): Promise<void>;
}

function createTrace(context: BrowserContext, directory: string): Trace {
  const files: string[] = [];
  return {
    context,
    files,
    start: () => context.tracing.start({ screenshots: true, snapshots: true }),
    async stop(label) {
      const file = path.join(directory, `${label}.zip`);
      await context.tracing.stop({ path: file });
      files.push(file);
    },
  };
}

/** Everything the browser side holds right now. A leak in any of these fails the gate. */
async function browserSurfaces(opened: Opened): Promise<Record<string, string>> {
  const { page, context } = opened;
  const inPage = await page.evaluate(() => ({
    text: document.body.innerText,
    inputs: [...document.querySelectorAll("input,textarea")].map((element) => {
      const field = element as HTMLInputElement;
      return `${field.name}|${field.id}|${field.type}|${field.value}|${field.getAttribute("value") ?? ""}`;
    }),
    local: JSON.stringify(Object.entries(localStorage)),
    session: JSON.stringify(Object.entries(sessionStorage)),
    cookie: document.cookie,
    resources: performance.getEntriesByType("resource").map((entry) => entry.name),
    history: String(history.length) + location.href,
  }));
  return {
    html: await page.content(),
    text: inPage.text,
    inputs: inPage.inputs.join("\n"),
    localStorage: inPage.local,
    sessionStorage: inPage.session,
    documentCookie: inPage.cookie,
    contextCookies: JSON.stringify(await context.cookies()),
    contextStorage: JSON.stringify(await context.storageState()),
    resourceUrls: inPage.resources.join("\n"),
    url: page.url() + inPage.history,
    console: opened.consoleLines.join("\n"),
    responseBodies: opened.bodies.map((body) => `${body.url}\n${body.text}`).join("\n"),
  };
}

async function expectNoCanaryAnywhere(opened: Opened, label: string): Promise<void> {
  for (const [surface, text] of Object.entries(await browserSurfaces(opened))) {
    expect(text.includes(CANARY), `${label}: the canary token is in the browser's ${surface}`).toBe(false);
  }
}

/** Server side surfaces: host request log, provider call log, API payloads, the database and every log file. */
async function expectNoCanaryOnServers(label: string, connectionId: string): Promise<void> {
  expect(JSON.stringify(await hostRequests()).includes(CANARY), `${label}: host request log`).toBe(false);
  expect(JSON.stringify(await providerCalls()).includes(CANARY), `${label}: provider call log`).toBe(false);

  for (const session of [alice, await login(USERS.admin)]) {
    const api = await apiAs(session);
    try {
      for (const route of [
        "/api/v1/git-connections",
        `/api/v1/git-connections/${connectionId}`,
        `/api/v1/git-connections/${connectionId}/audit-events?limit=100`,
      ]) {
        const response = await api.get(route);
        expect((await response.text()).includes(CANARY), `${label}: response of ${route}`).toBe(false);
      }
    } finally {
      await api.dispose();
    }
  }

  const info = await hostInfo();
  expect(info.databasePath, "the host must report its database file").not.toBeNull();
  for (const suffix of ["", "-wal", "-shm"]) {
    const file = `${info.databasePath}${suffix}`;
    if (fs.existsSync(file)) expect(fileContains(file, CANARY), `${label}: database file ${path.basename(file)}`).toBe(false);
  }

  const logs = [API_LOG, WEB_LOG].filter((file) => fs.existsSync(file));
  for (const file of logs) expect(fileContains(file, CANARY), `${label}: log ${path.basename(file)}`).toBe(false);
  expect(filesContaining([path.join(API_WORK_DIR, "logs"), path.join(RUN_DIR, "auth")], CANARY), `${label}: log files`).toEqual([]);
}

test("a canary token never appears outside the request that sends it", async ({ openAs }, testInfo) => {
  const traceDirectory = fs.mkdtempSync(path.join(RUN_DIR, "secret-traces-"));
  const opened = await openAs("alice");
  opened.captureBodies();
  const { page, context } = opened;
  const trace = createTrace(context, traceDirectory);
  const workspace = new WorkspacePage(page);

  // Positive control: the request that carries the token really contains it, so the searches below can fail.
  const sent: string[] = [];
  page.on("request", (request: Request) => {
    const body = request.postData();
    if (body && request.url().includes("/api/v1/git-connections")) sent.push(body);
  });

  let connectionId = "";
  try {
    await test.step("create: the token is typed and sent with tracing stopped", async () => {
      await workspace.goto();
      await trace.start();
      await workspace.addConnectionButton.click();
      await trace.stop("before-token-1");

      await workspace.dialog.getByLabel("Display name").fill(DISPLAY_NAME);
      const token = workspace.dialog.getByLabel("Access token");
      await expect(token).toHaveAttribute("type", "password");
      await token.fill(CANARY);
      await workspace.dialog.getByRole("button", { name: "Save connection" }).click();
      await expect(workspace.dialog).toHaveCount(0);

      await trace.start();
      await expect(workspace.connection(DISPLAY_NAME)).toHaveAttribute("aria-current", "true");
      expect(sent.some((body) => body.includes(CANARY)), "the create request must carry the token").toBe(true);
      connectionId = new URL(page.url()).searchParams.get("connection") ?? "";
      expect(connectionId).not.toBe("");
      await expectNoCanaryAnywhere(opened, "after create");
    });

    await test.step("browse: catalog and plan load with the connection", async () => {
      await expect(workspace.catalogRows.first()).toBeVisible();
      await workspace.repository("svc-001").click();
      await expect(page.getByRole("checkbox", { name: /^main/ })).toBeVisible();
      await expectNoCanaryAnywhere(opened, "after browsing");
    });

    await test.step("edit: the dialog never shows the token again", async () => {
      await page.getByRole("button", { name: "Edit", exact: true }).click();
      await expect(workspace.dialog.getByLabel("New token (optional)")).toHaveValue("");
      await expect(workspace.dialog.getByText("A token is saved.", { exact: false })).toBeVisible();
      await expectNoCanaryAnywhere(opened, "edit dialog open");
      await workspace.dialog.getByRole("button", { name: "Cancel" }).click();
    });

    await test.step("rotate: a replacement token is typed and sent with tracing stopped", async () => {
      await trace.stop("before-token-2");
      await page.getByRole("button", { name: "Edit", exact: true }).click();
      const field = workspace.dialog.getByLabel("New token (optional)");
      await field.fill(ROTATED);
      await workspace.dialog.getByRole("button", { name: "Save connection" }).click();
      await expect(workspace.dialog).toHaveCount(0);
      await expect(page.getByRole("status").filter({ hasText: "The connection is saved." })).toBeVisible();
      await trace.start();
      await expectNoCanaryAnywhere(opened, "after rotate");
    });

    await test.step("error: a rejected token shows a stable message and clears the field", async () => {
      await trace.stop("before-token-3");
      await workspace.addConnectionButton.click();
      await workspace.dialog.getByLabel("Display name").fill("Rejected canary");
      await workspace.dialog.getByLabel("Access token").fill(REVOKED);
      await workspace.dialog.getByRole("button", { name: "Save connection" }).click();
      const alert = workspace.dialog.getByRole("alert");
      await expect(alert).toHaveText("The provider rejected the token. Enter a valid token.");
      await expect(workspace.dialog.getByLabel("Access token")).toHaveValue("");
      await trace.start();
      await expectNoCanaryAnywhere(opened, "after a rejected token");
      await workspace.dialog.getByRole("button", { name: "Cancel" }).click();
    });

    await test.step("error: provider failures of the canary connection show stable messages", async () => {
      await setProviderRules([{ account: "canary", op: "*", status: 503 }]);
      await page.reload();
      await expect(page.getByRole("alert").filter({ hasText: "The repositories could not be loaded." })).toContainText(
        "The provider request failed. Try again later.",
      );
      await expectNoCanaryAnywhere(opened, "after a provider failure");
      await setProviderRules([{ account: "canary", op: "*", status: 429, retryAfterSeconds: 30 }]);
      await page.getByRole("button", { name: "Try again" }).click();
      await expect(page.getByRole("alert").filter({ hasText: "The repositories could not be loaded." })).toContainText(
        "The provider rate limit is reached. Try again later.",
      );
      await expectNoCanaryAnywhere(opened, "after a rate limit");
      await setProviderRules([]);
    });

    await test.step("servers: the token is in no response, log, database file or audit event", async () => {
      await expectNoCanaryOnServers("after all flows", connectionId);
    });

    await test.step("trace policy: the traces recorded around the token flows hold no canary", async () => {
      await trace.stop("final");
      expect(trace.files.length).toBeGreaterThanOrEqual(4);
      for (const file of trace.files) {
        expect(fileContains(file, CANARY), `trace ${path.basename(file)}`).toBe(false);
      }
    });

    await test.step("trace control: a trace of typed text holds it, so the search above can detect a leak", async () => {
      const control = await openAs("alice");
      const controlTrace = createTrace(control.context, traceDirectory);
      const controlWorkspace = new WorkspacePage(control.page);
      await controlWorkspace.goto();
      await controlTrace.start();
      await controlWorkspace.addConnectionButton.click();
      const typedControl = `control-${CANARY.slice(-8)}`;
      await controlWorkspace.dialog.getByLabel("Display name").fill(typedControl);
      await controlTrace.stop("control");
      expect(fileContains(controlTrace.files[0], typedControl), "the scanner must see text that a trace recorded").toBe(true);
    });
  } finally {
    // No trace of this test is kept, not even the clean ones: they list private repository names.
    fs.rmSync(traceDirectory, { recursive: true, force: true });
  }

  // Nothing else may be left behind by this test (no screenshot, no video, no trace).
  expect(fs.existsSync(testInfo.outputDir) ? fs.readdirSync(testInfo.outputDir) : []).toEqual([]);
  expect(filesContaining([testInfo.project.outputDir], CANARY)).toEqual([]);
});

test("the token field of the create dialog is a password field and stays empty after a failure", async ({ openAs }) => {
  const opened = await openAs("alice");
  const workspace = new WorkspacePage(opened.page);
  await workspace.goto();
  await workspace.addConnectionButton.click();
  const token = workspace.dialog.getByLabel("Access token");
  await expect(token).toHaveAttribute("type", "password");
  await expect(token).toHaveAttribute("autocomplete", "new-password");
  // An invalid server address fails before any request. The form keeps its other values but never the token.
  await workspace.dialog.getByLabel("Provider").selectOption({ label: "GitLab self-hosted" });
  await workspace.dialog.getByLabel("Server URL").fill("http://insecure.test");
  await workspace.dialog.getByLabel("Display name").fill("Plain HTTP");
  await token.fill(`e2e-secretless-${Date.now().toString(36)}`);
  await workspace.dialog.getByRole("button", { name: "Save connection" }).click();
  await expect(workspace.dialog.getByText("Enter an HTTPS address.")).toBeVisible();
  const page: Page = opened.page;
  expect(await page.evaluate(() => JSON.stringify(Object.entries(localStorage)))).not.toContain("secretless");
  expect(page.url()).not.toContain("secretless");
});
