import { api, ApiError } from "./api-client";
import type {
  AddIndexedBranchesRequest,
  BranchSyncResult,
  ConnectRepositoryRequest,
  ConnectedRepositoryRef,
  ConnectedRepositoryResult,
  CreateGitConnectionRequest,
  CreateGitConnectionResult,
  GitConnection,
  GitConnectionHealth,
  IndexedBranchSummary,
  ProviderPage,
  RemoteBranch,
  RemoteRepository,
  RemoveIndexedBranchResult,
  UpdateGitConnectionRequest,
} from "@/types/git-connection";
import type { RepositoryListResponse } from "@/types/repository";

interface Envelope<T> {
  success: boolean;
  data: T;
  existing?: boolean;
}

export interface PageRequest {
  cursor?: string | null;
  pageSize?: number;
  signal?: AbortSignal;
  q?: string;
  visibility?: "all" | "public" | "private";
  sort?: "updated" | "updatedAsc" | "name";
}

const CONNECTIONS = "/api/v1/git-connections";
const seg = encodeURIComponent;

function pageQuery({ cursor, pageSize, q, visibility, sort }: PageRequest): string {
  const params = new URLSearchParams();
  if (cursor) params.set("cursor", cursor);
  if (pageSize) params.set("pageSize", String(pageSize));
  if (q !== undefined) params.set("q", q);
  if (visibility !== undefined) params.set("visibility", visibility);
  if (sort !== undefined) params.set("sort", sort);
  const query = params.toString();
  return query ? `?${query}` : "";
}

/** Stable backend error code of a failed call, or null when the failure has none (network, abort). */
export function getErrorCode(error: unknown): string | null {
  if (error instanceof ApiError && error.data && typeof error.data === "object") {
    const code = (error.data as { errorCode?: unknown }).errorCode;
    return typeof code === "string" ? code : null;
  }
  return null;
}

/** Branch names the backend reported on a failed connect or add request. */
export function getErrorBranches(error: unknown): string[] {
  if (error instanceof ApiError && error.data && typeof error.data === "object") {
    const branches = (error.data as { branches?: unknown }).branches;
    if (Array.isArray(branches)) return branches.filter((item): item is string => typeof item === "string");
  }
  return [];
}

export function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === "AbortError";
}

export async function listConnections(signal?: AbortSignal): Promise<GitConnection[]> {
  return (await api.get<Envelope<GitConnection[]>>(CONNECTIONS, { signal })).data;
}

export async function createConnection(request: CreateGitConnectionRequest): Promise<CreateGitConnectionResult> {
  const body: CreateGitConnectionRequest = {
    provider: request.provider,
    displayName: request.displayName,
    token: request.token,
    ...(request.serverUrl ? { serverUrl: request.serverUrl } : {}),
  };
  const response = await api.post<Envelope<GitConnection>>(CONNECTIONS, body);
  return { connection: response.data, existing: response.existing === true };
}

/** The token key is sent only when the caller passes a non-empty replacement. */
export async function updateConnection(id: string, request: UpdateGitConnectionRequest): Promise<GitConnection> {
  const body: UpdateGitConnectionRequest = {};
  if (request.displayName !== undefined) body.displayName = request.displayName;
  if (request.token) body.token = request.token;
  return (await api.put<Envelope<GitConnection>>(`${CONNECTIONS}/${seg(id)}`, body)).data;
}

export async function deleteConnection(id: string): Promise<void> {
  await api.delete(`${CONNECTIONS}/${seg(id)}`);
}

export async function testConnection(id: string): Promise<GitConnectionHealth> {
  return (await api.post<Envelope<GitConnectionHealth>>(`${CONNECTIONS}/${seg(id)}/test`)).data;
}

export async function setConnectionEnabled(id: string, enabled: boolean): Promise<GitConnection> {
  const action = enabled ? "enable" : "disable";
  return (await api.post<Envelope<GitConnection>>(`${CONNECTIONS}/${seg(id)}/${action}`)).data;
}

