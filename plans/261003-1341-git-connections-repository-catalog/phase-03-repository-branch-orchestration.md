---
title: "Phase 3: Repository and branch orchestration"
status: completed
phase: 3
plan: "git-connections-repository-catalog"
depends_on: [1, 2]
scope: "repository identity, branch jobs, locks, branch removal"
test_strategy: "TDD with SQLite transactions, worker tests, endpoint tests, and file-system isolation tests"
---

# Phase 3: Repository and branch orchestration

## Context

Connect provider catalog entries to the existing repository, full-generation, and incremental-update pipeline.
Use the [pipeline research](./research/researcher-02-git-pipeline.md) and [Phase 3 scout report](./reports/scout-phase-03-branch-orchestration.md).
Reuse the existing `Repository`, `RepositoryBranch`, generation tasks, workers, and per-branch workspaces.

### Inherited from earlier phases

- Repository assignment must reject deleted or disabled connections inside its own transaction. The connection delete check is not atomic against a concurrent assignment.
- `GitConnection.ConcurrencyStamp` is the concurrency token.
- Phase 2 follow-ups to apply first as small fixes with tests: set `UseProxy = false` on the guard handler primary handler; treat 192.0.0.0/24 as non-public; unwrap NAT64 local-use 64:ff9b:1::/48 and SIIT ::ffff:0:a.b.c.d; add an HTTP-level 401 test for the legacy branches route.
- Do not change the creator-on-restore behavior; it awaits a user decision.

## Requirements

- Enforce one Connected Repository per `(Provider, NormalizedServerUrl, ProviderRepositoryId)`.
- Create or reuse one repository, then add multiple branches atomically.
- Create one independent full-generation task for each new branch.
- Allow full and incremental jobs on different branches of the same repository to run independently.
- Keep repository-level jobs mutually exclusive with every branch job for that repository.
- Model locks with nullable `BranchId`: one active repository-scope lock per repository and one active branch-scope lock per repository and branch.
- Require authentication for add, full, incremental, retry, cancel, rebuild, remove, task-status, and task-list actions.
- Allow every authenticated user to manage branches.
- Keep repository delete, visibility, assignment, and connection-maintenance authorization unchanged.
- On branch removal, cancel pending work, return 409 for processing work, remove only branch-local data, and preserve repository and connection records.
- Remove clone and fetch certificate bypasses.
- Use the active connection credential at job execution time.
- Revalidate the Git remote origin, DNS result, redirect behavior, and allowed address policy at clone and fetch time before attaching a PAT.
- Route GitHub App imports through a compatibility adapter that assigns stable provider identity and converges with PAT imports.
- Replace the old `(OrgName, RepoName)` global uniqueness rule so equal paths on different providers can coexist.
- Keep the existing repository owner for delete and visibility rules; shared authenticated users receive branch capability only.
- Record the authenticated actor for every add, sync, rebuild, retry, cancel, and remove action.

## File inventory

