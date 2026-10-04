/** DTOs of the shared Git connection and connected repository APIs. Secrets never appear in any of them. */

export type GitProviderKind = "GitHub" | "GitLab";

export type GitConnectionState = "Healthy" | "Warning" | "Disabled";

export interface GitConnection {
  id: string;
  provider: GitProviderKind;
  displayName: string;
  serverUrl: string;
  accountLogin: string;
  repositoryCount: number;
  state: GitConnectionState;
  isEnabled: boolean;
  lastValidatedAt?: string | null;
  lastValidationErrorCode?: string | null;
  createdAt: string;
  createdByUserId: string;
  /** Set by the backend only. The UI never derives it from a role. */
  canMaintain: boolean;
  hasSecret: boolean;
}

export interface CreateGitConnectionRequest {
  provider: GitProviderKind;
  displayName: string;
  /** Only for self-hosted GitLab. */
  serverUrl?: string;
  token: string;
}

export interface UpdateGitConnectionRequest {
  displayName?: string;
  /** Present only when the user typed a replacement token. */
  token?: string;
}

export interface CreateGitConnectionResult {
  connection: GitConnection;
  /** True when the backend found a connection for the same account instead of creating one. */
  existing: boolean;
}

export interface GitConnectionHealth {
  ok: boolean;
  state: string;
  errorCode?: string | null;
  latencyMs: number;
  checkedAt: string;
}

export interface ProviderPage<T> {
  items: T[];
  nextCursor?: string | null;
  totalCount?: number | null;
}

export interface RemoteRepository {
  providerRepositoryId: string;
  name: string;
  fullName: string;
  namespace?: string | null;
  description?: string | null;
  cloneUrl: string;
  webUrl?: string | null;
  defaultBranch?: string | null;
  visibility: string;
  updatedAt?: string | null;
}

export interface RemoteBranch {
  name: string;
  isDefault: boolean;
  commitSha?: string | null;
}

export interface ConnectRepositoryRequest {
  connectionId: string;
  providerRepositoryId: string;
  branches: string[];
  languageCode: string;
  /** Applies when the repository is created. An indexed repository keeps its setting. */
  generateSkill?: boolean;
}

export interface AddIndexedBranchesRequest {
  branches: string[];
  languageCode: string;
}

export interface IndexedBranchResult {
  branchId: string;
  branchName: string;
  created: boolean;
  taskId?: string | null;
  taskStatus?: string | null;
}

export interface ConnectedRepositoryResult {
  repositoryId: string;
  orgName: string;
  repoName: string;
  gitConnectionId: string;
  repositoryCreated: boolean;
  branches: IndexedBranchResult[];
}

export interface IndexedBranchSummary {
  branchId: string;
  branchName: string;
  lastCommitId?: string | null;
  generationStatus?: string | null;
  lastGenerationTaskId?: string | null;
  lastGenerationError?: string | null;
  lastProcessedAt?: string | null;
  languages: string[];
  activeTaskId?: string | null;
  activeTaskKind?: string | null;
}

export interface RemoveIndexedBranchResult {
  repositoryId: string;
  branchId: string;
  branchName: string;
  workspaceRemoved: boolean;
}

export interface BranchSyncResult {
  success: boolean;
  taskId: string;
  status: string;
}

/** A local repository that belongs to a connection, found through the repository list. */
export interface ConnectedRepositoryRef {
  repositoryId: string;
  orgName: string;
  repoName: string;
}
