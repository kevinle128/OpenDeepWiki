import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/lib/api-client";
import { renderWithIntl } from "@/test-utils/render-with-intl";
import { makeConnection, makeIndexedBranch, makeRepository } from "./test-fixtures";

const api = vi.hoisted(() => ({
  listIndexedBranches: vi.fn(),
  removeIndexedBranch: vi.fn(),
  triggerBranchSync: vi.fn(),
  listProviderBranches: vi.fn(),
}));
const tasks = vi.hoisted(() => ({
  enqueueBranchFullGeneration: vi.fn(),
  retryBranchGenerationTask: vi.fn(),
  cancelBranchGenerationTask: vi.fn(),
}));

vi.mock("@/lib/git-connections-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/git-connections-api")>()),
  ...api,
}));
vi.mock("@/lib/repository-api", () => tasks);

import { ManagedBranchesPanel } from "./managed-branches-panel";

function renderPanel(props: Partial<React.ComponentProps<typeof ManagedBranchesPanel>> = {}) {
  return renderWithIntl(
    <ManagedBranchesPanel
      connection={makeConnection()}
      repository={makeRepository()}
      repositoryId="r1"
      {...props}
    />,
  );
}

const row = (name: string) => screen.getByRole("group", { name: `Actions for branch ${name}` });

