import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { renderWithIntl } from "@/test-utils/render-with-intl";
import { makeBranch, makeConnection, makeIndexedBranch, makeRepository } from "./test-fixtures";

const api = vi.hoisted(() => ({
  listConnections: vi.fn(),
  listProviderRepositories: vi.fn(),
  listProviderBranches: vi.fn(),
  findConnectedRepository: vi.fn(),
  listIndexedBranches: vi.fn(),
}));

vi.mock("@/lib/git-connections-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/git-connections-api")>()),
  ...api,
}));
vi.mock("@/lib/repository-api", () => ({
  cancelBranchGenerationTask: vi.fn(),
  enqueueBranchFullGeneration: vi.fn(),
  retryBranchGenerationTask: vi.fn(),
}));

import { RepositoryWorkspace, type WorkspaceState } from "./repository-workspace";

const EMPTY_STATE: WorkspaceState = {
  connectionId: null,
  repositoryId: null,
  filters: { q: "", visibility: "all", sort: "updated" },
};

function setViewport(width: number) {
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    matches: width < 950,
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    onchange: null,
    dispatchEvent: vi.fn(),
  }));
}

/** A viewport that can cross the breakpoint while the workspace is mounted. */
function useResizableViewport(initialWidth: number) {
  let width = initialWidth;
  const listeners = new Set<() => void>();
  window.matchMedia = vi.fn().mockImplementation((query: string) => ({
    get matches() {
      return width < 950;
    },
    media: query,
    addEventListener: (_type: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_type: string, listener: () => void) => listeners.delete(listener),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    onchange: null,
    dispatchEvent: vi.fn(),
  }));
  return {
    resize(next: number) {
      width = next;
      act(() => listeners.forEach((listener) => listener()));
    },
  };
}

/** Keeps the state like the page does, so a selection reaches the workspace again. */
function renderWorkspace(initial: WorkspaceState = EMPTY_STATE) {
  const onStateChange = vi.fn();
  function Harness() {
    const [state, setState] = useState(initial);
    return (
      <RepositoryWorkspace
        state={state}
        onStateChange={(next) => {
          onStateChange(next);
          setState(next);
        }}
      />
    );
  }
  const view = renderWithIntl(<Harness />);
  return { onStateChange, ...view };
}

