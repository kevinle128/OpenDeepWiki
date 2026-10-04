# Red Team Review

## Scope

Four independent lenses reviewed `plan.md` and all six phase files against the current source.
The lenses were Security Adversary, Failure Mode Analyst, Assumption Destroyer, and Scope and Complexity Critic.

## Adjudicated findings

| # | Finding | Severity | Disposition | Applied to |
|---:|---|---|---|---|
| 1 | Production can use the built-in JWT signing key. | Critical | Accept | Phase 1 |
| 2 | Legacy discovery and task-read endpoints can expose private metadata without authentication. | High | Accept | Phases 2 and 3 |
| 3 | Clone and fetch need execution-time SSRF checks before PAT attachment. | High | Accept | Phase 3 |
| 4 | Legacy Git URLs can contain userinfo credentials. | High | Accept | Phase 4 |
| 5 | GitHub App and PAT imports can create duplicate repository rows. | High | Accept | Phase 3 |
| 6 | Gitee and unknown-host credentials have no safe contract migration. | Critical | Accept | Plan and Phase 4 |
| 7 | The old repository path unique index conflicts with stable provider identity. | Critical | Accept | Phase 3 |
| 8 | Branch removal did not name all branch-local records. | High | Accept | Phase 3 |
| 9 | The repository and branch lock schema was not exact. | High | Accept | Phase 3 |
| 10 | Admin maintenance used stale JWT role claims. | High | Accept | Phases 1 and 4 |
| 11 | Branch actions lacked actor attribution. | Medium | Accept | Phase 3 |
| 12 | Provider visibility refresh assumed GitHub. | High | Accept | Phase 3 |
| 13 | Remove and re-add of the same branch lacked a test. | High | Accept | Phase 3 |
| 14 | Playwright did not define the Next.js proxy connection. | High | Accept | Phase 6 |
| 15 | The user-visible activity feed exceeded the requested workflow. | Medium | Accept | Phase 5 |

## Rejected or merged observations

- The concern about shared repository ownership was merged into the explicit rule that branch capability is shared while existing owner-only delete and visibility rules stay unchanged.
- The proposed fixed protected-token size of exactly 4096 was not adopted as a storage ceiling.
The plan now requires unbounded text or at least 4096 characters with boundary tests.
- Real provider calls in Playwright were rejected.
Provider security behavior belongs in deterministic integration tests, while Playwright checks the user-visible state.

## Evidence highlights

- `src/OpenDeepWiki/Program.cs` contains the current fallback JWT signing key.
- `src/OpenDeepWiki/Endpoints/IncrementalUpdateEndpoints.cs` and `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs` expose current task routes without a complete authenticated boundary.
- `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs` performs clone and fetch operations and currently has credential and TLS-sensitive paths.
- `src/OpenDeepWiki.EFCore/MasterDbContext.cs` contains the current repository path and repository-wide lock constraints.
- `src/OpenDeepWiki/Services/GitHub/UserGitHubImportService.cs` and `src/OpenDeepWiki/Services/Admin/AdminGitHubImportService.cs` deduplicate by clone URL.
- `web/app/api/[...path]/route.ts` requires `API_PROXY_URL` for the frontend proxy.

## Result

All accepted findings are now explicit requirements, tests, or compatibility gates in the owning phase.
The plan is ready for final verification.