export async function listProviderRepositories(
  connectionId: string,
  request: PageRequest = {},
): Promise<ProviderPage<RemoteRepository>> {
  const response = await api.get<Envelope<ProviderPage<RemoteRepository>>>(
    `${CONNECTIONS}/${seg(connectionId)}/repositories${pageQuery(request)}`,
    { signal: request.signal },
  );
  return response.data;
}

export async function listProviderBranches(
  connectionId: string,
  providerRepositoryId: string,
  request: PageRequest = {},
): Promise<ProviderPage<RemoteBranch>> {
  const response = await api.get<Envelope<ProviderPage<RemoteBranch>>>(
    `${CONNECTIONS}/${seg(connectionId)}/repositories/${seg(providerRepositoryId)}/branches${pageQuery(request)}`,
    { signal: request.signal },
  );
  return response.data;
}

export async function connectRepository(request: ConnectRepositoryRequest): Promise<ConnectedRepositoryResult> {
  return (await api.post<Envelope<ConnectedRepositoryResult>>("/api/v1/connected-repositories", request)).data;
}

export async function addIndexedBranches(
  repositoryId: string,
  request: AddIndexedBranchesRequest,
): Promise<ConnectedRepositoryResult> {
  return (
    await api.post<Envelope<ConnectedRepositoryResult>>(`/api/v1/repositories/${seg(repositoryId)}/indexed-branches`, request)
  ).data;
}

export async function listIndexedBranches(repositoryId: string, signal?: AbortSignal): Promise<IndexedBranchSummary[]> {
  return (
    await api.get<Envelope<IndexedBranchSummary[]>>(`/api/v1/repositories/${seg(repositoryId)}/indexed-branches`, { signal })
  ).data;
}

export async function removeIndexedBranch(repositoryId: string, branchId: string): Promise<RemoveIndexedBranchResult> {
  return (
    await api.delete<Envelope<RemoveIndexedBranchResult>>(
      `/api/v1/repositories/${seg(repositoryId)}/indexed-branches/${seg(branchId)}`,
    )
  ).data;
}

export async function triggerBranchSync(repositoryId: string, branchId: string): Promise<BranchSyncResult> {
  return api.post<BranchSyncResult>(
    `/api/v1/repositories/${seg(repositoryId)}/branches/${seg(branchId)}/incremental-update`,
  );
}

function normalizeCloneUrl(value: string): string {
  try {
    const url = new URL(value);
    const path = url.pathname.replace(/\/+$/, "").replace(/\.git$/i, "");
    return `${url.protocol}//${url.host}${path}`.toLowerCase();
  } catch {
    return value.trim().toLowerCase();
  }
}

const LOOKUP_PAGE_SIZE = 50;
/** A guard against a backend that keeps answering with full pages. 100 pages are 5000 repositories of one keyword. */
const LOOKUP_MAX_PAGES = 100;

/**
 * The catalog knows remotes, the indexed-branch routes know local repositories. The repository list is the only
 * route that joins them, so the match uses the connection ID and the credential-free clone URL. The list has no
 * connection filter, so the lookup reads every page of the keyword matches until it finds the repository.
 */
export async function findConnectedRepository(
  connectionId: string,
  cloneUrl: string,
  repoName: string,
  signal?: AbortSignal,
): Promise<ConnectedRepositoryRef | null> {
  const wanted = normalizeCloneUrl(cloneUrl);
  for (let page = 1; page <= LOOKUP_MAX_PAGES; page++) {
    const params = new URLSearchParams({ page: String(page), pageSize: String(LOOKUP_PAGE_SIZE), keyword: repoName });
    const list = await api.get<RepositoryListResponse>(`/api/v1/repositories/list?${params.toString()}`, { signal });
    const match = list.items.find(
      (item) => item.gitConnectionId === connectionId && normalizeCloneUrl(item.gitUrl) === wanted,
    );
    if (match) return { repositoryId: match.id, orgName: match.orgName, repoName: match.repoName };
    if (list.items.length === 0 || page * LOOKUP_PAGE_SIZE >= list.total) return null;
  }
  return null;
}
