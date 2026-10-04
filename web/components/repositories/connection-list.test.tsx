import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/lib/api-client";
import { renderWithIntl } from "@/test-utils/render-with-intl";
import { makeConnection } from "./test-fixtures";

const api = vi.hoisted(() => ({
  deleteConnection: vi.fn(),
  testConnection: vi.fn(),
  setConnectionEnabled: vi.fn(),
}));

vi.mock("@/lib/git-connections-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/git-connections-api")>()),
  ...api,
}));

import { ConnectionList } from "./connection-list";

function renderList(overrides: Partial<React.ComponentProps<typeof ConnectionList>> = {}) {
  const props: React.ComponentProps<typeof ConnectionList> = {
    connections: [
      makeConnection(),
      makeConnection({ id: "c2", displayName: "VTC Studio", provider: "GitLab", serverUrl: "https://gitlab.com", canMaintain: false }),
    ],
    selectedId: "c1",
    onSelect: vi.fn(),
    onSaved: vi.fn(),
    onUpdated: vi.fn(),
    onRemoved: vi.fn(),
    ...overrides,
  };
  renderWithIntl(<ConnectionList {...props} />);
  return props;
}

describe("ConnectionList", () => {
  beforeEach(() => {
    Object.values(api).forEach((fn) => fn.mockReset());
  });

  it("lists connections as native buttons and marks the selected one for assistive technology", () => {
    const props = renderList();

    const list = screen.getByRole("list", { name: "Git connections" });
    const buttons = within(list).getAllByRole("button");
    expect(buttons).toHaveLength(2);
    expect(buttons[0].tagName).toBe("BUTTON");
    expect(buttons[0].getAttribute("aria-current")).toBe("true");
    expect(buttons[1].getAttribute("aria-current")).toBeNull();
    expect(within(buttons[0]).getByText("Selected")).toBeTruthy();

    fireEvent.click(buttons[1]);
    expect(props.onSelect).toHaveBeenCalledWith("c2");
  });

  it("hides maintenance actions from a user that the backend does not allow to maintain", () => {
    renderList({ selectedId: "c2" });

    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Delete" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Disable" })).toBeNull();
    expect(screen.getByText(/Only the creator or an administrator/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Add connection" })).toBeTruthy();
  });

  it("shows maintenance actions when the backend grants canMaintain", () => {
    renderList();

    for (const name of ["Edit", "Check", "Disable", "Delete"]) {
      expect(screen.getByRole("button", { name })).toBeTruthy();
    }
  });

  it("offers enable for a disabled connection and explains the limit", async () => {
    const disabled = makeConnection({ isEnabled: false, state: "Disabled" });
    api.setConnectionEnabled.mockResolvedValue({ ...disabled, isEnabled: true, state: "Healthy" });
    const props = renderList({ connections: [disabled] });

    expect(screen.getAllByText("Disabled").length).toBeGreaterThan(0);
    expect(screen.getByText(/Existing documentation stays available/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Enable" }));
    await waitFor(() => expect(props.onUpdated).toHaveBeenCalled());
    expect(api.setConnectionEnabled).toHaveBeenCalledWith("c1", true);
  });

  it("announces the result of a connection check in a polite status region", async () => {
    api.testConnection.mockResolvedValue({ ok: true, state: "Healthy", latencyMs: 184, checkedAt: "2026-10-03T00:00:00Z" });
    renderList();

    fireEvent.click(screen.getByRole("button", { name: "Check" }));

    const status = await screen.findByText("The connection works. Latency 184 ms.");
    expect(status.closest('[role="status"]')?.getAttribute("aria-live")).toBe("polite");
  });

  it("keeps the connection and shows a focused error when repositories still use it (409)", async () => {
    api.deleteConnection.mockRejectedValue(
      new ApiError("x", 409, { success: false, errorCode: "CONNECTION_IN_USE", message: "x" }),
    );
    const props = renderList();

    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    fireEvent.click(await screen.findByRole("button", { name: "Delete connection" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("Repositories still use this connection");
    expect(props.onRemoved).not.toHaveBeenCalled();
    await waitFor(() => expect(document.activeElement).toBe(alert));
  });

  it("returns focus to the add button when the dialog closes", async () => {
    renderList();
    const add = screen.getByRole("button", { name: "Add connection" });
    add.focus();

    fireEvent.click(add);
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(add));
  });
});
