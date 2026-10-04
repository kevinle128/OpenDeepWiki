# Phase 4 cook report: legacy credential migration

Status: DONE_WITH_CONCERNS. Expand, backfill, and switch are delivered with tests. The contract step is authored and tested on copies only. It is not applied and not enabled: the legacy columns and fields stay in the shipped model, both model snapshots, and the `DbInitializer` path. The only failing tests are the 7 baseline failures.

The hook for this run names another report path. I followed the task and wrote this file next to the earlier cook reports, as Phase 3 did. I edited the phase frontmatter (`status: in-progress`, plus a `note` key) and the Todo boxes by hand, as the task said, and did not use `ak plan phase update`. `ak plan validate` accepts the plan directory. The `plan.md` status cell is not edited.

## Results

- Full suite (`OpenDeepWiki.Tests`) with PostgreSQL and the real-format SQLite copy test enabled: 1370 total, 1363 passed, 7 failed, 0 skipped. The 7 failures are exactly `baseline-failing-tests.txt` (checked with a diff of the sorted lists). Before this phase the suite had 1244 tests.
- PostgreSQL ran on a disposable `postgres:16-alpine` container on port 55437 (`OPENDEEPWIKI_TEST_POSTGRES`). The container is removed.
- Phase gates: `LegacyGitCredentialMigrationServiceTests` 40 passed. `RepositoryAnalyzerSourceTests|RepositorySourceSubmitTests|PrivateRepositoryVisibilityPropertyTests`: 58 passed, 6 failed (the 6 baseline analyzer failures). `DbInitializer`: 22 passed. `dotnet build OpenDeepWiki.sln`: 0 errors; the 13 warnings are older CS1591 documentation warnings and one analyzer crash, none from this phase.
- `dotnet ef migrations has-pending-model-changes` is clean for both providers. `dotnet ef migrations list` for both providers shows `AddGitCredentialMigrationRecords` as the last migration and does not show the contract migration. Both model snapshots equal the tool output of the expand migration (diffed against a backup taken before the contract generation).
- `rg "AuthAccount|AuthPassword" src --glob '*.cs' --glob '!**/Migrations/*'` still has hits, as expected before the contract release: the entity, the two DTOs, the submit and admin update rejection checks, `GitCredentialResolver` (the only dual-read), the migration service (the legacy fields are its source), and `DbInitializer` (the contract method).
- Data safety: `data/opendeepwiki.db` was never opened by my processes except for one read-only `sqlite3 -readonly ".backup"`. Its size and modification time are unchanged (`1961984 1791027055` before and after). All schema work ran on copies in the scratchpad. The backup `pre-phase04.db` passes `integrity_check` and was restored into a working copy.
- Startup smoke test: the built application started on a copy of the real-format database (no Git connection tables, one repository with a legacy credential). `DbInitializer` added the new tables, left both legacy columns and the credential row untouched, `integrity_check` was `ok`, and `/health` returned 200. My first smoke run used the Development configuration, which overrides the environment connection string and created a fresh `src/OpenDeepWiki/opendeepwiki.db` (all rows dated by that run). I deleted it. The second run used `ConnectionStrings__Default`.

## What changed

Expand (schema and credential path):

- `GitCredentialMigrationRecord` (one row per repository, unique `RepositoryId`, cascade foreign key): state (`Migrated`, `Failed`, `Blocked`), stable error code, connection ID, attempt count, timestamps. No URL, token, or provider text.
- Both providers: tool-generated migration `AddGitCredentialMigrationRecords` (local `dotnet-ef`), paired idempotent `DbInitializer` DDL, `IContext` and `MasterDbContext` sets. Parity tests cover new, upgraded, and migrated databases on SQLite and PostgreSQL, plus unique and cascade behavior.
- `IGitCredentialResolver` gets `HasUsableCredentialAsync` (connection must exist, be enabled, and decrypt; the legacy password counts only when there is no connection) and a static `HasStoredCredential`. The legacy path logs a safe line with the repository ID when it is used. Resolution itself is unchanged: a repository with a connection never falls back.
- `IGitConnectionAuthorizationService.IsAdminAsync`: the caller exists and has an active Admin role in the database (not from the token). `CanMaintainAsync` shares its helpers.

Switch (stop legacy writes):

