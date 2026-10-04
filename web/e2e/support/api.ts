import { request as playwrightRequest, type APIRequestContext, type APIResponse } from "@playwright/test";

import { API_URL, WEB_URL, type TestUser } from "./env";

export interface Session {
  token: string;
  userId: string;
  roles: string[];
}

/** Signs in through the web proxy, exactly like the browser does, and returns the session. */
export async function login(user: TestUser): Promise<Session> {
  const context = await playwrightRequest.newContext({ baseURL: WEB_URL });
  try {
    const response = await context.post("/api/auth/login", { data: { email: user.email, password: user.password } });
    if (!response.ok()) throw new Error(`Sign in of ${user.email} failed with HTTP ${response.status()}`);
    const body = (await response.json()) as { data: { accessToken: string; user: { id: string; roles: string[] } } };
    return { token: body.data.accessToken, userId: body.data.user.id, roles: body.data.user.roles };
  } finally {
    await context.dispose();
  }
}

/** Creates the user through the web proxy. An existing user is fine. */
export async function ensureUser(user: TestUser): Promise<void> {
  const context = await playwrightRequest.newContext({ baseURL: WEB_URL });
  try {
    const response = await context.post("/api/auth/register", {
      data: { name: user.name, email: user.email, password: user.password, confirmPassword: user.password },
    });
    if (!response.ok() && response.status() !== 400) {
      throw new Error(`Registration of ${user.email} failed with HTTP ${response.status()}`);
    }
  } finally {
    await context.dispose();
  }
}

/** An API client for one signed-in user. Calls go through the web proxy, like the browser's calls. */
export async function apiAs(session: Session | null): Promise<APIRequestContext> {
  return playwrightRequest.newContext({
    baseURL: WEB_URL,
    extraHTTPHeaders: session ? { Authorization: `Bearer ${session.token}` } : {},
  });
}

/** Host control routes. They are reached directly, never through the web proxy. */
export async function control(): Promise<APIRequestContext> {
  return playwrightRequest.newContext({ baseURL: API_URL });
}

export async function json<T>(response: APIResponse): Promise<T> {
  const text = await response.text();
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new Error(`HTTP ${response.status()} ${response.url()} did not return JSON (${text.slice(0, 120) || "empty body"})`);
  }
}

export interface ProviderRule {
  account: string;
  /** user | repos | branches | repo | * */
  op: string;
  /** HTTP status to answer with, or 0 for the normal answer. */
  status?: number;
  delayMs?: number;
  retryAfterSeconds?: number;
  timeout?: boolean;
}

export async function setProviderRules(rules: ProviderRule[]): Promise<void> {
  const api = await control();
  try {
    const response = await api.put("/__e2e/rules", {
      data: rules.map((rule) => ({ status: 0, delayMs: 0, retryAfterSeconds: 0, timeout: false, ...rule })),
    });
    if (!response.ok()) throw new Error(`Setting provider rules failed with HTTP ${response.status()}`);
  } finally {
    await api.dispose();
  }
}

export async function setHold(enabled: boolean): Promise<void> {
  const api = await control();
  try {
    const response = await api.put("/__e2e/hold", { data: { enabled } });
    if (!response.ok()) throw new Error(`Setting the hold switch failed with HTTP ${response.status()}`);
  } finally {
    await api.dispose();
  }
}

/** While on, queued branch jobs stay Pending. */
export async function setPause(enabled: boolean): Promise<void> {
  const api = await control();
  try {
    const response = await api.put("/__e2e/pause", { data: { enabled } });
    if (!response.ok()) throw new Error(`Setting the pause switch failed with HTTP ${response.status()}`);
  } finally {
    await api.dispose();
  }
}

export interface HostRequest {
  sequence: number;
  method: string;
  path: string;
  status: number;
}

export async function hostRequests(): Promise<HostRequest[]> {
  const api = await control();
  try {
    return await json<HostRequest[]>(await api.get("/__e2e/requests"));
  } finally {
    await api.dispose();
  }
}

export interface ProviderCall {
  host: string;
  path: string;
  account: string | null;
  status: number;
}

export async function providerCalls(): Promise<ProviderCall[]> {
  const api = await control();
  try {
    return await json<ProviderCall[]>(await api.get("/__e2e/provider-calls"));
  } finally {
    await api.dispose();
  }
}

export interface HostInfo {
  workDir: string;
  databasePath: string | null;
  unfakedProviderCalls: number;
}

export async function hostInfo(): Promise<HostInfo> {
  const api = await control();
  try {
    return await json<HostInfo>(await api.get("/__e2e/info"));
  } finally {
    await api.dispose();
  }
}

/** A short account name that is unique in this run, so tests never share provider identities. */
let accountCounter = 0;
export function uniqueAccount(prefix: string): string {
  accountCounter += 1;
  const stamp = Date.now().toString(36).slice(-4);
  return `${prefix}${stamp}${accountCounter}`.toLowerCase().replace(/[^a-z0-9]/g, "");
}

export function tokenFor(account: string): string {
  return `e2e-${account}-${Math.random().toString(36).slice(2, 10)}A1`;
}

export interface ConnectionDto {
  id: string;
  provider: string;
  displayName: string;
  serverUrl: string;
  accountLogin: string | null;
  state: string;
  isEnabled: boolean;
  canMaintain: boolean;
  hasSecret: boolean;
  createdByUserId: string;
}

export interface CreateConnectionOptions {
  provider?: "GitHub" | "GitLab";
  serverUrl?: string;
  displayName?: string;
}

/** Creates a connection through the API. Use it to prepare a test that is not about the creation form. */
export async function createConnectionViaApi(
  session: Session,
  account: string,
  options: CreateConnectionOptions = {},
): Promise<ConnectionDto> {
  const api = await apiAs(session);
  try {
    const response = await api.post("/api/v1/git-connections", {
      data: {
        provider: options.provider ?? "GitHub",
        displayName: options.displayName ?? `Account ${account}`,
        token: tokenFor(account),
        ...(options.serverUrl ? { serverUrl: options.serverUrl } : {}),
      },
    });
    if (!response.ok()) throw new Error(`Creating the connection failed with HTTP ${response.status()}`);
    return (await json<{ data: ConnectionDto }>(response)).data;
  } finally {
    await api.dispose();
  }
}

export interface ConnectedRepositoryDto {
  repositoryId: string;
  orgName: string;
  repoName: string;
  branches: { branchId: string; branchName: string; created: boolean; taskId: string | null; taskStatus: string | null }[];
}

/** Indexes the repository with the given number (1-based) of the account's fake catalog. */
export async function connectRepositoryViaApi(
  session: Session,
  connection: ConnectionDto,
  repositoryNumber: number,
  branches: string[],
): Promise<ConnectedRepositoryDto> {
  const api = await apiAs(session);
  try {
    const page = await api.get(`/api/v1/git-connections/${connection.id}/repositories?pageSize=100`);
    const items = (await json<{ data: { items: { providerRepositoryId: string; name: string }[] } }>(page)).data.items;
    const wanted = `svc-${String(repositoryNumber).padStart(3, "0")}`;
    const remote = items.find((item) => item.name === wanted);
    if (!remote) throw new Error(`The fake catalog has no repository ${wanted}`);
    const response = await api.post("/api/v1/connected-repositories", {
      data: { connectionId: connection.id, providerRepositoryId: remote.providerRepositoryId, branches, languageCode: "en" },
    });
    if (!response.ok()) throw new Error(`Connecting the repository failed with HTTP ${response.status()}`);
    return (await json<{ data: ConnectedRepositoryDto }>(response)).data;
  } finally {
    await api.dispose();
  }
}
