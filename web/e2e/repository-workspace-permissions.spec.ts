import {
  apiAs,
  connectRepositoryViaApi,
  createConnectionViaApi,
  json,
  login,
  uniqueAccount,
  type ConnectionDto,
  type Session,
} from "./support/api";
import { USERS } from "./support/env";
import { expect, test } from "./support/fixtures";
import { WorkspacePage } from "./support/workspace";

let alice: Session;
let bob: Session;
let admin: Session;

test.beforeAll(async () => {
  [alice, bob, admin] = await Promise.all([login(USERS.alice), login(USERS.bob), login(USERS.admin)]);
});

async function prepareSharedConnection(): Promise<{ connection: ConnectionDto; account: string }> {
  const account = uniqueAccount("shared");
  const connection = await createConnectionViaApi(alice, account, { displayName: `Shared ${account}` });
  return { connection, account };
}

test.describe("anonymous visitor", () => {
  test("is sent to sign in and every connection and branch API answers 401", async ({ openAs }) => {
    const { page } = await openAs("anonymous");
    await page.goto("/repositories");
    await expect(page).toHaveURL(/\/auth/);

    const api = await apiAs(null);
    try {
      const calls = [
        api.get("/api/v1/git-connections"),
        api.post("/api/v1/git-connections", { data: { provider: "GitHub", displayName: "x", token: "e2e-nobody-abcd1234" } }),
        api.get("/api/v1/git-connections/none/repositories"),
        api.post("/api/v1/connected-repositories", { data: { connectionId: "none" } }),
        api.get("/api/v1/repositories/none/indexed-branches"),
        api.delete("/api/v1/repositories/none/indexed-branches/none"),
      ];
      for (const response of await Promise.all(calls)) {
        expect(response.status()).toBe(401);
      }
    } finally {
      await api.dispose();
    }
  });
});

test.describe("authenticated user who did not create the connection", () => {
  test("uses the shared connection and manages branches without maintenance controls", async ({ openAs }) => {
    const { connection } = await prepareSharedConnection();
    const indexed = await connectRepositoryViaApi(alice, connection, 2, ["main"]);

    const { page } = await openAs("bob");
    const workspace = new WorkspacePage(page);
    await workspace.goto();
    await workspace.connection(connection.displayName).click();

    await expect(page.getByText("Only the creator or an administrator can change this connection. You can still use it.")).toBeVisible();
    for (const name of ["Edit", "Check", "Disable", "Delete"]) {
      await expect(page.getByRole("button", { name, exact: true })).toHaveCount(0);
    }

    // Bob browses the catalog and adds a branch to the repository that Alice indexed.
    await workspace.repository("svc-002").click();
    await expect(workspace.managedBranch("main")).toBeVisible();
    await page.getByRole("button", { name: "Add branches" }).click();
    await workspace.indexBranches(["develop"], "Add selected branches");
    await expect(page.getByText("1 queued, 0 already indexed.")).toBeVisible();
    await page.getByRole("button", { name: "Manage indexed branches" }).click();
    await expect(workspace.managedBranch("develop")).toContainText("Completed", { timeout: 60_000 });

    // Bob rebuilds a branch.
    await workspace.managedBranch("main").getByRole("button", { name: "Rebuild" }).click();
    await expect(page.getByText("Rebuild started for main.")).toBeVisible();

    // The same calls made directly are refused for maintenance only.
    const api = await apiAs(bob);
    try {
      const put = await api.put(`/api/v1/git-connections/${connection.id}`, { data: { displayName: "Taken over" } });
      expect(put.status()).toBe(403);
      expect(await json<{ errorCode: string }>(put)).toMatchObject({ errorCode: "CONNECTION_MAINTENANCE_FORBIDDEN" });
      expect((await api.post(`/api/v1/git-connections/${connection.id}/disable`)).status()).toBe(403);
      expect((await api.post(`/api/v1/git-connections/${connection.id}/test`)).status()).toBe(403);
      expect((await api.delete(`/api/v1/git-connections/${connection.id}`)).status()).toBe(403);
      // Using the connection is allowed.
      expect((await api.get(`/api/v1/git-connections/${connection.id}/repositories?pageSize=5`)).status()).toBe(200);
      expect((await api.get(`/api/v1/repositories/${indexed.repositoryId}/indexed-branches`)).status()).toBe(200);
    } finally {
      await api.dispose();
    }
  });
});

