import { beforeEach, describe, expect, it, vi } from "vitest";

const removeToken = vi.fn();

vi.mock("@/lib/env", () => ({
  getApiProxyUrl: () => "",
}));

vi.mock("@/lib/auth-api", () => ({
  getToken: () => "session-jwt",
  removeToken: () => removeToken(),
}));

import {
  addIndexedBranches,
  connectRepository,
  createConnection,
  findConnectedRepository,
  getErrorCode,
  listProviderBranches,
  listProviderRepositories,
  removeIndexedBranch,
  triggerBranchSync,
  updateConnection,
} from "@/lib/git-connections-api";

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function lastCall(fetchMock: ReturnType<typeof vi.spyOn>) {
  const [url, init] = fetchMock.mock.calls.at(-1) as [string, RequestInit];
  return { url, init, headers: init.headers as Record<string, string> };
}

describe("git-connections-api", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    removeToken.mockClear();
  });

  it("encodes connection id, cursor and page size and reuses the session token", async () => {
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockResolvedValue(jsonResponse({ success: true, data: { items: [], nextCursor: null } }));
    const controller = new AbortController();

    await listProviderRepositories("conn/1", { cursor: "a+b=/c", pageSize: 25, signal: controller.signal });

    const { url, init, headers } = lastCall(fetchMock);
    expect(url).toBe("/api/v1/git-connections/conn%2F1/repositories?cursor=a%2Bb%3D%2Fc&pageSize=25");
    expect(headers.Authorization).toBe("Bearer session-jwt");
    expect(init.signal).toBe(controller.signal);
  });

  it("omits an empty cursor and encodes the provider repository id of a branch request", async () => {
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockResolvedValue(jsonResponse({ success: true, data: { items: [], nextCursor: null } }));

    await listProviderBranches("c1", "group/sub repo", { pageSize: 100 });

    expect(lastCall(fetchMock).url).toBe(
      "/api/v1/git-connections/c1/repositories/group%2Fsub%20repo/branches?pageSize=100",
    );
  });

  it("sends catalog search, access and sort to the server", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse({ success: true, data: { items: [], nextCursor: null } }),
    );
    await listProviderRepositories("c1", { q: "group/api", visibility: "private", sort: "name", pageSize: 50 });
    expect(lastCall(fetchMock).url).toBe(
      "/api/v1/git-connections/c1/repositories?pageSize=50&q=group%2Fapi&visibility=private&sort=name",
    );
  });

  it("sends the token once on create and returns the existing flag without the token", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse(
        { success: true, existing: true, data: { id: "c1", displayName: "Ocean", hasSecret: true, canMaintain: true } },
        200,
      ),
    );

    const result = await createConnection({ provider: "GitLab", displayName: "Ocean", serverUrl: "https://git.example.com", token: "glpat-secret" });

    expect(JSON.parse(lastCall(fetchMock).init.body as string)).toEqual({
      provider: "GitLab",
      displayName: "Ocean",
      serverUrl: "https://git.example.com",
      token: "glpat-secret",
    });
    expect(result.existing).toBe(true);
    expect(JSON.stringify(result)).not.toContain("glpat-secret");
  });

  it("leaves the token key out of an update that has no replacement", async () => {
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockResolvedValue(jsonResponse({ success: true, data: { id: "c1" } }));

    await updateConnection("c1", { displayName: "New name", token: "" });

    expect(JSON.parse(lastCall(fetchMock).init.body as string)).toEqual({ displayName: "New name" });
  });

  it("keeps a rejected provider token (422) as an error with a stable code and does not sign out", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse({ success: false, errorCode: "PROVIDER_UNAUTHORIZED", message: "The provider rejected the token." }, 422),
    );

    const error = await listProviderRepositories("c1").catch((reason: unknown) => reason);

    expect(getErrorCode(error)).toBe("PROVIDER_UNAUTHORIZED");
    expect(removeToken).not.toHaveBeenCalled();
  });

  it("signs the user out on 401 through the shared client", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(jsonResponse({}, 401));

    await expect(listProviderRepositories("c1")).rejects.toMatchObject({ status: 401 });
    expect(removeToken).toHaveBeenCalledTimes(1);
  });

  it("posts the branch names and language of a connect request", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse({
        success: true,
        data: { repositoryId: "r1", orgName: "o", repoName: "n", gitConnectionId: "c1", repositoryCreated: true, branches: [] },
      }, 201),
    );

    await connectRepository({ connectionId: "c1", providerRepositoryId: "42", branches: ["main", "dev"], languageCode: "en" });

    const { url, init } = lastCall(fetchMock);
    expect(url).toBe("/api/v1/connected-repositories");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({
      connectionId: "c1",
      providerRepositoryId: "42",
      branches: ["main", "dev"],
      languageCode: "en",
    });
  });

  it("adds branches and triggers a sync on the repository routes", async () => {
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockImplementation(async () =>
        jsonResponse({ success: true, data: { repositoryId: "r1", branches: [] }, taskId: "t1", status: "Pending" }),
      );

    await addIndexedBranches("r/1", { branches: ["x"], languageCode: "zh" });
    expect(lastCall(fetchMock).url).toBe("/api/v1/repositories/r%2F1/indexed-branches");

    await triggerBranchSync("r1", "b1");
    expect(lastCall(fetchMock).url).toBe("/api/v1/repositories/r1/branches/b1/incremental-update");
  });

  it("exposes the 409 code of an active branch job", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse({ success: false, errorCode: "BRANCH_JOB_ACTIVE", message: "A job of this branch is running." }, 409),
    );

    const error = await removeIndexedBranch("r1", "b1").catch((reason: unknown) => reason);

    expect(error).toMatchObject({ status: 409 });
    expect(getErrorCode(error)).toBe("BRANCH_JOB_ACTIVE");
  });

  it("finds the local repository of a remote by connection and credential-free clone URL", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse({
        items: [
          { id: "other", orgName: "o", repoName: "x", gitUrl: "https://github.com/o/x.git", gitConnectionId: "c2" },
          { id: "r9", orgName: "acme", repoName: "api", gitUrl: "https://github.com/acme/api", gitConnectionId: "c1" },
        ],
        total: 2,
      }),
    );

    const found = await findConnectedRepository("c1", "https://github.com/Acme/API.git", "api");

    expect(lastCall(fetchMock).url).toBe("/api/v1/repositories/list?page=1&pageSize=50&keyword=api");
    expect(found).toEqual({ repositoryId: "r9", orgName: "acme", repoName: "api" });
  });
  it("keeps paging until it finds a repository that is beyond the first 50 keyword matches", async () => {
    const filler = (from: number) =>
      Array.from({ length: 50 }, (_, index) => ({
        id: `x${from + index}`,
        orgName: "acme",
        repoName: `api-${from + index}`,
        gitUrl: `https://github.com/acme/api-${from + index}.git`,
        gitConnectionId: "c1",
      }));
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockResolvedValueOnce(jsonResponse({ items: filler(0), total: 120 }))
      .mockResolvedValueOnce(jsonResponse({ items: filler(50), total: 120 }))
      .mockResolvedValueOnce(
        jsonResponse({
          items: [{ id: "r9", orgName: "acme", repoName: "api", gitUrl: "https://github.com/acme/api", gitConnectionId: "c1" }],
          total: 120,
        }),
      );
    const controller = new AbortController();

    const found = await findConnectedRepository("c1", "https://github.com/acme/api.git", "api", controller.signal);

    expect(found).toEqual({ repositoryId: "r9", orgName: "acme", repoName: "api" });
    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
      "/api/v1/repositories/list?page=1&pageSize=50&keyword=api",
      "/api/v1/repositories/list?page=2&pageSize=50&keyword=api",
      "/api/v1/repositories/list?page=3&pageSize=50&keyword=api",
    ]);
    expect((fetchMock.mock.calls[2][1] as RequestInit).signal).toBe(controller.signal);
  });

  it("stops when every keyword match was read and reports that the repository is not indexed", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(
      jsonResponse({
        items: [{ id: "o", orgName: "o", repoName: "api", gitUrl: "https://github.com/o/api.git", gitConnectionId: "c2" }],
        total: 1,
      }),
    );

    await expect(findConnectedRepository("c1", "https://github.com/acme/api.git", "api")).resolves.toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("stops on an empty page even when the reported total is larger", async () => {
    const fetchMock = vi
      .spyOn(globalThis, "fetch")
      .mockResolvedValue(jsonResponse({ items: [], total: 500 }));

    await expect(findConnectedRepository("c1", "https://github.com/acme/api.git", "api")).resolves.toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
