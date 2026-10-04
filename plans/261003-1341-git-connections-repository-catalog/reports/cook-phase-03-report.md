# Phase 3 cook report: repository and branch orchestration

Status: DONE_WITH_CONCERNS. All Phase 3 requirements and the inherited Phase 2 follow-ups are implemented. The only failing tests are the 7 baseline failures. The concerns are listed at the end.

The hook for this run names another report path. I followed the task and wrote this file next to the Phase 1 and Phase 2 reports.

## Results

- Full suite (`OpenDeepWiki.Tests`) with PostgreSQL: 1221 total, 1214 passed, 7 failed, 0 skipped. The 7 failures are exactly `baseline-failing-tests.txt` (6 `RepositoryAnalyzerSourceTests`, 1 `RepositorySkillMarkdownBuilderTests`); checked with a diff. Before this phase the suite had 1005 tests.
- PostgreSQL ran against a disposable `postgres:16-alpine` container on port 55435 with `OPENDEEPWIKI_TEST_POSTGRES` set. The container is removed. PostgreSQL-only tests cover schema parity (fresh, upgraded, migrated), the identity and lock indexes, the lock race, the connect race, and branch removal with its foreign keys.
- Phase gates, each green: `ConnectedRepositoryServiceTests` 54, `IndexedBranchRemovalServiceTests` 22, `RepositoryGenerationLockServiceTests` 19, branch task + incremental service + incremental worker 42, `RepositorySourceSubmitTests` + `PrivateRepositoryVisibilityPropertyTests` 48. `dotnet build OpenDeepWiki.sln`: 0 warnings, 0 errors. `dotnet ef migrations has-pending-model-changes` is clean for both providers.
- Every other caller of the lock service and the coordinator passes the repository scope without a branch ID (`AdminRepositoryService` repository rebuild, `RepositoryService.RegenerateAsync`, `RepositoryProcessingWorker`); I read each call site.
- The lock and race tests ran 6 times in a row on SQLite (32 tests each time) with no failure.
- Mutation checks: removing the repository-row write lock made the PostgreSQL lock race fail; making the credential callback return the credential for any URL made the redirect test fail; dropping the old-index `DROP` and flipping a filter made the schema parity test fail. All code is restored.
- Upgrade check on real data: I backed up `data/` to the scratchpad, copied `opendeepwiki.db` and ran `DbInitializer` twice on the copy. The copy got the new columns, the filtered indexes and the lock `BranchId` foreign key, lost the old unique lock index, and passed `integrity_check` and `foreign_key_check`. The original `data/opendeepwiki.db` is untouched (same size and timestamp). The running container was not touched.

## What changed

Inherited Phase 2 follow-ups (done first, each with tests):

- `GitProviderGuardHandler.CreatePrimaryHandler` sets `UseProxy = false`. The direct GitHub handler keeps the environment proxy on purpose: it talks to one fixed public host.
- `192.0.0.0/24` is never allowed, even when listed. NAT64 local-use `64:ff9b:1::/48` (RFC 6052 /48 layout) and SIIT `::ffff:0:a.b.c.d` are unwrapped to the embedded IPv4 address before the policy runs. A public embedded address still passes.
- New HTTP-level test for the legacy branches route: it maps the real `RepositoryService.GetBranchesAsync` method through the real authentication and authorization pipeline. Anonymous gets 401 and the platform is not called. This test was green on first run, because the attribute was already correct.

Schema (both providers, one migration each: `AddRepositoryRemoteIdentityAndBranchLocks`, generated with local `dotnet-ef`):

- `Repository`: `Provider`, `ProviderBaseUrl`, `ProviderRepositoryId`, `DefaultBranch`. Unique filtered index `IX_Repositories_RemoteIdentity` on the three identity columns where the ID is set and the row is not deleted.
- `RepositoryGenerationLock.BranchId` (nullable, foreign key to the branch, cascade). Two filtered unique indexes replace the old single-column one: one repository-scope lock per repository (`BranchId IS NULL`) and one lock per branch (`BranchId IS NOT NULL`). `IncrementalUpdateTask.RequestedBy`.
- `DbInitializer` has paired idempotent SQLite and PostgreSQL steps, including `DROP INDEX IF EXISTS` for the old lock index (the old `CREATE` lines are removed so it is not re-created at startup).
- The Phase 1 and 2 upgrade tests now build the legacy model without the new columns and indexes, and the migration test runs to the new last migration.

