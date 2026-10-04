import type { GitConnection, IndexedBranchSummary, RemoteBranch, RemoteRepository } from "@/types/git-connection";

export function makeConnection(overrides: Partial<GitConnection> = {}): GitConnection {
  return {
    id: "c1",
    provider: "GitHub",
    displayName: "Ocean Labs",
    serverUrl: "https://github.com",
    accountLogin: "ocean",
    repositoryCount: 28,
    state: "Healthy",
    isEnabled: true,
    lastValidatedAt: null,
    lastValidationErrorCode: null,
    createdAt: "2026-10-01T00:00:00Z",
    createdByUserId: "u1",
    canMaintain: true,
    hasSecret: true,
    ...overrides,
  };
}

export function makeRepository(overrides: Partial<RemoteRepository> = {}): RemoteRepository {
  return {
    providerRepositoryId: "100",
    name: "api",
    fullName: "acme/api",
    namespace: "acme",
    description: "Public API",
    cloneUrl: "https://github.com/acme/api.git",
    webUrl: "https://github.com/acme/api",
    defaultBranch: "main",
    visibility: "Private",
    updatedAt: "2026-10-02T10:00:00Z",
    ...overrides,
  };
}

export function makeBranch(name: string, isDefault = false): RemoteBranch {
  return { name, isDefault, commitSha: "abc1234" };
}

export function makeIndexedBranch(overrides: Partial<IndexedBranchSummary> = {}): IndexedBranchSummary {
  return {
    branchId: "b1",
    branchName: "main",
    lastCommitId: "abc1234",
    generationStatus: "Completed",
    lastGenerationTaskId: "t1",
    lastGenerationError: null,
    lastProcessedAt: "2026-10-02T10:00:00Z",
    languages: ["en"],
    activeTaskId: null,
    activeTaskKind: null,
    ...overrides,
  };
}

export function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