- Submit: `GitConnectionId` is new. `authAccount` or `authPassword` and any Git URL with user info return a 400 with a stable code (`LEGACY_CREDENTIAL_FIELDS_REJECTED`, `GIT_URL_CONTAINS_CREDENTIALS`) that names `GitConnectionId` and never repeats the secret. A connection must exist, be enabled, be usable by the caller, and match the origin of the URL. The repository stores `GitConnectionId`, `Provider`, and `ProviderBaseUrl` and an audit event in the same save. A new small middleware, `UseRepositoryConnectionRequestErrors`, turns only `RepositoryConnectionRequestException` into the JSON 400.
- Private visibility and the list use connection semantics through the resolver. The list keeps `HasPassword` for old clients (connection or legacy password) and adds `HasGitConnection` and `GitConnectionId`.
- Admin update: the credential fields are rejected with the same code. `gitConnectionId` reassigns the connection (existence, enabled, origin match) and writes `RepositoryUnassigned` and `RepositoryAssigned` events. Admin-only, as before.

Backfill (`LegacyGitCredentialMigrationService`, `AdminGitConnectionMigrationEndpoints`):

- Admin routes under `/api/admin/git-connection-migration`: `GET dry-run`, `GET status`, `POST migrate`, `POST retry`. Every operation needs a current database Admin on top of the `AdminOnly` group. Nothing runs at startup; the service is registered through `AddLegacyGitCredentialMigration` and a test shows no hosted service depends on it.
- One global order (`CreatedAt`, then `Id`), read page by page, so the creator of a new connection is the owner of the earliest repository and does not depend on batch size or query order. Batches are 1 to 200. A PAT is validated once per run, and failures are remembered too.
- Provider is inferred only for `https://github.com` and `https://gitlab.com`. Everything else with a credential (Gitee, unknown or self-hosted host, non-HTTPS, scp form) is a blocked `UNSUPPORTED_HOST` repository with no provider call. A URL with user info is blocked `URL_CONTAINS_USERINFO`; the URL is never copied into a report, record, log, or error.
- Provider validation comes before the upsert. An existing live connection is reused (creator and credential unchanged). A soft-deleted identity is blocked `CONNECTION_DELETED`. A lost insert race re-reads the winner.
- Assignment goes through `IConnectedRepositoryService.AdoptLegacyRepositoryAsync` (see decisions). Success is recorded only after the credential resolver returns the connection credential and the URL matches the stored origin.
- Provider failures: 401, 403 and other permanent codes are `Blocked`; rate limit, timeout, unavailable, and DNS are `Failed` (retryable). Every outcome is stored per repository as it happens, so a cancelled run keeps finished work and a rerun skips it.
- `status` returns the repair queue and the contract blockers. `contractGateClear` covers only the migration part; the operator gates (backups, key ring, one full update cycle) are in the runbook.

Contract (authored, inert):

- Both providers: `*_RemoveLegacyRepositoryCredentials` migrations generated with `dotnet-ef` (the model was changed temporarily with `Ignore` for the two properties, then the model and both snapshots were restored byte for byte). The `[Migration]` attribute in each designer is commented out with a reason, so the migrator never finds the class. A test checks that it is not discoverable and that it drops exactly the two columns and restores them on down.
- `DbInitializer.ApplyLegacyCredentialContractAsync` (internal, not called by `InitializeAsync`): refuses while an active repository has a legacy credential without a connection or a credential in its URL; does nothing when the columns are gone. PostgreSQL drops both columns in one transaction. SQLite rebuilds `Repositories` in one transaction with foreign keys off: it takes the stored table definition, removes the two column definitions, copies rows by column name, swaps the table, recreates every index and trigger from the stored SQL, and fails on a row count change or any `foreign_key_check` row.
- Runbook: `docs/content/docs/deployment/legacy-credential-migration.mdx` (and its `meta.json` entry). It covers backups, the four steps, routes, state and code tables, the contract gate, the contract release order, the recovery drill, and limits.

## Tests added

- `LegacyGitCredentialMigrationServiceTests` (40): every row of the phase matrix except the schema, contract, and recovery rows, plus resume after a cancellation between the connection and the assignment, one-run PAT cache, deleted, disabled, and corrupt connections, userinfo canary in `GitUrl`, and a canary search of every text column of the database (a PAT may only appear in `Repositories.AuthPassword`, a URL canary only in `Repositories.GitUrl`).
- Resolver tests (legacy diagnostic, usable-credential matrix), `ConnectedRepositoryServiceTests` additions (assignment seam; a connect adopts a migrated row), `RepositoryConnectionSwitchTests` (submit, list, visibility, admin update), `AdminGitConnectionMigrationEndpointsTests` (401, 403 for non-admin token, 403 `ADMIN_REQUIRED` for a token whose database role is gone, results without secrets), `LegacySubmitRouteTests` (400 JSON through HTTP), startup tests, and the schema parity and constraint tests.
- `LegacyCredentialContractTests` (18): SQLite contract on seeded databases (columns, indexes including filtered ones, foreign keys, row counts, row content, `integrity_check`, child joins), refusal cases, idempotency, restore of the contract-before backup, the real-format copy upgrade (expand, stand-in assignment, contract), the EF migration applied to a copy equals the initializer result on SQLite and PostgreSQL, and the key ring drill (database without its key ring fails closed with `connection_secret_unreadable`; database plus key ring restored together resolves the credential).
- The 21 visibility property tests now use `GitCredentialResolver.HasStoredCredential` and generate connection and legacy-password repositories.
- Mutation checks (each failed the expected tests, code restored and diffed): reversed creator order, contract gate without the URL check, assignment without `ProviderBaseUrl`, and admin check removed.