| Path | Action | Rough size | Test impact |
|---|---|---:|---|
| `src/OpenDeepWiki.Entities/Repositories/Repository.cs` | Add stable provider identity and refreshed metadata fields. | M | Identity tests. |
| `src/OpenDeepWiki.Entities/Repositories/RepositoryGenerationLock.cs` | Add nullable `BranchId`. | S | Lock matrix tests. |
| `src/OpenDeepWiki.EFCore/MasterDbContext.cs` | Add remote-key, active-branch, and branch-lock filtered indexes. | M | SQLite constraint tests. |
| `src/OpenDeepWiki/Services/Repositories/IConnectedRepositoryService.cs` | Create connect and add-branches contract. | S | Service fake seam. |
| `src/OpenDeepWiki/Services/Repositories/ConnectedRepositoryService.cs` | Implement remote validation, transaction, get-or-create, branch tasks, and idempotency. | L | Core orchestration tests. |
| `src/OpenDeepWiki/Services/Repositories/IIndexedBranchRemovalService.cs` | Create branch-only removal contract. | S | Endpoint seam. |
| `src/OpenDeepWiki/Services/Repositories/IndexedBranchRemovalService.cs` | Remove branch languages, catalogs, files, translation tasks, incremental tasks, generation tasks, graph artifacts, processing logs, and the safe branch workspace after commit. | L | Removal and failure tests. |
| `src/OpenDeepWiki/Models/ConnectedRepositories/ConnectedRepositoryModels.cs` | Add multi-branch requests and per-branch results. | M | Contract tests. |
| `src/OpenDeepWiki/Endpoints/ConnectedRepositoryEndpoints.cs` | Add connect, add, list, and remove routes with authentication. | M | HTTP tests. |
| `src/OpenDeepWiki/Services/Repositories/RepositoryGenerationLockService.cs` | Implement repository/branch conflict matrix and precise release/heartbeat. | L | Concurrency tests. |
| `src/OpenDeepWiki/Services/Repositories/BranchGenerationTaskService.cs` | Check branch ownership, deduplicate by branch, and reserve branch lock. | M | Existing test updates. |
| `src/OpenDeepWiki/Services/Repositories/BranchGenerationWorker.cs` | Claim and heartbeat the branch lease. | M | Worker tests. |
| `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateService.cs` | Validate branch relation, use branch lock, and handle deleted files or full fallback. | L | Incremental tests. |
| `src/OpenDeepWiki/Services/Repositories/IncrementalUpdateWorker.cs` | Use branch leases and connection credentials for scheduled work. | M | Worker tests. |
| `src/OpenDeepWiki/Services/Repositories/RepositoryAnalyzer.cs` | Use resolver everywhere and remove `CertificateCheck = true`. | M | TLS and credential tests. |
| `src/OpenDeepWiki/Endpoints/BranchGenerationEndpoints.cs` | Replace the legacy restrictive branch gate with authenticated branch capability only. | M | Authorization tests. |
| `src/OpenDeepWiki/Endpoints/IncrementalUpdateEndpoints.cs` | Require authentication and stable errors. | M | Authorization tests. |
| `src/OpenDeepWiki/Infrastructure/DbInitializer.cs` | Add paired idempotent remote identity and lock schema upgrades. | M | Upgrade tests. |
| SQLite/PostgreSQL migrations and snapshots | Add remote identity and branch-aware lock schema. | M | Provider parity. |
| `tests/OpenDeepWiki.Tests/Services/Repositories/ConnectedRepositoryServiceTests.cs` | Create atomicity and idempotency tests. | L | New red tests. |
| `tests/OpenDeepWiki.Tests/Services/Repositories/IndexedBranchRemovalServiceTests.cs` | Create scoped cleanup and 409 tests. | L | New red tests. |
| Existing branch task, lock, worker, analyzer, and endpoint tests | Update permission and branch identity expectations. | L | Regression coverage. |

## Test scenario matrix

| Area | Red test | Expected result |
|---|---|---|
| Repository identity | Two connections select the same stable remote | One repository row. |
| Remote rename | Provider name and clone URL change | Same repository ID; metadata refreshes. |
| Multi-select | Three valid branches | Three branches, languages, and full tasks in one commit. |
| Atomicity | One selected branch is invalid | No repository, branch, or task partial write. |
| Idempotency | Existing and new branches are mixed | Only new branches get tasks. |
| Authorization | Anonymous branch mutation | 401. |
| Authorization | Anonymous task list or task status read | 401. |
| Authorization | Authenticated non-owner branch mutation | Allowed. |
| Authorization safety | Same user deletes repository or changes visibility | Existing restrictive rule still applies. |
| Full dedupe | Same branch enqueued twice | One active task. |
| Independence | Two branches enqueue and execute | Two tasks and two branch locks can run. |
| Lock conflict | Repository lock and any branch lock | Mutually exclusive. |
| Relation | Branch ID belongs to another repository | 404 and no task. |
| Incremental delete | Source file is deleted | Index deletion is applied, or that branch takes an explicit full fallback. |
| Removal pending | Pending full or incremental task exists | Cancel task, release lock, and remove branch. |
| Removal processing | Processing task exists | 409 and no data change. |
| Removal isolation | Other branches and repository-wide data exist | Preserved. |
| Re-add | A removed branch is selected again | The inactive row is restored or replaced without a unique-key failure. |
| Provider path | GitHub and GitLab both contain `foo/bar` | Two remote identities coexist. |
| Import convergence | GitHub App and PAT select the same remote | One repository row. |
| Workspace failure | Post-commit directory deletion fails | Database stays committed and cleanup is retryable. |
| TLS | Clone or fetch sees an invalid certificate | Operation fails. |

## Function and interface checklist

