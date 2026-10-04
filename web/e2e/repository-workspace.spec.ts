import type { Page } from "@playwright/test";

import {
  apiAs,
  createConnectionViaApi,
  hostInfo,
  json,
  login,
  providerCalls,
  setHold,
  setPause,
  setProviderRules,
  tokenFor,
  uniqueAccount,
  type Session,
} from "./support/api";
import { USERS } from "./support/env";
import { expect, test } from "./support/fixtures";
import { WorkspacePage } from "./support/workspace";

let alice: Session;

test.beforeAll(async () => {
  alice = await login(USERS.alice);
});

test.afterEach(async () => {
  // Rules and the hold switch are shared state of the host. Every test starts from the defaults.
  await setProviderRules([]);
  await setHold(false);
  await setPause(false);
});

/** Selects the first repository of the open catalog, picks the branches and submits the plan. */
async function indexRepository(workspace: WorkspacePage, repository: string, branches: string[]): Promise<void> {
  await workspace.repository(repository).click();
  await workspace.indexBranches(branches);
  await expect(workspace.page.getByText(`${branches.length} queued, 0 already indexed.`)).toBeVisible();
}

async function expectBranchesCompleted(page: Page, workspace: WorkspacePage, branches: string[]): Promise<void> {
  await workspace.openManagedBranches();
  for (const branch of branches) {
    await expect(workspace.managedBranch(branch)).toContainText("Completed", { timeout: 60_000 });
  }
  await expect(page.getByRole("list", { name: "Indexed branches" }).getByRole("listitem")).toHaveCount(branches.length);
}

test.describe("connect, browse and index", () => {
  test("GitHub: a user pages the catalog, selects branches and the tasks start independently", async ({ openAs }) => {
    const account = uniqueAccount("many");
    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();

    await workspace.addConnection({ provider: "GitHub", displayName: `GitHub ${account}`, token: tokenFor(account) });

    await expect(workspace.connection(`GitHub ${account}`)).toHaveAttribute("aria-current", "true");
    // 120 fake repositories, 50 per page: the first page, then two more.
    await expect(workspace.catalogRows).toHaveCount(50);
    // Search must reach a repository on an unloaded provider page.
    await page.getByLabel("Search repositories").fill("svc-120");
    await expect(workspace.catalogRows).toHaveCount(1);
    await expect(workspace.catalogRows.first()).toContainText("svc-120");
    await page.getByLabel("Search repositories").fill("");
    await expect(workspace.catalogRows).toHaveCount(50);
    await page.getByRole("button", { name: "Load more repositories" }).click();
    await expect(workspace.catalogRows).toHaveCount(100);
    await page.getByRole("button", { name: "Load more repositories" }).click();
    await expect(workspace.catalogRows).toHaveCount(120);
    await expect(page.getByRole("button", { name: "Load more repositories" })).toHaveCount(0);

    await setHold(true);
    await workspace.repository("svc-001").click();
    await expect(page).toHaveURL(/repo=/);
    await workspace.indexBranches(["main", "feature/hold"]);
    await expect(page.getByText("2 queued, 0 already indexed.")).toBeVisible();
    await workspace.openManagedBranches();

    // One branch is held in Processing. The other finishes anyway, so the jobs do not wait for each other.
    await expect(workspace.managedBranch("main")).toContainText("Completed", { timeout: 60_000 });
    await expect(workspace.managedBranch("feature/hold")).toContainText("Processing", { timeout: 60_000 });
    await setHold(false);
    await expect(workspace.managedBranch("feature/hold")).toContainText("Completed", { timeout: 60_000 });
  });

  test("GitLab.com: the same flow works and a rejected token shows a safe error", async ({ openAs }) => {
    const account = uniqueAccount("gl");
    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();

    await workspace.addConnection({ provider: "GitLab.com", displayName: `GitLab ${account}`, token: tokenFor(account) });
    await expect(workspace.connection(`GitLab ${account}`)).toHaveAttribute("aria-current", "true");
    await expect(workspace.catalogRows).toHaveCount(8);

    // GitLab marks the default branch, so it is preselected.
    await workspace.repository("svc-002").click();
    await expect(workspace.branchCheckbox("main")).toBeChecked();
    await workspace.indexBranches(["develop"]);
    await expect(page.getByText("2 queued, 0 already indexed.")).toBeVisible();
    await expectBranchesCompleted(page, workspace, ["main", "develop"]);

    // A second connection with a token that the provider rejects keeps the user in the dialog with a stable message.
    await workspace.addConnectionButton.click();
    await workspace.fillConnectionDialog({
      provider: "GitLab.com",
      displayName: "Rejected",
      token: `e2e-${account}-revoked1234`,
    });
    await workspace.dialog.getByRole("button", { name: "Save connection" }).click();
    const alert = workspace.dialog.getByRole("alert");
    await expect(alert).toHaveText("The provider rejected the token. Enter a valid token.");
    await expect(alert).toBeFocused();
    await expect(workspace.dialog.getByLabel("Access token")).toHaveValue("");
    // A rejected provider token is not a lost session: the user stays signed in.
    await expect(page).toHaveURL(/\/repositories/);
  });

  test("self-hosted GitLab on a public HTTPS host works without an operator allowlist", async ({ openAs }) => {
    const account = uniqueAccount("sh");
    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();

    await workspace.addConnection({
      provider: "GitLab self-hosted",
      serverUrl: "https://git.public-selfhosted.test",
      displayName: `Self ${account}`,
      token: tokenFor(account),
    });
    await expect(workspace.connection(`Self ${account}`)).toContainText("Self-hosted");
    await expect(workspace.catalogRows).toHaveCount(8);
    await indexRepository(workspace, "svc-003", ["main"]);
    await expectBranchesCompleted(page, workspace, ["main"]);

    // The dialog refuses an address that is not HTTPS before any request is made.
    await workspace.addConnectionButton.click();
    await workspace.fillConnectionDialog({
      provider: "GitLab self-hosted",
      serverUrl: "http://git.public-selfhosted.test",
      displayName: "Plain HTTP",
      token: tokenFor(account),
    });
    await workspace.dialog.getByRole("button", { name: "Save connection" }).click();
    await expect(workspace.dialog.getByText("Enter an HTTPS address.")).toBeVisible();
  });

  test("self-hosted GitLab on a private network works only for an allowlisted host", async ({ openAs }) => {
    const account = uniqueAccount("pv");
    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();

    await workspace.addConnection({
      provider: "GitLab self-hosted",
      serverUrl: "https://gitlab.corp-allowed.test",
      displayName: `Corp ${account}`,
      token: tokenFor(account),
    });
    await expect(workspace.connection(`Corp ${account}`)).toHaveAttribute("aria-current", "true");
    await expect(workspace.catalogRows).toHaveCount(8);
    await indexRepository(workspace, "svc-004", ["main"]);
    await expectBranchesCompleted(page, workspace, ["main"]);

    // The same kind of host that the operator did not allow resolves to a private address and is refused.
    await workspace.addConnectionButton.click();
    await workspace.fillConnectionDialog({
      provider: "GitLab self-hosted",
      serverUrl: "https://gitlab.corp-blocked.test",
      displayName: `Blocked ${account}`,
      token: tokenFor(account),
    });
    await workspace.dialog.getByRole("button", { name: "Save connection" }).click();
    await expect(workspace.dialog.getByRole("alert")).toHaveText("The server address is not allowed.");
  });
});

