# Git connection delivery review

Date: 2026-10-04.
Scope: the delivered shared Git connection, repository catalog, and branch workflow.
The workspace has no Git metadata, so this review uses current source and the accepted plan, not a commit diff.
No production source or live database was changed.

## Verdict

The delivery does not yet meet the accepted plan.
The private-repository shared-user workflow fails in the test application.
Required synchronization controls and catalog-wide search are absent.

## Findings

### High: Shared users cannot manage branches of private repositories

`RepositoryReadAccess.VisibleTo` permits only public repositories, the owner, or Admin (`src/OpenDeepWiki/Services/Repositories/RepositoryReadAccess.cs:22`).
`ConnectedRepositoryService.AddIndexedBranchesAsync` and `ListIndexedBranchesAsync` apply that rule (`src/OpenDeepWiki/Services/Repositories/ConnectedRepositoryService.cs:61`, `:84`).
Branch removal and generation endpoints use the same restriction.
This contradicts the accepted Shared Workspace branch capability.

Reproduction used the running disposable E2E application through the Next.js proxy.
Alice created a connection and indexed fake private repository `svc-002`.
Alice's branch-list call returned 200.
Bob's branch-list and add-`develop` calls both returned 404 with `REPOSITORY_NOT_FOUND`.
The existing shared-user E2E uses repository number 1 (`web/e2e/repository-workspace-permissions.spec.ts:57`).
That repository is public; only even numbers are private (`tests/OpenDeepWiki.E2EHost/FakeGitProviders.cs:287`).
Add a private-repository shared-user E2E and apply the accepted branch capability without broadening repository deletion or visibility mutation.

### High: Per-repository automatic synchronization cannot be configured

The connect and add-branch DTOs have no auto-sync setting (`src/OpenDeepWiki/Models/ConnectedRepositories/ConnectedRepositoryModels.cs:8`, `:18`).
The scheduled worker selects every completed, non-deleted repository based on its interval (`src/OpenDeepWiki/Services/Repositories/IncrementalUpdateWorker.cs:376`).
There is no user switch to opt an indexed repository out of automatic synchronization.
The UI offers no corresponding control.
The accepted plan requires this setting; declaring that no backend field exists does not satisfy it.

### Medium: Catalog search and sorting apply only to loaded pages

`filterAndSort` operates on local items (`web/components/repositories/repository-catalog.tsx:43`).
The provider request carries only a page cursor and page size (`:123`), and the visible rows use only accumulated items (`:146`).
Searching for a repository on an unloaded page can show no result even though the repository exists.
The UI labels the limitation, but the accepted server search, filter, and sort requirement remains unmet.

### High: CI permits delivery with failing full tests and lint

The backend gate excludes seven tests (`.github/workflows/quality.yml:29`) and runs them with `continue-on-error` (`:88`).
Full frontend lint also uses `continue-on-error` (`:138`).
Fresh checks reproduced all seven backend failures and 78 frontend lint errors.
The failures match the cook report's baseline list, but no Git history is available to independently prove their origin.
The repository rules require these failures to be resolved rather than hidden from the delivery gate.

### Medium: Existing self-hosted GitLab credentials cannot be backfilled

Legacy provider classification recognizes only `github.com` and `gitlab.com` (`src/OpenDeepWiki/Services/GitConnections/LegacyGitCredentialMigrationService.cs:616`).
Other hosts become unsupported and block the contract.
This preserves old data, which is safe, but leaves the user's existing self-hosted GitLab repository outside the protected-connection migration.
Provide an explicit provider or existing-connection assignment for approved self-hosted hosts rather than assuming every unknown host is GitLab.

### High: Clone and fetch address checks have a DNS race

The Git origin guard resolves and validates allowed addresses (`src/OpenDeepWiki/Services/Repositories/GitRemoteOriginGuard.cs:46`).
It does not pass those addresses to libgit2.
The clone and fetch calls resolve the hostname again; the credential callback checks origin text only (`src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs:1485`).
Provider HTTP calls have a stronger transport guard, but Git operations do not pin the validated destination.
A DNS answer can change between policy validation and connection.
This is a source-level finding; this review did not run a DNS-rebinding exploit.
Close the transport boundary or document and enforce an equivalent network restriction before claiming the plan's SSRF gate is complete.

## Fresh verification

- Focused backend: 575 passed, 10 skipped, 0 failed.
- Full backend: 1359 passed, 17 skipped, 7 failed.
- Backend build: 0 warnings, 0 errors.
- Frontend unit: 98 passed across 14 files; React `act` warning remains.
- TypeScript: passed.
- Eight-locale key parity: passed.
- Reviewed workspace lint: passed.
- Full frontend lint: 78 errors and 66 warnings.
- First browser E2E run: 25 passed, 1 failed in provider-error selection.
The private-sharing reproduction added a fixture during this run, so this result is not treated as an isolated clean-suite failure.
The provider-error test passed on an isolated rerun, together with four evidence gates.
A clean full rerun is recorded below.
- Clean full browser E2E rerun: 26 passed in 3.5 minutes.
The runner used the production frontend build and its disposable API host.
- Private shared-user reproduction: failed as described above.

## Limits

PostgreSQL integration tests that need an external database were skipped in this run.
No migration, deployment, or real GitHub or GitLab account was exercised.
The E2E host substitutes the branch processor and refuses real clone and fetch operations.
Passing its browser suite does not prove real repository indexing.

## Unresolved questions

None is needed to explain these findings.
The accepted plan already defines the missing behavior.