- [x] `ConnectedRepositoryService.ConnectAsync` gets or creates by stable remote key.
- [x] `AddIndexedBranchesAsync` validates every remote branch before opening the write transaction.
- [x] `BranchGenerationTaskService.FindActiveBranchTaskAsync` filters by `BranchId`.
- [x] `EnqueueFullGenerationAsync` proves that branch belongs to repository.
- [x] `IncrementalUpdateService.TriggerManualUpdateAsync` and `ProcessIncrementalUpdateAsync` repeat the relation check.
- [x] Lock acquire, release, unbind, and heartbeat carry repository and branch identity.
- [x] Active lock constraints enforce the repository/branch conflict matrix in both database providers.
- [x] `IndexedBranchRemovalService.RemoveAsync` returns 409 before changing processing data.
- [x] Workspace deletion resolves through `RepositoryWorkspacePath.ForBranch` and stays under the configured root.
- [x] Branch endpoints use an authenticated-only helper whose name cannot be reused for repository-level mutations.
- [x] Visibility refresh uses the repository provider client and never assumes GitHub for GitLab or Gitee rows.

## Dependency map

```text
Provider remote ID -> ConnectedRepositoryService -> Repository + RepositoryBranch
RepositoryBranch -> BranchGenerationTask / IncrementalUpdateTask
Tasks -> branch-aware RepositoryGenerationLock -> existing workers -> RepositoryBranchProcessor
Branch removal -> branch-scoped database cleanup -> safe workspace cleanup
Credential resolver -> RepositoryAnalyzer clone/fetch/remote refs
```

## Tests Before

1. Add SQLite transaction tests for stable remote uniqueness, multi-branch atomicity, and concurrent duplicate requests.
2. Change existing restrictive branch expectations to authenticated-user expectations and keep anonymous 401 tests.
3. Add lock conflict matrix, branch task dedupe, relation mismatch, TLS, incremental deletion, and removal tests.
4. Confirm each test fails for the current repository-wide lock, missing branch filter, missing removal service, or old authorization.

## Refactor and implementation

1. Add stable remote identity and branch-aware lock schema to both providers and `DbInitializer`.
2. Fix task dedupe and lock ownership before creating new orchestration APIs.
3. Fix incremental relation checks and deleted-file behavior.
4. Implement the transactional connected-repository service.
5. Implement branch-only removal and post-commit workspace cleanup.
6. Change only branch mutation authorization.
7. Remove TLS bypasses, revalidate the remote at execution time, and use the runtime credential resolver.
8. Add the GitHub App compatibility adapter and provider-aware visibility refresh.

## Tests After

1. Run the smallest service test after each implementation slice.
2. Run lock and duplicate-request tests repeatedly against SQLite.
3. Run worker tests with two branches from one repository.
4. Verify existing repository delete and visibility tests are unchanged and green.
5. Run migration parity and full backend suites.

## Regression gates

```bash
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~ConnectedRepositoryServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~IndexedBranchRemovalServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter FullyQualifiedName~RepositoryGenerationLockServiceTests
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter "FullyQualifiedName~BranchGenerationTaskServiceTests|FullyQualifiedName~IncrementalUpdateServiceTests|FullyQualifiedName~IncrementalUpdateWorkerTests"
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj --filter "FullyQualifiedName~RepositorySourceSubmitTests|FullyQualifiedName~PrivateRepositoryVisibilityPropertyTests"
dotnet test tests/OpenDeepWiki.Tests/OpenDeepWiki.Tests.csproj
dotnet build OpenDeepWiki.sln
```

## Security and risk

- Never send a connection PAT to a repository host that does not match the normalized provider origin.
- Use database constraints for identity and active locks, then recover from unique conflicts by re-reading.
- Treat workspace paths as untrusted derived input and verify the resolved path before deletion.
- Keep global generation slots to prevent branch independence from exhausting the host.
- Do not call the repository-wide admin cleanup service from branch removal.

## Rollback

- Keep new remote identity and nullable lock columns if application code rolls back.
- Route new imports off while legacy submit continues through `RepositoryProcessingWorker`.
- Do not remove repository or connection rows to undo branch orchestration.

## Todo

- [x] Write identity, atomicity, lock, authorization, incremental, TLS, and removal failing tests.
- [x] Add paired schema changes and `DbInitializer` updates.
- [x] Fix task dedupe, branch locks, and worker leases.
- [x] Add connected repository and removal services and endpoints.
- [x] Relax only branch mutation authorization.
- [x] Pass focused, permission, migration, and full regression gates.

## Success criteria

- [x] One stable remote produces one Connected Repository across connections and renames.
- [x] Multiple branches create and execute independent jobs without partial writes.
- [x] Every authenticated user can manage branches, and dangerous repository permissions remain unchanged.
- [x] Processing branch removal returns 409 and idle removal affects only that branch.
- [x] Clone and fetch use valid TLS and runtime-resolved connection credentials.