test.describe("managed branches", () => {
  test("a user adds, syncs, rebuilds, retries, cancels and removes branches while the others stay unchanged", async ({ openAs }) => {
    const account = uniqueAccount("life");
    const connection = await createConnectionViaApi(alice, account);
    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();
    await workspace.connection(connection.displayName).click();
    await indexRepository(workspace, "svc-001", ["main", "feature/fail"]);
    await workspace.openManagedBranches();

    await expect(workspace.managedBranch("main")).toContainText("Completed", { timeout: 60_000 });
    await expect(workspace.managedBranch("feature/fail")).toContainText("Failed", { timeout: 60_000 });
    await expect(workspace.managedBranch("feature/fail")).toContainText("Simulated generation failure.");

    // Add a branch to the indexed repository.
    await page.getByRole("button", { name: "Add branches" }).click();
    await workspace.indexBranches(["develop"], "Add selected branches");
    // The result panel hands over to the list again. The list refreshes and shows the new branch.
    await page.getByRole("button", { name: "Manage indexed branches" }).click();
    await expect(workspace.managedBranch("develop")).toContainText("Completed", { timeout: 60_000 });

    // Sync starts an incremental task. It stays pending because the scheduled worker is off in this host.
    // Only full generation tasks can be cancelled, so a sync task shows no Cancel control.
    await workspace.managedBranch("main").getByRole("button", { name: "Sync" }).click();
    await expect(page.getByText("Sync started for main.")).toBeVisible();
    await expect(workspace.managedBranch("main")).toContainText("Active task: Incremental");
    await expect(workspace.managedBranch("main").getByRole("button", { name: "Cancel" })).toHaveCount(0);

    // A full generation that waits to start can be cancelled. The job dispatcher is paused so the task stays pending.
    await setPause(true);
    await page.getByRole("button", { name: "Add branches" }).click();
    await workspace.indexBranches(["release/1.0"], "Add selected branches");
    await page.getByRole("button", { name: "Manage indexed branches" }).click();
    await expect(workspace.managedBranch("release/1.0")).toContainText("Pending");
    await workspace.managedBranch("release/1.0").getByRole("button", { name: "Cancel" }).click();
    await expect(page.getByText("Cancel requested for release/1.0.")).toBeVisible();
    await expect(workspace.managedBranch("release/1.0")).toContainText("Cancelled");
    await setPause(false);

    // Rebuild runs a full generation again.
    await workspace.managedBranch("develop").getByRole("button", { name: "Rebuild" }).click();
    await expect(page.getByText("Rebuild started for develop.")).toBeVisible();
    await expect(workspace.managedBranch("develop")).toContainText("Completed", { timeout: 60_000 });

    // Retry runs the failed task again. It fails again, with the same stable text.
    await workspace.managedBranch("feature/fail").getByRole("button", { name: "Retry" }).click();
    await expect(page.getByText("Retry started for feature/fail.")).toBeVisible();
    await expect(workspace.managedBranch("feature/fail")).toContainText("Failed", { timeout: 60_000 });

    // Removing one branch leaves the others as they were.
    await workspace.managedBranch("develop").getByRole("button", { name: "Remove" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Remove data only" }).click();
    await expect(page.getByText("develop is removed from the index.")).toBeVisible();
    await expect(workspace.managedBranch("develop")).toHaveCount(0);
    await expect(workspace.managedBranch("main")).toContainText("Completed");
    await expect(workspace.managedBranch("main")).toContainText("Active task: Incremental");
    await expect(workspace.managedBranch("feature/fail")).toContainText("Failed");
    await expect(workspace.managedBranch("release/1.0")).toContainText("Cancelled");
    await expect(page.getByRole("list", { name: "Indexed branches" }).getByRole("listitem")).toHaveCount(3);
  });

  test("removing a branch with a processing job answers 409 and the branch remains", async ({ openAs }) => {
    const account = uniqueAccount("busy");
    const connection = await createConnectionViaApi(alice, account);
    await setHold(true);
    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();
    await workspace.connection(connection.displayName).click();
    await indexRepository(workspace, "svc-001", ["main", "feature/hold"]);
    await workspace.openManagedBranches();
    await expect(workspace.managedBranch("feature/hold")).toContainText("Processing", { timeout: 60_000 });

    await workspace.managedBranch("feature/hold").getByRole("button", { name: "Remove" }).click();
    await page.getByRole("alertdialog").getByRole("button", { name: "Remove data only" }).click();
    const alert = page.getByRole("alert").filter({ hasText: "A job of this branch is running." });
    await expect(alert).toHaveText("A job of this branch is running. Try again when it has finished.");
    await expect(alert).toBeFocused();
    await expect(workspace.managedBranch("feature/hold")).toBeVisible();

    // The same request made directly shows the status code and the stable code.
    const api = await apiAs(alice);
    try {
      const repositoryId = new URL(page.url()).searchParams.get("repo");
      expect(repositoryId).not.toBeNull();
      const list = await api.get(`/api/v1/git-connections/${connection.id}/repositories?pageSize=1`);
      expect(list.status()).toBe(200);
      const local = await api.get(`/api/v1/repositories/list?page=1&pageSize=50&keyword=svc-001`);
      const item = (await json<{ items: { id: string; gitConnectionId: string }[] }>(local)).items.find(
        (entry) => entry.gitConnectionId === connection.id,
      );
      expect(item).toBeDefined();
      const branches = (await json<{ data: { branchId: string; branchName: string }[] }>(
        await api.get(`/api/v1/repositories/${item!.id}/indexed-branches`),
      )).data;
      const busy = branches.find((branch) => branch.branchName === "feature/hold")!;
      const refused = await api.delete(`/api/v1/repositories/${item!.id}/indexed-branches/${busy.branchId}`);
      expect(refused.status()).toBe(409);
      expect(await json<{ errorCode: string }>(refused)).toMatchObject({ errorCode: "BRANCH_JOB_ACTIVE" });

      // Once the job has finished, the same removal works.
      await setHold(false);
      await expect(workspace.managedBranch("feature/hold")).toContainText("Completed", { timeout: 60_000 });
      const removed = await api.delete(`/api/v1/repositories/${item!.id}/indexed-branches/${busy.branchId}`);
      expect(removed.status()).toBe(200);
    } finally {
      await api.dispose();
    }
  });
});

test.describe("provider failures and stale state", () => {
  test("each provider failure shows a stable message and a later selection is not overwritten", async ({ openAs }) => {
    const failing = [
      { account: uniqueAccount("fa"), rule: { status: 401 }, message: "The provider rejected the token. Enter a valid token." },
      {
        account: uniqueAccount("fb"),
        rule: { status: 429, retryAfterSeconds: 30 },
        message: "The provider rate limit is reached. Try again later.",
      },
      { account: uniqueAccount("fc"), rule: { status: 503 }, message: "The provider request failed. Try again later." },
      { account: uniqueAccount("fd"), rule: { timeout: true }, message: "The provider did not answer in time." },
    ];
    const healthy = uniqueAccount("ok");
    const connections = await Promise.all(failing.map((entry) => createConnectionViaApi(alice, entry.account)));
    const healthyConnection = await createConnectionViaApi(alice, healthy);
    await setProviderRules(failing.map((entry) => ({ account: entry.account, op: "repos", ...entry.rule })));

    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();

    for (const [index, entry] of failing.entries()) {
      await workspace.connection(connections[index].displayName).click();
      const alert = page.getByRole("alert").filter({ hasText: "The repositories could not be loaded." });
      await expect(alert).toContainText(entry.message);
      await expect(workspace.catalogRows).toHaveCount(0);
    }

    // The UI does not retry on its own. Each failing account was asked once.
    await page.waitForTimeout(2_000);
    const calls = await providerCalls();
    for (const entry of failing) {
      expect(calls.filter((call) => call.account === entry.account && call.path.endsWith("/repos"))).toHaveLength(1);
    }

    // Selecting a healthy connection replaces the error. The failures that were answered earlier do not come back.
    await workspace.connection(healthyConnection.displayName).click();
    await expect(workspace.catalogRows).toHaveCount(8);
    await expect(page.getByRole("alert").filter({ hasText: "The repositories could not be loaded." })).toHaveCount(0);

    // After the cause is gone, the user can try again.
    await workspace.connection(connections[1].displayName).click();
    await expect(page.getByRole("alert").filter({ hasText: "The repositories could not be loaded." })).toBeVisible();
    await setProviderRules([]);
    await page.getByRole("button", { name: "Try again" }).click();
    await expect(workspace.catalogRows).toHaveCount(8);
  });

  test("a slow answer for a connection that is no longer selected is ignored", async ({ openAs }) => {
    const slow = uniqueAccount("slow");
    const fast = uniqueAccount("fast");
    const slowConnection = await createConnectionViaApi(alice, slow);
    const fastConnection = await createConnectionViaApi(alice, fast);
    await setProviderRules([
      { account: slow, op: "repos", delayMs: 3_000 },
      { account: slow, op: "branches", delayMs: 3_000 },
    ]);

    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();

    // The slow connection is selected first and replaced right away.
    await workspace.connection(slowConnection.displayName).click();
    await workspace.connection(fastConnection.displayName).click();
    await expect(workspace.catalogRows).toHaveCount(8);
    await expect(workspace.catalogRows.first()).toContainText(fast);

    // Wait longer than the slow answer needs. The list must still belong to the selected connection.
    await page.waitForTimeout(4_000);
    await expect(workspace.catalogRows).toHaveCount(8);
    await expect(page.getByRole("list", { name: "Repository catalog" })).not.toContainText(slow);
    await expect(page.getByText("8 loaded")).toBeVisible();

    // The same holds for the plan: a slow branch list of a repository that is no longer selected is dropped.
    await workspace.connection(slowConnection.displayName).click();
    await expect(workspace.catalogRows).toHaveCount(8, { timeout: 10_000 });
    await workspace.repository("svc-001").click();
    await workspace.repository("svc-002").click();
    await expect(page.getByRole("heading", { name: `${slow}/svc-002` })).toBeVisible();
    await page.waitForTimeout(4_000);
    await expect(page.getByRole("heading", { name: `${slow}/svc-002` })).toBeVisible();
    await expect(page.getByRole("heading", { name: `${slow}/svc-001` })).toHaveCount(0);
    await expect(page.getByRole("checkbox", { name: /^main/ })).toBeVisible();
    await expect(page.getByRole("list").filter({ has: page.getByRole("checkbox") }).getByRole("listitem")).toHaveCount(5);
  });
});

test("the host serves every provider call in process and holds no real account", async () => {
  const info = await hostInfo();
  expect(info.unfakedProviderCalls).toBe(0);
});