Locks and workers:

- `IRepositoryGenerationLockService` methods take an optional trailing `branchId`. A scope must match its branch ID (repository scope has none, branch scope needs one); the service throws `ArgumentException` otherwise. Release, unbind and heartbeat match the owner and, when given, the branch.
- A repository lock excludes every other lock of the repository; a branch lock excludes the repository lock and the same branch only. A stale blocking lock is recovered and replaced.
- The filtered indexes cannot express "a repository lock excludes all branch locks". The acquire therefore runs in a transaction whose first statement updates the repository row to its own value, so concurrent acquires for one repository run in turn. This is proven on PostgreSQL by the mutation check above. It is a no-op on the InMemory provider.
- `WikiGenerationCoordinator`, `WikiGenerationWorkLease`, `BranchGenerationWorker` and `IncrementalUpdateWorker` carry the branch ID. Two branches of one repository now run at the same time and still share the global slots (worker tests use a barrier that only opens when both jobs run together).
- `BranchGenerationTaskService`: `FindActiveBranchTaskAsync` filters by branch; retry looks the branch up with its repository before taking a lock; the actor is stored in `RequestedBy`.

Authorization:

- Branch endpoints (full generation, retry, cancel, task read, task list, incremental trigger, read, retry, list, and the new connect/add/list/remove routes) need a signed-in user and nothing more. The group requires authorization and each handler checks again. The old owner/admin helper is replaced by `AuthorizeBranchOperationAsync` and `AuthorizeBranchTaskOperationAsync`; the old names are gone, so nothing repository-level can reuse them.
- Repository delete (admin policy), visibility (owner rule) and connection maintenance are unchanged. A test on one HTTP host shows that the same user can add a branch and gets 403 on admin delete and on visibility, while the owner is not refused.
- Actor recording: new audit event types (`BranchAdded`, `BranchRebuildRequested`, `BranchSyncRequested`, `BranchTaskRetried`, `BranchTaskCancelled`, `BranchRemoved`, stored as integers, so no schema change) go to `GitConnectionAuditEvents` with the actor and repository ID. Add, remove and assignment events are written in the same transaction as the action. Rebuild, sync, retry and cancel are written after the action and never hide its result. A repository without a connection has no audit table to write to, so the action is logged with identifiers only. `BranchGenerationTask.RequestedBy` and the new `IncrementalUpdateTask.RequestedBy` also hold the actor.

Connected repositories (`ConnectedRepositoryService`, `ConnectedRepositoryEndpoints`):

- `POST /api/v1/connected-repositories`, `POST` and `GET /api/v1/repositories/{id}/indexed-branches`, `DELETE /api/v1/repositories/{id}/indexed-branches/{branchId}`. Envelope `{ success, data }` or `{ success: false, errorCode, message, branches? }`. Provider errors reuse the Phase 2 mapping (429 keeps `Retry-After`).
- Every selected branch is checked against the remote before the write (the provider branch list is scanned page by page, at most 100 pages). The write is one transaction: connection guard, get-or-create repository, branches, languages, one full task and one branch lock per new branch, audit events. Unique-index conflicts roll back and the request re-reads (up to 6 attempts), so concurrent requests converge. A repeated request changes nothing.
- The first statement of the transaction updates the connection row (new `ConcurrencyStamp`) only if it is not deleted and is enabled. This closes the inherited gap: a connection delete that read the old stamp fails its stamp check, and a deleted or disabled connection is rejected inside the transaction. Tests: disabled or deleted after validation writes nothing; a stale delete gets `DbUpdateConcurrencyException`.
- The clone URL is built from the connection origin and the provider path, never taken from the provider answer.
- A new connected repository gets status `Completed`, not `Pending`: `Pending` would send it to the repository-wide worker, which runs branches in order, and full task enqueue refuses `Pending`.

