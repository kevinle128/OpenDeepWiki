import { fireEvent, screen, waitFor } from "@testing-library/react";
import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/lib/api-client";
import { renderWithIntl } from "@/test-utils/render-with-intl";
import { makeConnection } from "@/components/repositories/test-fixtures";

const repoApi = vi.hoisted(() => ({
  submitRepository: vi.fn(),
  submitArchiveRepository: vi.fn(),
  submitLocalDirectoryRepository: vi.fn(),
  fetchGitBranches: vi.fn(),
  checkGitHubRepo: vi.fn(),
}));
const connectionsApi = vi.hoisted(() => ({ listConnections: vi.fn() }));
const toast = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }));

vi.mock("@/lib/repository-api", () => repoApi);
vi.mock("@/lib/git-connections-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/git-connections-api")>()),
  ...connectionsApi,
}));
vi.mock("sonner", () => ({ toast }));

import { RepositorySubmitForm } from "./repository-submit-form";

const URL_INPUT = "https://github.com/acme/api";

function fillPrivateForm() {
  fireEvent.change(screen.getByPlaceholderText(/github\.com/i), { target: { value: URL_INPUT } });
  const switches = screen.getAllByRole("switch");
  fireEvent.click(switches[switches.length - 1]);
}

describe("RepositorySubmitForm private repositories", () => {
  beforeAll(() => {
    // The switch primitive measures itself, and jsdom has no ResizeObserver.
    globalThis.ResizeObserver ??= class {
      observe() {}
      unobserve() {}
      disconnect() {}
    };
  });

  beforeEach(() => {
    Object.values(repoApi).forEach((fn) => fn.mockReset());
    toast.success.mockReset();
    toast.error.mockReset();
    repoApi.fetchGitBranches.mockResolvedValue({ branches: [], isSupported: false });
    repoApi.checkGitHubRepo.mockResolvedValue({ exists: false, isPrivate: false });
    connectionsApi.listConnections.mockResolvedValue([
      makeConnection({ id: "gh", displayName: "Ocean GitHub", accountLogin: "ocean" }),
      makeConnection({ id: "gl", displayName: "Other host", provider: "GitLab", serverUrl: "https://gitlab.com" }),
      makeConnection({ id: "off", displayName: "Disabled", isEnabled: false }),
    ]);
  });

  it("offers no account or password field and lists only enabled connections of the same server", async () => {
    renderWithIntl(<RepositorySubmitForm />);
    fillPrivateForm();

    const select = (await screen.findByLabelText("Git connection")) as HTMLSelectElement;
    const options = Array.from(select.options).map((option) => option.textContent);
    expect(options).toContain("Ocean GitHub (ocean)");
    expect(options.join()).not.toContain("Other host");
    expect(options.join()).not.toContain("Disabled");
    expect(screen.queryByPlaceholderText(/access token or password/i)).toBeNull();
    expect(document.querySelector('input[type="password"]')).toBeNull();
  });

  it("submits the connection ID and never sends the legacy credential fields", async () => {
    repoApi.submitRepository.mockResolvedValue({});
    renderWithIntl(<RepositorySubmitForm />);
    fillPrivateForm();

    fireEvent.change(await screen.findByLabelText("Git connection"), { target: { value: "gh" } });
    fireEvent.submit(screen.getByRole("button", { name: /submit|add|create/i }).closest("form")!);

    await waitFor(() => expect(repoApi.submitRepository).toHaveBeenCalled());
    const request = repoApi.submitRepository.mock.calls[0][0];
    expect(request).toMatchObject({ gitUrl: URL_INPUT, isPublic: false, gitConnectionId: "gh" });
    expect(request).not.toHaveProperty("authAccount");
    expect(request).not.toHaveProperty("authPassword");
  });

  it("blocks a private submit without a connection and explains what to do", async () => {
    renderWithIntl(<RepositorySubmitForm />);
    fillPrivateForm();
    await screen.findByLabelText("Git connection");

    fireEvent.submit(screen.getByRole("button", { name: /submit|add|create/i }).closest("form")!);

    expect(await screen.findByText("Choose a Git connection for a private repository.")).toBeTruthy();
    expect(repoApi.submitRepository).not.toHaveBeenCalled();
  });

  it("points to the workspace when no connection fits the repository server", async () => {
    connectionsApi.listConnections.mockResolvedValue([]);
    renderWithIntl(<RepositorySubmitForm />);
    fillPrivateForm();

    const link = await screen.findByRole("link", { name: "Add a connection" });
    expect(link.getAttribute("href")).toBe("/repositories");
  });

  it("shows localized text for a connection error code and does not repeat provider text", async () => {
    repoApi.submitRepository.mockRejectedValue(
      new ApiError("raw backend text", 409, { success: false, errorCode: "CONNECTION_DISABLED" }),
    );
    renderWithIntl(<RepositorySubmitForm />);
    fillPrivateForm();
    fireEvent.change(await screen.findByLabelText("Git connection"), { target: { value: "gh" } });

    fireEvent.submit(screen.getByRole("button", { name: /submit|add|create/i }).closest("form")!);

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith("The connection is disabled."));
  });

  it("shows localized text for an ApiError without a code and never shows the backend message", async () => {
    repoApi.submitRepository.mockRejectedValue(new ApiError("raw backend text", 500, { message: "raw backend text" }));
    renderWithIntl(<RepositorySubmitForm />);
    fillPrivateForm();
    fireEvent.change(await screen.findByLabelText("Git connection"), { target: { value: "gh" } });

    fireEvent.submit(screen.getByRole("button", { name: /submit|add|create/i }).closest("form")!);

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    expect(toast.error.mock.calls[0][0]).not.toContain("raw backend text");
    expect(toast.error).toHaveBeenCalledWith("The provider request failed. Try again later.");
  });

  it("does not log the error object when a submit fails", async () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    const failure = new ApiError("token ghp_secret", 500, { message: "token ghp_secret" });
    repoApi.submitRepository.mockRejectedValue(failure);
    renderWithIntl(<RepositorySubmitForm />);
    fillPrivateForm();
    fireEvent.change(await screen.findByLabelText("Git connection"), { target: { value: "gh" } });

    fireEvent.submit(screen.getByRole("button", { name: /submit|add|create/i }).closest("form")!);

    await waitFor(() => expect(toast.error).toHaveBeenCalled());
    const logged = spy.mock.calls.flat();
    expect(logged).not.toContain(failure);
    expect(JSON.stringify(logged.map(String))).not.toContain("ghp_secret");
    spy.mockRestore();
  });

  it("submits a public Git repository without a connection ID, account or password", async () => {
    repoApi.submitRepository.mockResolvedValue({});
    renderWithIntl(<RepositorySubmitForm />);
    fireEvent.change(screen.getByPlaceholderText(/github\.com/i), { target: { value: URL_INPUT } });

    fireEvent.submit(screen.getByRole("button", { name: /submit|add|create/i }).closest("form")!);

    await waitFor(() => expect(repoApi.submitRepository).toHaveBeenCalled());
    const request = repoApi.submitRepository.mock.calls[0][0];
    expect(request).toMatchObject({ gitUrl: URL_INPUT, isPublic: true, languageCode: "en" });
    expect(request).not.toHaveProperty("gitConnectionId");
    expect(request).not.toHaveProperty("authAccount");
    expect(request).not.toHaveProperty("authPassword");
  });

  it("matches a connection to the repository server by scheme, host and port like the backend", async () => {
    const { isSameGitServer } = await import("./private-connection-picker");

    expect(isSameGitServer("https://GitHub.com/acme/api", "https://github.com")).toBe(true);
    expect(isSameGitServer("https://git.example.com:8443/a/b", "https://git.example.com:8443/base")).toBe(true);
    expect(isSameGitServer("https://git.example.com/a/b", "https://git.example.com:8443")).toBe(false);
    expect(isSameGitServer("http://github.com/acme/api", "https://github.com")).toBe(false);
    expect(isSameGitServer("git@github.com:acme/api.git", "https://github.com")).toBe(false);
  });
});