## Deviations and gaps in TDD

- Red was confirmed by compile errors for the missing types and members across all families (`red-build.txt` in the scratchpad), not by a runtime red run per family. After the implementation the new tests passed on the first run. I added the four mutation checks above to show that the tests can fail. The red claim for each family is therefore weaker than the plan asked for.
- The repository is not a git repository, so "restored byte for byte" is checked with backups and `diff` in the scratchpad.

## Decisions I made where the plan was open

1. The relinking seam. `ConnectAsync` needs a provider repository ID, a signed-in user, and branch selection, and it queues branch tasks. A legacy repository has only a URL. I added one method, `AdoptLegacyRepositoryAsync`, to the Phase 3 service and made the backfill call only that. It links without branches, tasks, or locks; checks the connection is usable and the secret is readable; requires the repository URL to be on the connection origin without user info; sets `GitConnectionId`, `Provider`, and `ProviderBaseUrl`; and writes one `RepositoryAssigned` event. The one change to the Phase 3 adoption rule is that `FindAdoptableLegacyAsync` now matches rows with no `ProviderRepositoryId` even when `Provider` is set, so a later connect adopts a migrated row instead of creating a sibling (test added). The backfill does not fill `ProviderRepositoryId`: there is no provider call that finds a remote ID from a URL. Removing both pieces removes the adoption behavior.
2. A soft-deleted connection with the same identity blocks the repository (`CONNECTION_DELETED`). Reviving a shared credential without a person is the same question as creator-on-restore, which I did not touch.
3. Only `github.com` and `gitlab.com` are inferred. A token is not sent to a guessed host.
4. Admin update reassigns a connection; it does not unassign one.
5. Dry run omits the repair queue; status has it.

## Open decisions for you

- Confirm the Phase 3 adoption (kept behind `AdoptLegacyRepositoryAsync` plus one filter line), and that the backfill leaves `ProviderRepositoryId` empty until someone connects the remote.
- Soft-deleted connection policy and creator-on-restore (still open from Phase 2).
- Self-hosted GitLab repositories with legacy credentials cannot be migrated automatically. They need an explicit host-to-provider mapping or an Admin to create the connection and assign the repository. Gitee token handling is still open.
- Rejecting the old fields breaks the current web form for private repositories until Phase 5 sends `gitConnectionId`. `web/` is not changed and was not type-checked.
- `plan.md` Delivery Deviations is not edited by me. Candidates: decision 1, the soft-deleted connection block, the red-evidence gap above.

## Verification gaps

- The backfill ran only against fake providers. No real GitHub or GitLab call, no real token, and no run against the real database copy (its one credentialed repository was only given a stand-in connection in the contract copy test).
- PostgreSQL was tested on a disposable server with data created by the tests. There is no real-format PostgreSQL copy.
- The contract enabling steps in the runbook (turn the attribute on, copy the designer target model into both snapshots, call the initializer method once) were not rehearsed end to end. Only the pieces are tested: the migration operations, their SQL on copies, and the initializer method.
- The contract-before backup restore is a file restore of a SQLite copy; no restored copy was used by the older binary.
- One full synchronization cycle, clone, pull, branch list, incremental update, and rebuild on connection credentials were not observed.
- The runbook is English in a Chinese-language docs site and was not reviewed.
- `LegacySubmitRouteTests` maps `SubmitAsync` with a hand-written `MapPost` and the real error middleware. The smoke run hit the real generated route only anonymously. So "400 with a stable code through the generated MiniApi route" is inferred, not observed.
- The contract tests remove their database files but leave the key ring directories they create in the scratchpad.
- An anonymous call to the legacy submit route still returns 500 because `GetCurrentUserId` throws `UnauthorizedAccessException`. This was already so; I saw it in the smoke run and did not change it.

## Files outside the phase inventory