Removal (`IndexedBranchRemovalService`): one transaction cancels pending tasks of the branch, returns 409 `BRANCH_JOB_ACTIVE` if a full or incremental task is processing (the transaction rolls back, nothing changes), then deletes catalogs, documents, languages, translation, incremental and full tasks, graph artifacts, processing logs, locks and the branch row. It never calls the repository-wide cleanup. The workspace goes after the commit (see decisions).

Clone, fetch and remote lookup (`RepositoryAnalyzer`, new `GitRemoteOriginGuard`):

- No `CertificateCheck` callback is installed any more. Tests: option factories hold no bypass; a local TLS server with a self-signed certificate never receives an HTTP request on clone or on fetch (before the change it did).
- For a repository with a connection the remote must be an HTTPS URL without user info on the origin stored in `ProviderBaseUrl`, and the host must pass the Phase 2 address policy, before the credential is read. A repository with a connection but no recorded origin fails closed. An existing workspace whose `origin` is another remote is cloned again instead of fetched.
- The credential handler gives the credential only to the connection origin. libgit2 asks with the URL that challenged it, so after a redirect it asks with the redirect target and the handler refuses. Test: two local HTTP servers, the first redirects to the second, the second challenges with 401; the second never sees an `Authorization` header.

Incremental: relation checks in `TriggerManualUpdateAsync`, `ProcessIncrementalUpdateAsync` and `CheckForUpdatesAsync`; stable `IncrementalUpdateRejectedException` codes mapped to 404 and 409; `RequestedBy`. Deleted files: `IRepositoryAnalyzer.GetDeletedFilesAsync` reports deleted paths and the old path of renames. `ProcessIncrementalUpdateAsync` then returns `RequiresFullGeneration` and does not move the baseline. The worker marks the incremental task `Cancelled` with a message and queues a full generation for that branch after it released its lease.

GitHub App compatibility (`GitHubAppRepositoryAdapter`, used by the user and admin import services): imports keep their routes. When the entry carries the new optional `id`, the repository gets the stable GitHub identity, and a remote that already has that identity is skipped. `ListInstallationReposAsync` also marks `AlreadyImported` by identity. A later connection of the same remote finds the row and attaches itself.

Visibility refresh: new `IRepositoryVisibilityProbe` asks the repository's own provider client with its connection credential. A repository without a provider identity is only checked when it lives on github.com; gitlab.com, gitee.com and other hosts are skipped, and a provider failure keeps the stored value.

Also added (needed by the "task list" and "list" requirements): `GET /api/v1/repositories/{id}/branch-generation-tasks` and `GET /api/v1/repositories/{id}/incremental-updates` (newest first, `branchId` and `limit` filters, authenticated).

## Decisions I made where the plan was open

1. Path uniqueness. The phase text says to replace the `(OrgName, RepoName)` rule. Workspace paths, `RegenerateAsync` and the `/{org}/{repo}` routes all key on that pair, so two remotes with equal paths would share one checkout. After a design check (kongming) I kept the unique index and made the pair a route and workspace slug: `ConnectedRepositoryService` picks the first free slug (`acme`, `acme~github`/`acme~gitlab`, then numbered), joins a GitLab subgroup namespace with `_`, and retries on a unique conflict. Equal remote paths coexist with different slugs. This is a literal deviation from the phase wording.
2. Legacy rows are adopted. A legacy repository with no identity and the same normalized clone URL (case, `.git` and trailing slash ignored, user info never matches) is linked to the connection and gets the identity instead of a sibling row. It keeps its plaintext credential fields, and the resolver prefers the connection. This touches what Phase 4 calls relinking; please confirm it.
3. An existing repository keeps its own connection. If another connection selects the same remote, the original stays, and if the original is disabled the request fails with `CONNECTION_DISABLED` (disabling must block new indexing for everybody). A repository with no connection (App import, legacy) takes the caller's connection; so does one whose connection is gone.
4. A connect or add on an App-imported repository that is still `Pending` returns `REPOSITORY_GENERATION_ACTIVE` (409) until the repository-wide worker finishes.
5. Renames: connect refreshes `GitUrl`, description, default branch, visibility and, when the provider path changed, `OrgName` and `RepoName` (a free slug is chosen). The old workspace directory stays on disk and a new one is cloned. Visibility follows the provider (the scheduled sync already did this).
6. Removal hard-deletes rows, like the admin cleanup, so a re-add has no unique conflict. A soft-deleted branch row (from older cleanups) is restored as a fresh branch on re-add: no baseline, pending state, stale tasks cancelled, stale locks removed.
7. Only full and incremental tasks make removal return 409. Translation and graph jobs do not.
8. Workspace cleanup: the branch directory (`branches/{name}`) is deleted after the commit, only if the path is strictly inside the configured root, is not a link, and no other indexed branch maps to the same sanitized name (`feature/x` and `feature_x` share a directory). A failed delete is recorded in `.pending-workspace-removals` in the repository directory. `IIndexedBranchRemovalService.CleanupRemovedWorkspacesAsync` retries it and skips names that a re-added branch owns. There is no HTTP route or background sweeper for it yet.
9. Branch name rules (`git check-ref-format` subset), at most 50 branches per request, language code pattern, default language `zh` (the submit routes have no default).
10. Repository-level `/regenerate` stays owner/admin; "rebuild" for every authenticated user means a branch full generation.
11. Legacy repositories without a connection keep their old credential path. They get no origin binding and no address check, so redirects of a legacy credential are unchanged.

