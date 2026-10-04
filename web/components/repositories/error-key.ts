import { ApiError } from "@/lib/api-client";
import { getErrorCode } from "@/lib/git-connections-api";

const CODE_KEYS: Record<string, string> = {
  CONNECTION_NOT_FOUND: "connectionNotFound",
  CONNECTION_MAINTENANCE_FORBIDDEN: "maintenanceForbidden",
  CONNECTION_DISABLED: "connectionDisabled",
  CONNECTION_IN_USE: "connectionInUse",
  CONNECTION_CONFLICT: "conflict",
  CONNECTED_REPOSITORY_CONFLICT: "conflict",
  CONNECTION_ACCOUNT_MISMATCH: "accountMismatch",
  CONNECTION_SECRET_UNREADABLE: "secretUnreadable",
  INVALID_PROVIDER: "invalidProvider",
  INVALID_TOKEN: "invalidToken",
  INVALID_DISPLAY_NAME: "invalidDisplayName",
  SERVER_URL_INVALID: "serverUrlInvalid",
  SERVER_URL_BLOCKED: "serverUrlBlocked",
  PROVIDER_UNAUTHORIZED: "providerRejected",
  PROVIDER_FORBIDDEN: "providerForbidden",
  PROVIDER_NOT_FOUND: "providerNotFound",
  PROVIDER_RATE_LIMITED: "rateLimited",
  PROVIDER_TIMEOUT: "timeout",
  NO_BRANCHES_SELECTED: "noBranches",
  TOO_MANY_BRANCHES: "tooManyBranches",
  INVALID_BRANCH_NAME: "invalidBranchName",
  REMOTE_BRANCH_NOT_FOUND: "remoteBranchNotFound",
  REMOTE_BRANCH_LOOKUP_LIMIT: "remoteBranchLookupLimit",
  REPOSITORY_NOT_FOUND: "repositoryNotFound",
  BRANCH_NOT_FOUND: "branchNotFound",
  BRANCH_JOB_ACTIVE: "branchJobActive",
  BRANCH_GENERATION_ACTIVE: "branchJobActive",
  GENERATION_LOCK_CONFLICT: "generationActive",
  REPOSITORY_GENERATION_ACTIVE: "generationActive",
  LEGACY_CREDENTIAL_FIELDS_REJECTED: "legacyCredentials",
  GIT_URL_CONTAINS_CREDENTIALS: "urlCredentials",
  CONNECTION_HOST_MISMATCH: "hostMismatch",
  REPOSITORY_ALREADY_CONNECTED: "alreadyConnected",
};

/**
 * Maps a failed call to a key under `repositories.errors`. The backend text is never shown:
 * it is fixed per code today, but the code is the only contract.
 */
export function errorKey(error: unknown): string {
  const code = getErrorCode(error);
  if (code && CODE_KEYS[code]) return CODE_KEYS[code];

  if (error instanceof ApiError) {
    switch (error.status) {
      case 403:
        return "maintenanceForbidden";
      case 404:
        return "notFound";
      case 409:
        return "conflict";
      case 422:
        return "providerRejected";
      case 429:
        return "rateLimited";
      case 504:
        return "timeout";
      default:
        if (error.status >= 500) return "providerUnavailable";
    }
  }
  return "generic";
}