describe("ManagedBranchesPanel", () => {
  beforeEach(() => {
    [...Object.values(api), ...Object.values(tasks)].forEach((fn) => fn.mockReset());
    api.listIndexedBranches.mockResolvedValue([
      makeIndexedBranch({ branchId: "b1", branchName: "main" }),
      makeIndexedBranch({ branchId: "b2", branchName: "develop", generationStatus: "Failed", lastGenerationTaskId: "t9", lastGenerationError: "boom" }),
    ]);
  });
  afterEach(() => vi.useRealTimers());

  it("lists each indexed branch with a text status and a localized failure", async () => {
    renderPanel();

    const list = await screen.findByRole("list", { name: "Indexed branches" });
    const rows = within(list).getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain("Completed");
    expect(rows[1].textContent).toContain("Failed");
    expect(rows[1].textContent).toContain("boom");
  });

  it("starts a sync and a rebuild through the existing task routes and announces them", async () => {
    api.triggerBranchSync.mockResolvedValue({ success: true, taskId: "i1", status: "Pending" });
    tasks.enqueueBranchFullGeneration.mockResolvedValue({ success: true, taskId: "g1" });
    renderPanel();
    await screen.findByRole("list", { name: "Indexed branches" });

    fireEvent.click(within(row("main")).getByRole("button", { name: "Sync" }));
    expect(await screen.findByText("Sync started for main.")).toBeTruthy();
    expect(api.triggerBranchSync).toHaveBeenCalledWith("r1", "b1");

    fireEvent.click(within(row("main")).getByRole("button", { name: "Rebuild" }));
    expect(await screen.findByText("Rebuild started for main.")).toBeTruthy();
    expect(tasks.enqueueBranchFullGeneration).toHaveBeenCalledWith("r1", "b1");
    expect(screen.getByText("Rebuild started for main.").closest('[aria-live="polite"]')).toBeTruthy();
  });

  it("offers retry for a failed branch and cancel for a task that waits to start", async () => {
    api.listIndexedBranches.mockResolvedValue([
      makeIndexedBranch({ branchId: "b1", branchName: "main", generationStatus: "Pending", activeTaskId: "t5", activeTaskKind: "Full" }),
      makeIndexedBranch({ branchId: "b2", branchName: "develop", generationStatus: "Failed", lastGenerationTaskId: "t9" }),
    ]);
    tasks.retryBranchGenerationTask.mockResolvedValue({ success: true, taskId: "t10" });
    tasks.cancelBranchGenerationTask.mockResolvedValue({ success: true, taskId: "t5" });
    renderPanel();
    await screen.findByRole("list", { name: "Indexed branches" });

    expect(within(row("main")).queryByRole("button", { name: "Retry" })).toBeNull();
    fireEvent.click(within(row("main")).getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(tasks.cancelBranchGenerationTask).toHaveBeenCalledWith("t5"));

    fireEvent.click(within(row("develop")).getByRole("button", { name: "Retry" }));
    await waitFor(() => expect(tasks.retryBranchGenerationTask).toHaveBeenCalledWith("t9"));
  });

  it("does not offer cancel for a task that is already processing, because only a pending task can be cancelled", async () => {
    api.listIndexedBranches.mockResolvedValue([
      makeIndexedBranch({ branchId: "b1", branchName: "main", generationStatus: "Processing", activeTaskId: "t5", activeTaskKind: "Full" }),
    ]);
    renderPanel();
    await screen.findByRole("list", { name: "Indexed branches" });

    expect(screen.getByText("Active task: Full")).toBeTruthy();
    expect(within(row("main")).queryByRole("button", { name: "Cancel" })).toBeNull();
  });

  it("does not offer cancel for a sync task, because only full generation tasks can be cancelled", async () => {
    api.listIndexedBranches.mockResolvedValue([
      makeIndexedBranch({ branchId: "b1", branchName: "main", generationStatus: "Completed", activeTaskId: "i3", activeTaskKind: "Incremental" }),
    ]);
    renderPanel();
    await screen.findByRole("list", { name: "Indexed branches" });

    expect(screen.getByText("Active task: Incremental")).toBeTruthy();
    expect(within(row("main")).queryByRole("button", { name: "Cancel" })).toBeNull();
    expect(within(row("main")).getByRole("button", { name: "Remove" })).toBeTruthy();
  });

  it("keeps the branch and focuses an error when removal is refused because a job is active (409)", async () => {
    api.removeIndexedBranch.mockRejectedValue(
      new ApiError("x", 409, { success: false, errorCode: "BRANCH_JOB_ACTIVE" }),
    );
    renderPanel();
    await screen.findByRole("list", { name: "Indexed branches" });

    fireEvent.click(within(row("main")).getByRole("button", { name: "Remove" }));
    expect(await screen.findByText(/Only the OpenDeepWiki data of main is deleted/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Remove data only" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("A job of this branch is running");
    await waitFor(() => expect(document.activeElement).toBe(alert));
    expect(screen.getByRole("list", { name: "Indexed branches" }).textContent).toContain("main");
  });

  it("removes a branch from the list after a confirmed removal", async () => {
    api.removeIndexedBranch.mockResolvedValue({ repositoryId: "r1", branchId: "b1", branchName: "main", workspaceRemoved: true });
    api.listIndexedBranches
      .mockResolvedValueOnce([makeIndexedBranch({ branchId: "b1", branchName: "main" }), makeIndexedBranch({ branchId: "b2", branchName: "develop" })])
      .mockResolvedValue([makeIndexedBranch({ branchId: "b2", branchName: "develop" })]);
    renderPanel();
    await screen.findByRole("list", { name: "Indexed branches" });

    fireEvent.click(within(row("main")).getByRole("button", { name: "Remove" }));
    fireEvent.click(await screen.findByRole("button", { name: "Remove data only" }));

    expect(await screen.findByText("main is removed from the index.")).toBeTruthy();
    expect(within(screen.getByRole("list", { name: "Indexed branches" })).queryByText("main")).toBeNull();
  });

  it("disables sync and rebuild for a disabled connection and says why", async () => {
    renderPanel({ connection: makeConnection({ isEnabled: false, state: "Disabled" }) });
    await screen.findByRole("list", { name: "Indexed branches" });

    expect((within(row("main")).getByRole("button", { name: "Sync" }) as HTMLButtonElement).disabled).toBe(true);
    expect((within(row("main")).getByRole("button", { name: "Rebuild" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText("The connection is disabled, so sync and rebuild are unavailable.")).toBeTruthy();
  });

  it("refreshes while a task is active and reports progress in a polite live region", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    api.listIndexedBranches
      .mockResolvedValueOnce([makeIndexedBranch({ branchId: "b1", branchName: "main", generationStatus: "Processing", activeTaskId: "t1" })])
      .mockResolvedValue([makeIndexedBranch({ branchId: "b1", branchName: "main", generationStatus: "Completed" })]);
    renderPanel();

    const progress = await screen.findByText("0 of 1 branches have finished.");
    expect(progress.closest('[aria-live="polite"]')).toBeTruthy();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(6000);
    });
    expect(await screen.findByText("1 of 1 branches have finished.")).toBeTruthy();
  });
});
