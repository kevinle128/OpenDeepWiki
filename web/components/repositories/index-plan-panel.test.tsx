import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/lib/api-client";
import { renderWithIntl } from "@/test-utils/render-with-intl";
import { makeBranch, makeConnection, makeRepository } from "./test-fixtures";

const api = vi.hoisted(() => ({
  listProviderBranches: vi.fn(),
  connectRepository: vi.fn(),
  addIndexedBranches: vi.fn(),
}));

vi.mock("@/lib/git-connections-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/git-connections-api")>()),
  ...api,
}));

import { IndexPlanPanel } from "./index-plan-panel";

const BRANCHES = {
  items: [makeBranch("main", true), makeBranch("develop"), makeBranch("release/1.0")],
  nextCursor: null,
};

function renderPanel(props: Partial<React.ComponentProps<typeof IndexPlanPanel>> = {}) {
  const merged: React.ComponentProps<typeof IndexPlanPanel> = {
    connection: makeConnection(),
    repository: makeRepository(),
    onManage: vi.fn(),
    ...props,
  };
  renderWithIntl(<IndexPlanPanel {...merged} />);
  return merged;
}

describe("IndexPlanPanel", () => {
  beforeEach(() => {
    Object.values(api).forEach((fn) => fn.mockReset());
    api.listProviderBranches.mockResolvedValue(BRANCHES);
  });

  it("preselects the default branch and sends every selected branch with the chosen language", async () => {
    api.connectRepository.mockResolvedValue({
      repositoryId: "r1", orgName: "acme", repoName: "api", gitConnectionId: "c1", repositoryCreated: true, branches: [],
    });
    renderPanel();

    const main = await screen.findByRole("checkbox", { name: /main/ });
    expect(main.getAttribute("aria-checked")).toBe("true");
    fireEvent.click(screen.getByRole("checkbox", { name: /release\/1\.0/ }));
    fireEvent.change(screen.getByLabelText("Documentation language"), { target: { value: "vi" } });
    fireEvent.click(screen.getByRole("button", { name: "Index selected branches" }));

    await waitFor(() => expect(api.connectRepository).toHaveBeenCalled());
    expect(api.connectRepository).toHaveBeenCalledWith({
      connectionId: "c1",
      providerRepositoryId: "100",
      branches: ["main", "release/1.0"],
      languageCode: "vi",
      generateSkill: true,
    });
  });

  it("sends generateSkill false when the SKILL toggle is switched off", async () => {
    api.connectRepository.mockResolvedValue({
      repositoryId: "r1", orgName: "acme", repoName: "api", gitConnectionId: "c1", repositoryCreated: true, branches: [],
    });
    renderPanel();

    const skill = await screen.findByRole("checkbox", { name: /Generate a SKILL\.md/ });
    expect(skill.getAttribute("aria-checked")).toBe("true");
    fireEvent.click(skill);
    fireEvent.click(screen.getByRole("button", { name: "Index selected branches" }));

    await waitFor(() => expect(api.connectRepository).toHaveBeenCalled());
    expect(api.connectRepository).toHaveBeenCalledWith(expect.objectContaining({ generateSkill: false }));
  });

  it("offers no SKILL toggle when branches are added to an indexed repository", async () => {
    renderPanel({ repositoryId: "r1" });

    await screen.findByRole("checkbox", { name: /main/ });
    expect(screen.queryByRole("checkbox", { name: /Generate a SKILL\.md/ })).toBeNull();
  });

  it("shows a result for each branch instead of one success message", async () => {
    api.connectRepository.mockResolvedValue({
      repositoryId: "r1", orgName: "acme", repoName: "api", gitConnectionId: "c1", repositoryCreated: false,
      branches: [
        { branchId: "b1", branchName: "main", created: true, taskId: "t1", taskStatus: "Pending" },
        { branchId: "b2", branchName: "develop", created: false },
      ],
    });
    const props = renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: "Index selected branches" }));

    const results = await screen.findByRole("list", { name: "Result per branch" });
    const rows = within(results).getAllByRole("listitem");
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain("main");
    expect(rows[0].textContent).toContain("Queued (Pending)");
    expect(rows[1].textContent).toContain("develop");
    expect(rows[1].textContent).toContain("Already indexed");
    expect(screen.getByText("1 queued, 1 already indexed.")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Manage indexed branches" }));
    expect(props.onManage).toHaveBeenCalledWith(expect.objectContaining({ repositoryId: "r1" }));
  });

  it("names the failed branches and focuses the error when the backend rejects the request", async () => {
    api.connectRepository.mockRejectedValue(
      new ApiError("x", 422, { success: false, errorCode: "REMOTE_BRANCH_NOT_FOUND", branches: ["develop"] }),
    );
    renderPanel();

    fireEvent.click(await screen.findByRole("button", { name: "Index selected branches" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("A selected branch does not exist on the provider.");
    expect(alert.textContent).toContain("These branches failed: develop");
    await waitFor(() => expect(document.activeElement).toBe(alert));
    expect(screen.queryByRole("list", { name: "Result per branch" })).toBeNull();
  });

  it("sets no state when a branch page fails after the panel unmounted", async () => {
    const errorSpy = vi.spyOn(console, "error").mockImplementation(() => {});
    let rejectMore: (error: unknown) => void = () => {};
    api.listProviderBranches
      .mockResolvedValueOnce({ items: BRANCHES.items, nextCursor: "next" })
      .mockImplementationOnce(() => new Promise((_, reject) => { rejectMore = reject; }));
    const { unmount } = renderWithIntl(
      <IndexPlanPanel connection={makeConnection()} repository={makeRepository()} onManage={vi.fn()} />,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Load more branches" }));
    const signal = api.listProviderBranches.mock.calls[1][2].signal as AbortSignal;
    unmount();
    expect(signal.aborted).toBe(true);
    rejectMore(new ApiError("late", 500, {}));
    await Promise.resolve();
    await Promise.resolve();

    // The late failure is swallowed: nothing is logged and no state update is attempted.
    expect(errorSpy).not.toHaveBeenCalled();
    errorSpy.mockRestore();
  });

  it("blocks submission with no selected branch", async () => {
    renderPanel();

    fireEvent.click(await screen.findByRole("checkbox", { name: /main/ }));

    const submit = screen.getByRole("button", { name: "Index selected branches" }) as HTMLButtonElement;
    expect(submit.disabled).toBe(true);
    expect(screen.getByText("Select at least one branch.")).toBeTruthy();
  });

  it("explains a disabled connection and loads nothing", () => {
    renderPanel({ connection: makeConnection({ isEnabled: false, state: "Disabled" }) });

    expect(api.listProviderBranches).not.toHaveBeenCalled();
    expect(screen.getByText("The connection is disabled. Enable it to index branches.")).toBeTruthy();
  });

  it("adds branches to an existing repository and hides branches that are already indexed", async () => {
    api.addIndexedBranches.mockResolvedValue({
      repositoryId: "r1", orgName: "a", repoName: "b", gitConnectionId: "c1", repositoryCreated: false,
      branches: [{ branchId: "b3", branchName: "develop", created: true, taskId: "t", taskStatus: "Pending" }],
    });
    renderPanel({ repositoryId: "r1", excludedBranches: ["main"] });

    expect(await screen.findByRole("checkbox", { name: /develop/ })).toBeTruthy();
    expect(screen.queryByRole("checkbox", { name: /main/ })).toBeNull();
    fireEvent.click(screen.getByRole("checkbox", { name: /develop/ }));
    fireEvent.click(screen.getByRole("button", { name: "Add selected branches" }));

    await waitFor(() => expect(api.addIndexedBranches).toHaveBeenCalledWith("r1", { branches: ["develop"], languageCode: "en" }));
    expect(api.connectRepository).not.toHaveBeenCalled();
  });
});