describe("RepositoryWorkspace", () => {
  beforeEach(() => {
    Object.values(api).forEach((fn) => fn.mockReset());
    api.listConnections.mockResolvedValue([
      makeConnection(),
      makeConnection({ id: "c2", displayName: "Shared GitLab", provider: "GitLab", canMaintain: false }),
    ]);
    api.listProviderRepositories.mockResolvedValue({ items: [makeRepository()], nextCursor: null });
    api.listProviderBranches.mockResolvedValue({ items: [makeBranch("main", true)], nextCursor: null });
    api.findConnectedRepository.mockResolvedValue(null);
    api.listIndexedBranches.mockResolvedValue([makeIndexedBranch()]);
  });

  it.each([1440, 950])("keeps connections, repositories and plan in three columns at %ipx", async (width) => {
    setViewport(width);
    renderWorkspace();

    expect(await screen.findByRole("heading", { name: "Connections" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Repositories" })).toBeTruthy();
    expect(screen.getByText("Select a repository to plan its indexing.")).toBeTruthy();
    expect(screen.queryByRole("tablist")).toBeNull();
  });

  it.each([900, 390])("keeps all three steps as tabs at %ipx and moves forward after a selection", async (width) => {
    setViewport(width);
    const { onStateChange } = renderWorkspace();

    const tabs = await screen.findAllByRole("tab");
    expect(tabs.map((tab) => tab.textContent)).toEqual(["1. Connections", "2. Repositories", "3. Plan"]);
    expect(tabs[0].getAttribute("aria-selected")).toBe("true");

    fireEvent.click(await screen.findByRole("button", { name: /Ocean Labs/ }));

    expect(onStateChange).toHaveBeenCalledWith(expect.objectContaining({ connectionId: "c1", repositoryId: null }));
    await waitFor(() => expect(screen.getByRole("tab", { name: "2. Repositories" }).getAttribute("aria-selected")).toBe("true"));
  });

  it("keeps the selected connection and its state visible above the tabs on a narrow screen", async () => {
    setViewport(390);
    renderWorkspace({ ...EMPTY_STATE, connectionId: "c1" });

    const summary = await screen.findByRole("region", { name: "Current selection" });
    await waitFor(() => expect(summary.textContent).toContain("Ocean Labs"));
    expect(summary.textContent).toContain("Healthy");
  });

  it("lets a user use a shared connection without maintenance controls", async () => {
    setViewport(1440);
    renderWorkspace({ ...EMPTY_STATE, connectionId: "c2" });

    expect(await screen.findByText("api")).toBeTruthy();
    expect(api.listProviderRepositories).toHaveBeenCalledWith("c2", expect.anything());
    expect(screen.queryByRole("button", { name: "Delete" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
  });

  it("shows the index plan for a repository that is not indexed yet and reports the selection", async () => {
    setViewport(1440);
    const { onStateChange } = renderWorkspace({ ...EMPTY_STATE, connectionId: "c1" });

    fireEvent.click(await screen.findByRole("button", { name: /acme\/api/ }));

    expect(onStateChange).toHaveBeenCalledWith(expect.objectContaining({ connectionId: "c1", repositoryId: "100" }));
    expect(api.findConnectedRepository).toHaveBeenCalledWith("c1", "https://github.com/acme/api.git", "api", expect.any(AbortSignal));
    expect(await screen.findByRole("heading", { name: "acme/api" })).toBeTruthy();
    expect(await screen.findByRole("checkbox", { name: /main/ })).toBeTruthy();
  });

  it("shows managed branches for a repository that is already indexed", async () => {
    setViewport(1440);
    api.findConnectedRepository.mockResolvedValue({ repositoryId: "r1", orgName: "acme", repoName: "api" });
    renderWorkspace({ ...EMPTY_STATE, connectionId: "c1", repositoryId: "100" });

    const plan = await screen.findByRole("region", { name: /acme\/api/ });
    expect(await within(plan).findByRole("list", { name: "Indexed branches" })).toBeTruthy();
    expect(api.listIndexedBranches).toHaveBeenCalledWith("r1", expect.anything());
  });

  it("drops a connection from the URL state when it no longer exists", async () => {
    setViewport(1440);
    const { onStateChange } = renderWorkspace({ ...EMPTY_STATE, connectionId: "gone" });

    await waitFor(() =>
      expect(onStateChange).toHaveBeenCalledWith(expect.objectContaining({ connectionId: null, repositoryId: null })),
    );
  });

  it("explains a failed connection list and retries", async () => {
    setViewport(1440);
    api.listConnections.mockRejectedValueOnce(new Error("boom")).mockResolvedValueOnce([makeConnection()]);
    renderWorkspace();

    fireEvent.click((await screen.findAllByRole("button", { name: "Try again" }))[0]);

    expect(await screen.findByRole("button", { name: /Ocean Labs/ })).toBeTruthy();
  });

  it("opens on the repositories tab on a narrow screen when the URL names only a connection", async () => {
    setViewport(390);
    renderWorkspace({ ...EMPTY_STATE, connectionId: "c1" });

    const tab = await screen.findByRole("tab", { name: "2. Repositories" });
    expect(tab.getAttribute("aria-selected")).toBe("true");
  });

  it("opens on the plan tab on a narrow screen when the URL names a repository", async () => {
    setViewport(390);
    renderWorkspace({ ...EMPTY_STATE, connectionId: "c1", repositoryId: "100" });

    const tab = await screen.findByRole("tab", { name: "3. Plan" });
    expect(tab.getAttribute("aria-selected")).toBe("true");
    expect(await screen.findByRole("heading", { name: "acme/api" })).toBeTruthy();
  });

  it("keeps the mounted panes and an open dialog when the viewport crosses the breakpoint", async () => {
    const viewport = useResizableViewport(1440);
    renderWorkspace({ ...EMPTY_STATE, connectionId: "c1" });

    // The catalog remounts once when the connection list arrives, so the elements are captured after the rows load.
    await screen.findByText("api");
    const catalogHeading = screen.getByRole("heading", { name: "Repositories" });
    const repositoryRow = screen.getByRole("button", { name: /acme\/api/ });
    const listCalls = api.listProviderRepositories.mock.calls.length;
    fireEvent.click(screen.getByRole("button", { name: "Add connection" }));
    expect(await screen.findByRole("dialog")).toBeTruthy();

    viewport.resize(900);
    await screen.findAllByRole("tab", { hidden: true });

    // An open modal dialog hides the page behind it from the accessibility tree, so the queries include hidden nodes.
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Repositories", hidden: true })).toBe(catalogHeading);
    expect(screen.getByRole("button", { name: /acme\/api/, hidden: true })).toBe(repositoryRow);

    viewport.resize(1440);
    await waitFor(() => expect(screen.queryByRole("tablist", { hidden: true })).toBeNull());

    expect(screen.getByRole("dialog")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Repositories", hidden: true })).toBe(catalogHeading);
    expect(api.listProviderRepositories.mock.calls.length).toBe(listCalls);
    expect(api.listConnections).toHaveBeenCalledTimes(1);
  });
});