Source: `Services/Repositories/{ConnectedRepositoryService,IConnectedRepositoryService}.cs` (assignment seam, adoption filter), `Services/Repositories/RepositoryConnectionRequestException.cs` (new), `Services/GitConnections/{IGitCredentialResolver,IGitConnectionAuthorizationService,GitConnectionAuthorizationService}.cs`, `Models/RepositoryListResponse.cs`, `Endpoints/Admin/AdminEndpoints.cs`, `Program.cs` (registration and the error middleware), the two expand migrations and the two inactive contract migrations with designers, both snapshots (tool output of the expand step), `docs/content/docs/deployment/{legacy-credential-migration.mdx,meta.json}`.

Tests, new: `Services/GitConnections/{LegacyGitCredentialMigrationServiceTests,LegacyGitCredentialMigrationStartupTests}.cs`, `Services/Repositories/RepositoryConnectionSwitchTests.cs`, `Endpoints/{AdminGitConnectionMigrationEndpointsTests,LegacySubmitRouteTests}.cs`, `Infrastructure/LegacyCredentialContractTests.cs`. Modified: `DbInitializerGitConnectionTests` (progress table, last migration name, shared helpers made internal), `ConnectedRepositoryServiceTests`, `ConnectedRepositoryEndpointsTests`, `GitCredentialResolverTests`, `PrivateRepositoryVisibilityPropertyTests`, `RepositorySourceSubmitTests`, `LegacyBranchRouteAuthorizationTests`, the three analyzer test files with a resolver double, and both `IContext` test doubles.

## Unresolved questions

- Keep the Phase 3 adoption by URL, or move it out of the shared service?
- Should the backfill fill `ProviderRepositoryId` through a new provider lookup by repository path?
- Should a soft-deleted connection with the same identity be restorable by the backfill?

## Review fixes

Status: DONE. Each fix has a test that failed first (items 1 to 3 confirmed red at runtime), then passed.

1. Deterministic creator after a temporary error. In `LegacyGitCredentialMigrationService`, before a new connection is created, a repository waits when an earlier repository (`CreatedAt`, then `Id`) of the same provider and server still has a `Failed` record. The waiting repository gets a `Failed` record with the new code `EARLIER_REPOSITORY_PENDING`, so no connection is created and the Admin `retry` path handles it. An existing live connection is still reused without this check. Test: `MigrateAsync_WhenTheEarliestRepositoryFailedTemporarily_DoesNotLetALaterOwnerCreateTheConnection` (the earliest repository is rate limited in run 1; run 1 creates no connection; the retry creates one whose creator is the earliest owner). The runbook lists the new code and the limit text now matches.
2. `ConnectedRepositoryService.FindAdoptableLegacyAsync` orders matches: rows with a connection first, then `CreatedAt`, then `Id` (ordinal). Test: `ConnectAsync_ForDuplicateLegacyRowsOfOneRemote_AdoptsTheAssignedRowFirstThenTheEarliest`.
3. Credentials in the URL are removed from the list and detail DTOs (`GitUrl` and `SourceLocation`). New `RepositorySource.RedactUserInfo` is used by `RepositoryService.GetListAsync` and by the admin list and detail in `AdminRepositoryService` (the admin DTOs had the same leak). Stored data is unchanged. Test: `ListAndDetail_NeverReturnCredentialsEmbeddedInTheGitUrl` (also checks the stored URL is intact).
4. Runbook: a warning in the contract gate says that the gate checks only non-deleted repositories, so the legacy credential of a soft-deleted repository is lost when the contract runs; restore and migrate such rows first.

Not changed: creator-on-restore, the contract step (still inert).

Results: full suite with PostgreSQL (`postgres:16-alpine`, port 55438, container removed): 1373 total, 1365 passed, 7 failed, 1 skipped. The 7 failures equal `baseline-failing-tests.txt` (three clean runs). Two earlier full runs each showed one extra failure, `ObjectDisposedException` on `SQLitePCL.sqlite3` in a different SQLite test each time (1 ms, passes alone); I treated it as a parallel-run flake, not related to these fixes, and did not investigate it further. `dotnet build OpenDeepWiki.sln`: 0 errors, 0 warnings.

Files: `Services/GitConnections/{LegacyGitCredentialMigrationService,ILegacyGitCredentialMigrationService}.cs`, `Services/Repositories/{ConnectedRepositoryService,RepositoryService}.cs`, `Services/Admin/AdminRepositoryService.cs`, `OpenDeepWiki.Entities/Repositories/RepositorySource.cs`, the runbook, and the tests `LegacyGitCredentialMigrationServiceTests`, `ConnectedRepositoryServiceTests`, `RepositoryConnectionSwitchTests`.