## Deviations and gaps in TDD

- Red was confirmed for the lock matrix, task dedupe, relation checks, auth, schema model, removal, endpoints, adapter, TLS bypass, deleted files, visibility, and the Phase 2 follow-ups, in each case by a compile error for missing members or a failing assertion. I did not capture a separate red run for: the analyzer origin, address and workspace-origin tests (the guard class existed before the first run), the incremental worker lease and fallback tests, and the `Phase 2` legacy-route HTTP test (green by nature). The schema parity tests were checked by mutation instead.
- Branch removal for the InMemory provider uses tracked deletes; only SQLite and PostgreSQL are exercised.
- `plan.md` Delivery Deviations is not edited by me. Candidates for it: the path-uniqueness deviation (decision 1), legacy adoption (decision 2), and `Completed` as the initial status of connected repositories.

## Files outside the phase inventory

Source: `Services/Repositories/{BranchActionAuditor,GitRemoteOriginGuard,RepositoryVisibilityProbe,WikiGenerationConcurrencyService,IRepositoryAnalyzer,IIncrementalUpdateService,IConnectedRepositoryService}.cs`, `Services/GitHub/{GitHubAppRepositoryAdapter,UserGitHubImportService}.cs`, `Services/Admin/AdminGitHubImportService.cs`, `Models/Admin/GitHubImportModels.cs` (optional `Id`), `Services/GitConnections/{GitLabServerUrlValidator,GitProviderGuardHandler}.cs`, `OpenDeepWiki.Entities/GitConnections/GitConnectionAuditEvent.cs`, `OpenDeepWiki.Entities/Repositories/IncrementalUpdateTask.cs`, the two new migrations with designers and both snapshots (tool output).

Tests, new: `Endpoints/{TestEndpointHost,LegacyBranchRouteAuthorizationTests,BranchOperationEndpointsTests,ConnectedRepositoryEndpointsTests}.cs`, `EFCore/RepositoryOrchestrationModelTests.cs`, `SqliteScratchDatabase.cs`, `Services/Repositories/{FakeRemoteCatalog,BranchGenerationWorkerTests,IncrementalUpdateWorkerLeaseTests,RepositoryAnalyzerRemoteSecurityTests,GitHubAppImportAdapterTests,ConnectedRepositoryServiceTests,IndexedBranchRemovalServiceTests}.cs`. Modified: `BranchGenerationTaskServiceTests`, `WikiGenerationCoordinatorTests`, `IncrementalUpdateServiceTests`, `IncrementalUpdateWorkerTests`, `RepositoryGenerationLockServiceTests`, `RepositoryAnalyzerCredentialSeamTests`, `GitLabServerUrlValidatorTests`, `Infrastructure/DbInitializerGitConnectionTests`.

## Verification gaps

