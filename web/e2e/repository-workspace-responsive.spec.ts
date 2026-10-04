import type { Locator, Page } from "@playwright/test";

import {
  apiAs,
  connectRepositoryViaApi,
  createConnectionViaApi,
  login,
  providerCalls,
  tokenFor,
  uniqueAccount,
  type ConnectionDto,
  type Session,
} from "./support/api";
import { USERS } from "./support/env";
import { expect, reviewShot, test } from "./support/fixtures";
import { WorkspacePage } from "./support/workspace";

const WIDTHS = { wide: 1440, medium: 900, narrow: 390 } as const;

let alice: Session;

test.beforeAll(async () => {
  alice = await login(USERS.alice);
});

async function prepareIndexedRepository(): Promise<{ connection: ConnectionDto; account: string; providerRepositoryId: string }> {
  const account = uniqueAccount("resp");
  const connection = await createConnectionViaApi(alice, account, { displayName: `Layout ${account}` });
  await connectRepositoryViaApi(alice, connection, 1, ["main", "develop"]);
  // The fake repository ids are stable: account index * 1000 + number. Read the id from the catalog instead of computing it.
  const api = await apiAs(alice);
  try {
    const page = await api.get(`/api/v1/git-connections/${connection.id}/repositories?pageSize=10`);
    const body = (await page.json()) as { data: { items: { providerRepositoryId: string; name: string }[] } };
    const remote = body.data.items.find((item) => item.name === "svc-001")!;
    return { connection, account, providerRepositoryId: remote.providerRepositoryId };
  } finally {
    await api.dispose();
  }
}

/** True when neither the document nor any pane scrolls sideways. */
async function expectNoHorizontalOverflow(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => {
    const doc = document.documentElement;
    const offenders: string[] = [];
    if (doc.scrollWidth > window.innerWidth) offenders.push(`document ${doc.scrollWidth} > ${window.innerWidth}`);
    if (document.body.scrollWidth > window.innerWidth) offenders.push(`body ${document.body.scrollWidth} > ${window.innerWidth}`);
    for (const element of document.querySelectorAll<HTMLElement>('[data-slot="tabs-content"]')) {
      if (element.hidden) continue;
      if (element.scrollWidth > element.clientWidth + 1) {
        offenders.push(`pane ${element.id || element.className.slice(0, 30)} ${element.scrollWidth} > ${element.clientWidth}`);
      }
    }
    return offenders;
  });
  expect(overflow).toEqual([]);
}

async function box(locator: Locator) {
  const result = await locator.boundingBox();
  expect(result, "the element must be laid out").not.toBeNull();
  return result!;
}

