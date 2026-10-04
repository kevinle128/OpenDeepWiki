import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/lib/api-client";
import { renderWithIntl } from "@/test-utils/render-with-intl";
import { deferred, makeConnection, makeRepository } from "./test-fixtures";

const api = vi.hoisted(() => ({ listProviderRepositories: vi.fn() }));

vi.mock("@/lib/git-connections-api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/git-connections-api")>()),
  ...api,
}));

import { RepositoryCatalog, type CatalogFilters } from "./repository-catalog";

const DEFAULT_FILTERS: CatalogFilters = { q: "", visibility: "all", sort: "updated" };

function Harness(props: Partial<React.ComponentProps<typeof RepositoryCatalog>> & { onFilters?: (f: CatalogFilters) => void }) {
  const [filters, setFilters] = useState<CatalogFilters>(DEFAULT_FILTERS);
  return (
    <RepositoryCatalog
      connection={makeConnection()}
      selectedId={null}
      onSelect={vi.fn()}
      filters={filters}
      onFiltersChange={(next) => {
        setFilters(next);
        props.onFilters?.(next);
      }}
      {...props}
    />
  );
}

const page = (names: string[], nextCursor: string | null = null) => ({
  items: names.map((name, index) => makeRepository({ providerRepositoryId: `${name}-${index}`, name, fullName: `acme/${name}` })),
  nextCursor,
});