- No real GitHub, GitLab or self-managed server and no real DNS were used. TLS and redirect behavior are proven against loopback servers and fake providers.
- libgit2 (LibGit2Sharp 0.31) exposes no redirect mode, so redirects are controlled only by the credential callback. A redirect target that serves the repository without a challenge gets no credential but is still followed. Also, libgit2 resolves DNS itself, so the address policy check and the connection can see different answers (rebinding). The Phase 2 HTTP client pins addresses; clone and fetch cannot.
- The permission-failure workspace test is skipped on Windows. Windows path rules (case, reserved names) were not exercised.
- The web client was not changed: it does not send `id` in the App import request, so App imports from the UI still get no identity until Phase 5 sends it. `web/` was not type-checked.
- GitHub numeric-ID endpoints still need a real-token check (Phase 6), as noted in the Phase 2 report.
- The new routes are not in any docs page. Phase 6 should document them.

## Unresolved questions

- Decision 2 (legacy adoption by URL): keep it in Phase 3, or move it to the Phase 4 relinking step?
- Decision 1 (slug names such as `acme~gitlab` in routes): accept for now, or plan the larger change (provider segment in routes and workspace paths) for a later phase?
- Creator-on-restore of a soft-deleted connection is still open from Phase 2 and was not touched.
- Should `CleanupRemovedWorkspacesAsync` get an admin route or a background sweep?

## Review fixes

Three review findings are fixed, each test-first. Full suite with PostgreSQL (`postgres:16-alpine` on port 55436, removed afterwards): 1244 total, 1237 passed, 7 failed, 0 skipped. The 7 failures are exactly `baseline-failing-tests.txt`. `dotnet build OpenDeepWiki.sln`: 0 warnings, 0 errors. One earlier full run showed one extra failure, `RepositoryOrchestrationModelTests.Sqlite_RemoteIdentity_IgnoresSoftDeletedRepositories`. It passed 3 of 3 alone and in 2 more full runs, and the fixes do not touch it. I could not reproduce it, so treat it as a rare flake.

1. Workspace cleanup retry. `IIndexedBranchRemovalService.CleanupAllRemovedWorkspacesAsync` finds repositories with a `.pending-workspace-removals` file and retries each one; a failure in one repository does not stop the others. `CleanupRemovedWorkspacesAsync` no longer needs a signed-in user, because a background worker has none and it only removes directories that an earlier removal named. `BranchGenerationWorker` calls the sweep in its polling loop, in its own try block, at most every 5 minutes, so a sweep failure never blocks job dispatch. No route was added. Test: a running worker removes a pending directory and its marker (red when the worker call is disabled: it timed out), plus two service tests for the user-free sweep. The existing 22 removal tests still pass; the anonymous test covers `RemoveAsync` only.
2. Private repository reads. New `RepositoryReadAccess.VisibleTo(IUserContext)`: public, or owner, or Admin. It matches the rule of `RepositoryService.UpdateVisibilityAsync` and `RegenerateAsync` (owner or Admin) plus public. A hidden repository is reported as `REPOSITORY_NOT_FOUND` (404), and a hidden task as `TASK_NOT_FOUND` (404), so existence does not leak. Applied to: branch task read (`GetTaskAsync`) and list, the shared `AuthorizeBranchOperationAsync` (so full generation, retry and cancel use it too), incremental trigger, list, read and retry, and the indexed-branch list, add and remove in the services. Repository delete, visibility, regenerate and connection maintenance are unchanged. Tests: 9 routes return 404 for a stranger and change nothing; owner and Admin are allowed; service tests for list, add and remove. Existing tests that used a private remote with a second user now use a public remote.
3. Kept connection stamp. In `RefreshRepositoryAsync`, when the repository keeps connection B while the request uses connection A, B gets the same conditional stamp UPDATE (not deleted and enabled). If that update matches no row, B is re-read: deleted means the repository takes A, disabled means `CONNECTION_DISABLED`. Tests on SQLite and PostgreSQL: a disable that read B before the connect fails its stamp check with `DbUpdateConcurrencyException`, and the stamp of B changes (red before the fix on all three).

Decision for you: item 2 also hides private repositories from branch mutations of users who cannot see them (add, remove, rebuild, retry, cancel, incremental). The request said mutations stay open "for repositories they can see", so I read it that way. The visibility rule has no department rule, unlike `McpUserResolver`; department-assigned users who are not owners do not see a private repository here. Tell me if that rule should be added.
