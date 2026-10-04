import type { CatalogFilters } from "@/components/repositories/repository-catalog";
import type { WorkspaceState } from "@/components/repositories/repository-workspace";

const DEFAULT_FILTERS: CatalogFilters = { q: "", visibility: "all", sort: "updated" };
const VISIBILITIES: CatalogFilters["visibility"][] = ["all", "public", "private"];
const SORTS: CatalogFilters["sort"][] = ["updated", "updatedAsc", "name"];

/** Reads the workspace state from the URL. Only IDs, filters and sorting live there, never a secret. */
export function parseWorkspaceState(params: URLSearchParams): WorkspaceState {
  const access = params.get("access") as CatalogFilters["visibility"] | null;
  const sort = params.get("sort") as CatalogFilters["sort"] | null;
  return {
    connectionId: params.get("connection") || null,
    repositoryId: params.get("repo") || null,
    filters: {
      q: params.get("q") ?? DEFAULT_FILTERS.q,
      visibility: access && VISIBILITIES.includes(access) ? access : DEFAULT_FILTERS.visibility,
      sort: sort && SORTS.includes(sort) ? sort : DEFAULT_FILTERS.sort,
    },
  };
}

/** Builds the query string of a state. Values that equal the default stay out of the URL. */
export function serializeWorkspaceState(state: WorkspaceState): string {
  const params = new URLSearchParams();
  if (state.connectionId) params.set("connection", state.connectionId);
  if (state.connectionId && state.repositoryId) params.set("repo", state.repositoryId);
  if (state.filters.q) params.set("q", state.filters.q);
  if (state.filters.visibility !== DEFAULT_FILTERS.visibility) params.set("access", state.filters.visibility);
  if (state.filters.sort !== DEFAULT_FILTERS.sort) params.set("sort", state.filters.sort);
  return params.toString();
}