test.describe("layout by width", () => {
  test("1440px shows connections, repositories and the plan as three columns", async ({ openAs }) => {
    const { connection, providerRepositoryId } = await prepareIndexedRepository();
    const { page } = await openAs("alice", { viewport: { width: WIDTHS.wide, height: 900 } });
    const workspace = new WorkspacePage(page);
    await workspace.goto(`?connection=${connection.id}&repo=${providerRepositoryId}`);

    await expect(page.getByRole("tablist")).toHaveCount(0);
    const connections = page.locator('section[aria-labelledby="connections-heading"]');
    const catalog = page.locator('section[aria-labelledby="catalog-heading"]');
    const plan = page.locator('section[aria-labelledby="managed-heading"]');
    await expect(workspace.managedBranch("main")).toBeVisible({ timeout: 30_000 });
    await expect(catalog).toBeVisible();

    const [first, second, third] = [await box(connections), await box(catalog), await box(plan)];
    expect(first.x + first.width).toBeLessThanOrEqual(second.x + 1);
    expect(second.x + second.width).toBeLessThanOrEqual(third.x + 1);
    expect(Math.abs(first.y - second.y)).toBeLessThan(8);
    expect(Math.abs(second.y - third.y)).toBeLessThan(8);
    await expectNoHorizontalOverflow(page);
    await reviewShot(page, "layout-1440");
  });

  for (const [label, width] of [
    ["900px", WIDTHS.medium],
    ["390px", WIDTHS.narrow],
  ] as const) {
    test(`${label} shows three tabs, opens on the plan named by the URL and loses no state`, async ({ openAs }) => {
      const { connection, providerRepositoryId } = await prepareIndexedRepository();
      const { page } = await openAs("alice", { viewport: { width, height: 800 } });
      const workspace = new WorkspacePage(page);
      await workspace.goto(`?connection=${connection.id}&repo=${providerRepositoryId}`);

      const tabs = page.getByRole("tab");
      await expect(tabs).toHaveText(["1. Connections", "2. Repositories", "3. Plan"]);
      // The URL names a repository, so the plan opens first. The summary keeps the selection visible.
      await expect(page.getByRole("tab", { name: "3. Plan" })).toHaveAttribute("aria-selected", "true");
      const summary = page.getByRole("region", { name: "Current selection" });
      await expect(summary).toContainText(connection.displayName);
      await expect(summary).toContainText("svc-001");
      await expect(workspace.managedBranch("main")).toBeVisible({ timeout: 30_000 });
      await expectNoHorizontalOverflow(page);
      await reviewShot(page, `layout-${width}-plan`);

      await page.getByRole("tab", { name: "2. Repositories" }).click();
      await expect(workspace.catalogRows).toHaveCount(8);
      await expect(workspace.repository("svc-001")).toHaveAttribute("aria-current", "true");
      await expectNoHorizontalOverflow(page);
      await reviewShot(page, `layout-${width}-repositories`);

      await page.getByRole("tab", { name: "1. Connections" }).click();
      await expect(workspace.connection(connection.displayName)).toHaveAttribute("aria-current", "true");
      await expectNoHorizontalOverflow(page);
      await reviewShot(page, `layout-${width}-connections`);

      // Only the selected step is shown. The others are hidden but stay mounted.
      await expect(page.getByRole("tabpanel")).toHaveCount(1);
    });
  }

  test("a URL that names only a connection opens the repositories step on a narrow screen", async ({ openAs }) => {
    const { connection } = await prepareIndexedRepository();
    const { page } = await openAs("alice", { viewport: { width: WIDTHS.narrow, height: 800 } });
    const workspace = new WorkspacePage(page);
    await workspace.goto(`?connection=${connection.id}`);
    await expect(page.getByRole("tab", { name: "2. Repositories" })).toHaveAttribute("aria-selected", "true");
    await expect(workspace.catalogRows).toHaveCount(8);
  });

  test("crossing 950px keeps the catalog and an open dialog without loading again", async ({ openAs }) => {
    const { connection, account } = await prepareIndexedRepository();
    const { page } = await openAs("alice", { viewport: { width: WIDTHS.wide, height: 900 } });
    const workspace = new WorkspacePage(page);
    await workspace.goto(`?connection=${connection.id}`);
    await expect(workspace.catalogRows).toHaveCount(8);

    await workspace.addConnectionButton.click();
    await workspace.dialog.getByLabel("Display name").fill("Typed before resize");
    const catalogCalls = async () =>
      (await providerCalls()).filter((call) => call.account === account && call.path.endsWith("/repos")).length;
    const before = await catalogCalls();

    await page.setViewportSize({ width: WIDTHS.medium, height: 900 });
    await expect(page.getByRole("tablist")).toBeVisible();
    await expect(workspace.dialog).toBeVisible();
    await expect(workspace.dialog.getByLabel("Display name")).toHaveValue("Typed before resize");

    await page.setViewportSize({ width: WIDTHS.wide, height: 900 });
    await expect(page.getByRole("tablist")).toHaveCount(0);
    await expect(workspace.dialog).toBeVisible();
    await expect(workspace.dialog.getByLabel("Display name")).toHaveValue("Typed before resize");
    await workspace.dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(workspace.catalogRows).toHaveCount(8);
    await page.waitForTimeout(500);
    expect(await catalogCalls()).toBe(before);
  });
});

