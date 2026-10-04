import { fireEvent, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/lib/api-client";
import { renderWithIntl } from "@/test-utils/render-with-intl";
import { makeConnection } from "./test-fixtures";

const api = vi.hoisted(() => ({
  createConnection: vi.fn(),
  updateConnection: vi.fn(),
}));

vi.mock("@/lib/git-connections-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/git-connections-api")>()),
  ...api,
}));

import { ConnectionDialog } from "./connection-dialog";

const SECRET = "glpat-super-secret-value";

function renderDialog(props: Partial<React.ComponentProps<typeof ConnectionDialog>> = {}) {
  const merged = { open: true, onOpenChange: vi.fn(), onSaved: vi.fn(), ...props };
  renderWithIntl(<ConnectionDialog {...merged} />);
  return merged;
}

function type(label: string | RegExp, value: string) {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

describe("ConnectionDialog", () => {
  beforeEach(() => {
    api.createConnection.mockReset();
    api.updateConnection.mockReset();
    window.localStorage.clear();
    window.sessionStorage.clear();
  });

  it("opens an edit form with an empty token field and only reports that a token is saved", () => {
    renderDialog({ connection: makeConnection({ hasSecret: true }) });

    const token = screen.getByLabelText("New token (optional)") as HTMLInputElement;
    expect(token.value).toBe("");
    expect(token.type).toBe("password");
    expect(screen.getByText(/A token is saved/)).toBeTruthy();
    expect((screen.getByLabelText("Display name") as HTMLInputElement).value).toBe("Ocean Labs");
  });

  it("sends no token when an edit leaves the field empty", async () => {
    api.updateConnection.mockResolvedValue(makeConnection({ displayName: "Renamed" }));
    const props = renderDialog({ connection: makeConnection() });

    type("Display name", "Renamed");
    fireEvent.click(screen.getByRole("button", { name: "Save connection" }));

    await waitFor(() => expect(props.onSaved).toHaveBeenCalled());
    expect(api.updateConnection).toHaveBeenCalledWith("c1", { displayName: "Renamed", token: undefined });
    expect(props.onOpenChange).toHaveBeenCalledWith(false);
  });

  it("shows the server URL field only for self-hosted GitLab and sends it with the new connection", async () => {
    api.createConnection.mockResolvedValue({ connection: makeConnection(), existing: false });
    const props = renderDialog();

    expect(screen.queryByLabelText("Server URL")).toBeNull();
    fireEvent.change(screen.getByLabelText("Provider"), { target: { value: "gitlab-self-hosted" } });
    type("Server URL", "https://git.example.com");
    type("Display name", "Ocean");
    type("Access token", SECRET);
    fireEvent.click(screen.getByRole("button", { name: "Save connection" }));

    await waitFor(() => expect(props.onSaved).toHaveBeenCalledWith(expect.anything(), false));
    expect(api.createConnection).toHaveBeenCalledWith({
      provider: "GitLab",
      displayName: "Ocean",
      serverUrl: "https://git.example.com",
      token: SECRET,
    });
  });

  it("focuses an error summary when required fields are missing and does not call the API", async () => {
    renderDialog();

    fireEvent.click(screen.getByRole("button", { name: "Save connection" }));

    const summary = await screen.findByRole("alert");
    expect(summary.textContent).toContain("The connection was not saved");
    await waitFor(() => expect(document.activeElement).toBe(summary));
    expect(api.createConnection).not.toHaveBeenCalled();
    expect(screen.getAllByText("This field is required.").length).toBeGreaterThan(0);
  });

  it("rejects a self-hosted server URL that is not HTTPS before any request", async () => {
    renderDialog();

    fireEvent.change(screen.getByLabelText("Provider"), { target: { value: "gitlab-self-hosted" } });
    type("Server URL", "http://git.example.com");
    type("Display name", "Ocean");
    type("Access token", SECRET);
    fireEvent.click(screen.getByRole("button", { name: "Save connection" }));

    expect(await screen.findByText("Enter an HTTPS address.")).toBeTruthy();
    expect(api.createConnection).not.toHaveBeenCalled();
  });

  it("keeps the token out of the DOM, storage and URL after a rejected token (422)", async () => {
    api.createConnection.mockRejectedValue(
      new ApiError("The provider rejected the token.", 422, { success: false, errorCode: "PROVIDER_UNAUTHORIZED" }),
    );
    renderDialog();

    type("Display name", "Ocean");
    type("Access token", SECRET);
    fireEvent.click(screen.getByRole("button", { name: "Save connection" }));

    const summary = await screen.findByText("The provider rejected the token. Enter a valid token.");
    expect(summary.closest('[role="alert"]')).toBeTruthy();
    expect((screen.getByLabelText("Access token") as HTMLInputElement).value).toBe("");
    expect(document.body.innerHTML).not.toContain(SECRET);
    expect(JSON.stringify({ ...window.localStorage })).not.toContain(SECRET);
    expect(JSON.stringify({ ...window.sessionStorage })).not.toContain(SECRET);
    expect(window.location.href).not.toContain(SECRET);
  });

  it("reports a reused connection through the saved callback", async () => {
    api.createConnection.mockResolvedValue({ connection: makeConnection(), existing: true });
    const props = renderDialog();

    type("Display name", "Ocean");
    type("Access token", SECRET);
    fireEvent.click(screen.getByRole("button", { name: "Save connection" }));

    await waitFor(() => expect(props.onSaved).toHaveBeenCalledWith(expect.anything(), true));
  });
});
