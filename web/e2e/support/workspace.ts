import { expect, type Locator, type Page } from "@playwright/test";

export type ProviderChoice = "GitHub" | "GitLab.com" | "GitLab self-hosted";

export interface NewConnection {
  provider: ProviderChoice;
  displayName: string;
  token: string;
  serverUrl?: string;
}

/** Locators and actions of the repository workspace. Names are the English labels of the messages file. */
export class WorkspacePage {
  constructor(readonly page: Page) {}

  async goto(query = ""): Promise<void> {
    await this.page.goto(`/repositories${query}`);
    await expect(this.page.getByRole("heading", { level: 1, name: "Repository workspace" })).toBeVisible();
  }

  get addConnectionButton(): Locator {
    return this.page.getByRole("button", { name: "Add connection" });
  }

  get dialog(): Locator {
    return this.page.getByRole("dialog");
  }

  connection(name: string): Locator {
    return this.page.getByRole("list", { name: "Git connections" }).getByRole("button", { name: new RegExp(name) });
  }

  repository(name: string): Locator {
    return this.page.getByRole("list", { name: "Repository catalog" }).getByRole("button", { name: new RegExp(`/${name}\\b`) });
  }

  get catalogRows(): Locator {
    return this.page.getByRole("list", { name: "Repository catalog" }).getByRole("listitem");
  }

  branchCheckbox(name: string): Locator {
    return this.page.getByRole("checkbox", { name: new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\/]/g, "\\$&")}( |$)`) });
  }

  get liveStatus(): Locator {
    return this.page.getByRole("status");
  }

  managedBranch(name: string): Locator {
    return this.page
      .getByRole("list", { name: "Indexed branches" })
      .getByRole("listitem")
      .filter({ has: this.page.locator("code", { hasText: new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\/]/g, "\\$&")}$`) }) });
  }

  /** Opens the dialog, fills it and saves. The token field is cleared by the page after submit. */
  async addConnection(connection: NewConnection): Promise<void> {
    await this.addConnectionButton.click();
    await this.fillConnectionDialog(connection);
    await this.dialog.getByRole("button", { name: "Save connection" }).click();
  }

  async fillConnectionDialog(connection: NewConnection): Promise<void> {
    const dialog = this.dialog;
    await expect(dialog).toBeVisible();
    await dialog.getByLabel("Provider").selectOption({ label: connection.provider });
    if (connection.provider === "GitLab self-hosted") {
      await dialog.getByLabel("Server URL").fill(connection.serverUrl ?? "");
    }
    await dialog.getByLabel("Display name").fill(connection.displayName);
    await dialog.getByLabel("Access token").fill(connection.token);
  }

  /** Selects branches in the open index plan and submits. Returns after the result list is shown. */
  async indexBranches(branches: string[], submitLabel = "Index selected branches"): Promise<void> {
    for (const branch of branches) {
      const box = this.branchCheckbox(branch);
      await expect(box).toBeVisible();
      await box.check();
    }
    await this.page.getByRole("button", { name: submitLabel }).click();
    await expect(this.page.getByRole("list", { name: "Result per branch" })).toBeVisible();
  }

  async openManagedBranches(): Promise<void> {
    await this.page.getByRole("button", { name: "Manage indexed branches" }).click();
    await expect(this.page.getByRole("list", { name: "Indexed branches" })).toBeVisible();
  }
}