test.describe("keyboard only", () => {
  async function tabTo(page: Page, target: Locator, limit = 150): Promise<void> {
    for (let presses = 0; presses < limit; presses++) {
      if (await target.evaluate((element) => element === document.activeElement).catch(() => false)) return;
      await page.keyboard.press("Tab");
    }
    throw new Error("The control was not reachable with the Tab key.");
  }

  test("connect, select, plan, manage and remove work with the keyboard and keep focus in order", async ({ openAs }) => {
    const account = uniqueAccount("kbd");
    const { page } = await openAs("alice", { viewport: { width: WIDTHS.wide, height: 900 } });
    const workspace = new WorkspacePage(page);
    await workspace.goto();

    // The dialog opens from the keyboard and Escape returns the focus to the control that opened it.
    await tabTo(page, workspace.addConnectionButton);
    await page.keyboard.press("Enter");
    await expect(workspace.dialog).toBeVisible();
    await expect(workspace.dialog.getByLabel("Provider")).toBeFocused();
    await page.keyboard.press("Escape");
    await expect(workspace.dialog).toHaveCount(0);
    await expect(workspace.addConnectionButton).toBeFocused();

    // A rejected token moves the focus to the alert and the token field is empty again.
    await page.keyboard.press("Enter");
    await expect(workspace.dialog.getByLabel("Provider")).toBeFocused();
    await page.keyboard.press("Tab");
    await page.keyboard.type(`Keyboard ${account}`);
    await page.keyboard.press("Tab");
    await page.keyboard.type(`e2e-${account}-revoked0001`);
    await page.keyboard.press("Enter");
    await expect(workspace.dialog.getByRole("alert")).toBeFocused();
    await expect(workspace.dialog.getByLabel("Access token")).toHaveValue("");

    // A valid token saves the connection. The live status announces it and the focus returns to the opener.
    await workspace.dialog.getByLabel("Access token").focus();
    await page.keyboard.type(tokenFor(account));
    await page.keyboard.press("Enter");
    await expect(workspace.dialog).toHaveCount(0);
    await expect(page.getByRole("status").filter({ hasText: "The connection is saved." })).toBeVisible();
    await expect(workspace.addConnectionButton).toBeFocused();
    await expect(workspace.catalogRows).toHaveCount(8);

    await tabTo(page, workspace.repository("svc-001"));
    await page.keyboard.press("Enter");
    await expect(page.getByRole("heading", { name: `${account}/svc-001` })).toBeVisible();

    await tabTo(page, workspace.branchCheckbox("main"));
    await page.keyboard.press("Space");
    await expect(workspace.branchCheckbox("main")).toBeChecked();
    await expect(page.getByText("1 selected")).toBeVisible();
    await tabTo(page, page.getByRole("button", { name: "Index selected branches" }));
    await page.keyboard.press("Enter");
    await expect(page.getByRole("status").filter({ hasText: "1 queued, 0 already indexed." })).toBeVisible();

    await tabTo(page, page.getByRole("button", { name: "Manage indexed branches" }));
    await page.keyboard.press("Enter");
    await expect(workspace.managedBranch("main")).toBeVisible();
    await expect(workspace.managedBranch("main")).toContainText("Completed", { timeout: 60_000 });

    // The confirmation dialog traps the focus, closes with Escape and returns to the Remove button.
    const remove = workspace.managedBranch("main").getByRole("button", { name: "Remove" });
    await tabTo(page, remove);
    await page.keyboard.press("Enter");
    const confirmation = page.getByRole("alertdialog");
    await expect(confirmation).toBeVisible();
    await page.keyboard.press("Tab");
    await expect(confirmation.locator(":focus")).toHaveCount(1);
    await page.keyboard.press("Escape");
    await expect(confirmation).toHaveCount(0);
    await expect(remove).toBeFocused();
    await expect(workspace.managedBranch("main")).toBeVisible();
  });
});