test.describe("creator and administrator", () => {
  test("the creator edits, checks, disables and enables the connection", async ({ openAs }) => {
    const { connection } = await prepareSharedConnection();
    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();
    await workspace.connection(connection.displayName).click();

    await page.getByRole("button", { name: "Check", exact: true }).click();
    await expect(page.getByText(/The connection works\. Latency \d+ ms\./)).toBeVisible();

    await page.getByRole("button", { name: "Edit", exact: true }).click();
    const dialog = workspace.dialog;
    await expect(dialog.getByLabel("New token (optional)")).toHaveValue("");
    await expect(dialog.getByText("A token is saved. Leave this field empty to keep it, or enter a new token to replace it.")).toBeVisible();
    await dialog.getByLabel("Display name").fill(`${connection.displayName} renamed`);
    await dialog.getByRole("button", { name: "Save connection" }).click();
    await expect(workspace.connection(`${connection.displayName} renamed`)).toBeVisible();

    await page.getByRole("button", { name: "Disable", exact: true }).click();
    await expect(page.getByText("The connection is disabled.", { exact: true })).toBeVisible();
    await page.getByRole("button", { name: "Enable", exact: true }).click();
    await expect(page.getByText("The connection is enabled.", { exact: true })).toBeVisible();
  });

  test("an administrator maintains a connection that another user created", async ({ openAs }) => {
    const { connection } = await prepareSharedConnection();
    const { page } = await openAs("admin");
    const workspace = new WorkspacePage(page);
    await workspace.goto();
    await workspace.connection(connection.displayName).click();

    for (const name of ["Edit", "Check", "Disable", "Delete"]) {
      await expect(page.getByRole("button", { name, exact: true })).toBeVisible();
    }
    await page.getByRole("button", { name: "Check", exact: true }).click();
    await expect(page.getByText(/The connection works\. Latency \d+ ms\./)).toBeVisible();
    await page.getByRole("button", { name: "Disable", exact: true }).click();
    await expect(page.getByText("The connection is disabled.", { exact: true })).toBeVisible();

    const api = await apiAs(admin);
    try {
      expect((await api.post(`/api/v1/git-connections/${connection.id}/enable`)).status()).toBe(200);
    } finally {
      await api.dispose();
    }
  });
});

test.describe("disabled connection", () => {
  test("keeps existing documentation readable and refuses discovery and new work with a clear state", async ({ openAs }) => {
    const { connection } = await prepareSharedConnection();
    const indexed = await connectRepositoryViaApi(alice, connection, 1, ["main"]);

    const owner = await apiAs(alice);
    try {
      expect((await owner.post(`/api/v1/git-connections/${connection.id}/disable`)).status()).toBe(200);
    } finally {
      await owner.dispose();
    }

    const { page } = await openAs("alice");
    const workspace = new WorkspacePage(page);
    await workspace.goto();
    await workspace.connection(connection.displayName).click();
    await expect(page.getByText("This connection is disabled. Existing documentation stays available.", { exact: false })).toBeVisible();
    await expect(page.getByText("This connection is disabled, so repositories cannot be browsed.")).toBeVisible();
    await expect(page.getByRole("link", { name: "Open existing documentation" })).toBeVisible();

    const api = await apiAs(bob);
    try {
      const catalog = await api.get(`/api/v1/git-connections/${connection.id}/repositories?pageSize=5`);
      expect(catalog.status()).toBe(409);
      expect(await json<{ errorCode: string }>(catalog)).toMatchObject({ errorCode: "CONNECTION_DISABLED" });

      const connect = await api.post("/api/v1/connected-repositories", {
        data: { connectionId: connection.id, providerRepositoryId: "1", branches: ["main"], languageCode: "en" },
      });
      expect(connect.status()).toBe(409);
      expect(await json<{ errorCode: string }>(connect)).toMatchObject({ errorCode: "CONNECTION_DISABLED" });

      // The documentation record and its branches stay.
      const branches = await api.get(`/api/v1/repositories/${indexed.repositoryId}/indexed-branches`);
      expect(branches.status()).toBe(200);
      expect((await json<{ data: { branchName: string }[] }>(branches)).data.map((branch) => branch.branchName)).toContain("main");
      const list = await api.get(`/api/v1/repositories/list?page=1&pageSize=50&keyword=svc-001`);
      expect((await json<{ items: { id: string }[] }>(list)).items.map((item) => item.id)).toContain(indexed.repositoryId);
    } finally {
      await api.dispose();
    }
  });
});
