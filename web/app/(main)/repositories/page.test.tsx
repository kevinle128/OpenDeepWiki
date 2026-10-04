import { screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { renderWithIntl } from "@/test-utils/render-with-intl";

const replace = vi.fn();
const auth = vi.hoisted(() => ({ value: { isAuthenticated: false, isLoading: false } }));
let query = "";

vi.mock("next/navigation", () => ({
  useRouter: () => ({ replace }),
  usePathname: () => "/repositories",
  useSearchParams: () => new URLSearchParams(query),
}));
vi.mock("@/contexts/auth-context", () => ({ useAuth: () => auth.value }));
vi.mock("@/components/app-layout", () => ({
  AppLayout: ({ children }: { children: React.ReactNode }) => <div>{children}</div>,
}));
vi.mock("@/components/repositories/repository-workspace", () => ({
  RepositoryWorkspace: ({ state, onStateChange }: { state: unknown; onStateChange: (s: unknown) => void }) => (
    <button onClick={() => onStateChange({ connectionId: "c1", repositoryId: "42", filters: { q: "a b", visibility: "private", sort: "name" } })}>
      workspace {JSON.stringify(state)}
    </button>
  ),
}));

import RepositoriesPage from "./page";

describe("repositories page", () => {
  beforeEach(() => {
    replace.mockReset();
    query = "";
    auth.value = { isAuthenticated: false, isLoading: false };
  });

  it("redirects an anonymous visitor to the sign-in page", () => {
    renderWithIntl(<RepositoriesPage />);

    expect(replace).toHaveBeenCalledWith("/auth");
    expect(screen.queryByRole("button", { name: /workspace/ })).toBeNull();
  });

  it("waits for the session check before redirecting", () => {
    auth.value = { isAuthenticated: false, isLoading: true };
    renderWithIntl(<RepositoriesPage />);

    expect(replace).not.toHaveBeenCalled();
    expect(screen.getByText("Loading the workspace")).toBeTruthy();
  });

  it("restores selection and filters from the URL and writes only non-secret state back", () => {
    auth.value = { isAuthenticated: true, isLoading: false };
    query = "connection=c9&repo=7&q=api&sort=name";
    renderWithIntl(<RepositoriesPage />);

    const workspace = screen.getByRole("button", { name: /workspace/ });
    expect(workspace.textContent).toContain('"connectionId":"c9"');
    expect(workspace.textContent).toContain('"repositoryId":"7"');
    expect(workspace.textContent).toContain('"sort":"name"');

    workspace.click();
    expect(replace).toHaveBeenCalledWith("/repositories?connection=c1&repo=42&q=a+b&access=private&sort=name", { scroll: false });
  });

  it("ignores unknown filter values in the URL", () => {
    auth.value = { isAuthenticated: true, isLoading: false };
    query = "access=bogus&sort=bogus";
    renderWithIntl(<RepositoriesPage />);

    const text = screen.getByRole("button", { name: /workspace/ }).textContent;
    expect(text).toContain('"visibility":"all"');
    expect(text).toContain('"sort":"updated"');
  });
});