describe("RepositoryCatalog", () => {
  beforeEach(() => api.listProviderRepositories.mockReset());

  it("loads the first page of the selected connection with a cancellation signal", async () => {
    api.listProviderRepositories.mockResolvedValue(page(["api", "web"]));
    renderWithIntl(<Harness />);

    const list = await screen.findByRole("list", { name: "Repository catalog" });
    expect(within(list).getAllByRole("button")).toHaveLength(2);
    expect(api.listProviderRepositories).toHaveBeenCalledWith(
      "c1",
      expect.objectContaining({ pageSize: 50, signal: expect.any(AbortSignal) }),
    );
  });

  it("ignores a slow answer of the previous connection and aborts its request", async () => {
    const slow = deferred<ReturnType<typeof page>>();
    api.listProviderRepositories.mockImplementation((id: string) =>
      id === "old" ? slow.promise : Promise.resolve(page(["fresh-repo"])),
    );
    const { rerender } = renderWithIntl(<Harness connection={makeConnection({ id: "old" })} />);
    await waitFor(() => expect(api.listProviderRepositories).toHaveBeenCalled());
    const firstSignal = api.listProviderRepositories.mock.calls[0][1].signal as AbortSignal;

    rerender(<Harness connection={makeConnection({ id: "new" })} />);
    expect(await screen.findByText("fresh-repo")).toBeTruthy();
    expect(firstSignal.aborted).toBe(true);

    slow.resolve(page(["stale-repo"]));
    await new Promise((resolve) => setTimeout(resolve, 10));
    expect(screen.queryByText("stale-repo")).toBeNull();
    expect(screen.getByText("fresh-repo")).toBeTruthy();
  });

  it("loads the next page with the cursor and appends it", async () => {
    api.listProviderRepositories
      .mockResolvedValueOnce(page(["api"], "cursor/1=="))
      .mockResolvedValueOnce(page(["web"]));
    renderWithIntl(<Harness />);

    fireEvent.click(await screen.findByRole("button", { name: "Load more repositories" }));

    expect(await screen.findByText("web")).toBeTruthy();
    expect(screen.getByText("api")).toBeTruthy();
    expect(api.listProviderRepositories.mock.calls[1][1]).toMatchObject({ cursor: "cursor/1==" });
    expect(screen.queryByRole("button", { name: "Load more repositories" })).toBeNull();
  });

  it("requests global search, access and sort and resets pagination", async () => {
    const onFilters = vi.fn();
    api.listProviderRepositories
      .mockResolvedValueOnce(page(["alpha", "beta", "zeta"], "old-cursor"))
      .mockResolvedValueOnce(page(["alpha", "beta", "zeta"]))
      .mockResolvedValueOnce(page(["alpha", "beta"]))
      .mockResolvedValueOnce(page(["beta"]));
    renderWithIntl(<Harness onFilters={onFilters} />);
    await screen.findByText("zeta");

    const names = () => within(screen.getByRole("list", { name: "Repository catalog" })).getAllByRole("button").map((b) => b.textContent ?? "");
    expect(names()[0]).toContain("alpha");

    fireEvent.change(screen.getByLabelText("Sort"), { target: { value: "name" } });
    await waitFor(() => expect(api.listProviderRepositories).toHaveBeenLastCalledWith("c1",
      expect.objectContaining({ sort: "name", visibility: "all", q: "" })));
    await screen.findByText("zeta");

    fireEvent.change(screen.getByLabelText("Access"), { target: { value: "private" } });
    await waitFor(() => expect(screen.queryByText("zeta")).toBeNull());

    fireEvent.change(screen.getByLabelText("Search repositories"), { target: { value: "BET" } });
    await waitFor(() => expect(names()).toHaveLength(1));
    expect(onFilters).toHaveBeenLastCalledWith({ q: "BET", visibility: "private", sort: "name" });
    expect(api.listProviderRepositories).toHaveBeenLastCalledWith("c1",
      expect.objectContaining({ q: "BET", visibility: "private", sort: "name" }));
    expect(api.listProviderRepositories.mock.calls[3][1].cursor).toBeUndefined();
  });

  it("selects a repository through a native button with a visible selected mark", async () => {
    const onSelect = vi.fn();
    api.listProviderRepositories.mockResolvedValue(page(["api"]));
    renderWithIntl(<Harness onSelect={onSelect} selectedId="api-0" />);

    const button = await screen.findByRole("button", { name: /api/ });
    expect(button.tagName).toBe("BUTTON");
    expect(button.getAttribute("aria-current")).toBe("true");
    expect(within(button).getByText("Selected")).toBeTruthy();

    fireEvent.click(button);
    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ providerRepositoryId: "api-0" }));
  });

  it.each([
    { q: "3q", visibility: "all" as const, message: "No repository matches the filters." },
    { q: "", visibility: "private" as const, message: "No repository matches the filters." },
    { q: "", visibility: "all" as const, message: "This connection has no repositories." },
  ])("uses the correct empty state for q=$q and access=$visibility", async ({ q, visibility, message }) => {
    api.listProviderRepositories.mockResolvedValue(page([]));
    renderWithIntl(<Harness filters={{ q, visibility, sort: "updated" }} />);

    expect(await screen.findByText(message)).toBeTruthy();
    expect(screen.queryByRole("list", { name: "Repository catalog" })).toBeNull();
  });

  it("makes no request for a disabled connection and explains why", () => {
    renderWithIntl(<Harness connection={makeConnection({ isEnabled: false, state: "Disabled" })} />);

    expect(api.listProviderRepositories).not.toHaveBeenCalled();
    expect(screen.getByText(/This connection is disabled, so repositories cannot be browsed/)).toBeTruthy();
    expect(screen.getByRole("link", { name: "Open existing documentation" })).toBeTruthy();
  });

  it("maps a rejected provider token (422) to a localized error with a retry", async () => {
    api.listProviderRepositories
      .mockRejectedValueOnce(new ApiError("x", 422, { success: false, errorCode: "PROVIDER_UNAUTHORIZED" }))
      .mockResolvedValueOnce(page(["api"]));
    renderWithIntl(<Harness />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("The provider rejected the token. Enter a valid token.");

    fireEvent.click(screen.getByRole("button", { name: "Try again" }));
    expect(await screen.findByText("api")).toBeTruthy();
    await waitFor(() => expect(api.listProviderRepositories).toHaveBeenCalledTimes(2));
  });
});
