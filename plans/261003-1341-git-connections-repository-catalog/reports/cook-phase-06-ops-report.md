# Phase 6 ops report: backend workflow test, deployment, CI, docs, migration and container smoke

Status: DONE_WITH_CONCERNS. The backend, docs and operations half of Phase 6 is delivered. The CI workflows cannot run here, so they are authored and linted only. The E2E harness, the three consecutive E2E runs and the web carry-over items belong to the other subagent and are not covered here.

The task named this path for the report. The hook names `plans/reports/`. I followed the task, as Phases 3 to 5 did.

## Verified here

| Item | Evidence |
|---|---|
| Full backend suite with PostgreSQL (disposable `postgres:16-alpine` on port 55440) | Final run: 1382 tests, 1375 passed, 7 failed, 0 skipped. The 7 failures equal `baseline-failing-tests.txt` (diff is empty). The real-format database copy test ran against a scratchpad copy. |
| `dotnet build OpenDeepWiki.sln` | 0 warnings, 0 errors. |
| Intermittent failures (root cause and fix) | 25 consecutive full runs after the fix: every run 1382 tests, 7 failures, all baseline, 0 `ObjectDisposedException`. Before the fix: 3 extra failures in the first 36 runs. For one of them (`ConnectedRepositoryEndpointsTests`) I captured the `ObjectDisposedException` with its stack. The other two (`RepositoryConnectionSwitchTests`, `RepositoryGenerationLockServiceTests`) were seen with the quiet logger, so their messages were not kept. |
| New workflow integration test | `tests/OpenDeepWiki.Tests/Integration/GitConnectionWorkflowTests.cs`, 3 tests, real SQLite file and real HTTP pipeline (reuses `TestEndpointHost`, `SqliteScratchDatabase`, `FakeHostResolver`). One journey: create connection (second user gets the same row), page the catalog with the cursor, list branches, connect with two branches (two independent tasks), add a third branch as another user (earlier tasks untouched), 409 `BRANCH_JOB_ACTIVE` on a processing branch, cancel and remove another branch, 403 for a non-maintainer, disable (discovery, connect and add return 409 `CONNECTION_DISABLED`, indexed branches stay readable), Admin enables again, no response holds the token. Also anonymous refusal on four routes and a rate-limit failure that recovers. |
| SQLite upgrade of a scratchpad copy of `data/opendeepwiki.db` (made with the online backup API; the original was never opened for write) | The new binary (Production, sandbox without outbound network) started on the copy, `/health` 200, three new tables, indexes present, `integrity_check` ok, `foreign_key_check` empty, row counts of all earlier tables identical. The `RealFormatCopy` expand-and-contract test also passed on the copy. `data/opendeepwiki.db` still has size 1961984 and date 3 Oct 18:30. |
| PostgreSQL upgrade | The old image (Oct 2 build, started as a separate container `odw-p6-legacy` without touching `opendeepwiki-*`) created a legacy PostgreSQL schema (47 tables, no `GitConnections`). I added two synthetic repositories (one with a synthetic legacy password), then started the new binary on that database: health 200, same row counts in every earlier table, three new tables, synthetic rows intact. Column and index definitions of the upgraded database equal those of a fresh database (898 lines, empty diff). |
| Container smoke, SQLite stack (`compose.yaml`) | Project `odw-smoke-sqlite`, ports 18181 and 18190, scratchpad data directory, images `odw-smoke/*:test`. Backend healthy through the new health check, web started after it (`depends_on: service_healthy`), proxy reached the backend, key ring file created in `data/dataprotection-keys`. A synthetic GitLab connection protected with that key ring was readable before and after `restart opendeepwiki` (the test endpoint reached the provider step, `PROVIDER_DNS_FAILURE` for a `.invalid` host, so nothing left the machine). The key file list did not change. Torn down with `down -v`. |
| Container smoke, PostgreSQL stack (`compose.pgsql.yaml`) | Project `odw-smoke-pgsql`, ports 18182 and 18191 (the PostgreSQL port was not published). Same checks, same result. This run also exercised the new `user: "0:0"`. The macOS bind mount does not reproduce the Linux case of a root-owned `./data` directory, so that reason is reasoned, not shown. |
| Runbook rehearsal (`legacy-credential-migration.mdx`) on a scratchpad copy with synthetic legacy rows | Backup of database and key ring, expand on start, dry run (no rows written), anonymous refused (401), migrate (6 processed: 2 `Failed/PROVIDER_DNS_FAILURE`, 4 `Blocked`: Gitee and unknown host `UNSUPPORTED_HOST`, no password `LEGACY_CREDENTIAL_INCOMPLETE`, user info in URL `URL_CONTAINS_USERINFO`), status (`contractGateClear: false`), retry, legacy fields untouched. Recovery drill: database restored without key ring gives 409 `CONNECTION_SECRET_UNREADABLE`; database restored with its key ring reads the credential. No synthetic token appears in any application log. The app ran under `sandbox-exec` with all outbound network denied except the loopback interface. |
| Docs | Built with bun in a scratchpad copy: `lint` clean, `next build` passes (23 pages, so every edited MDX page compiles). Every `/docs/...` link in all pages resolves. The new anchor `#git-connection-secrets` exists in the built HTML. README links checked. |
| Web lint scope | `npx eslint` on the workspace files and on `e2e` and `playwright.config.ts`: 0 problems. |

## Authored but not verified (cannot run here)

- `.github/workflows/quality.yml`, `docker-image.yml`, `release.yml`: only `actionlint` (1.7.7, run in a container) and a YAML load. The baseline-exclusion test filter was checked locally (1375 tests, 0 failed). Nothing was executed on GitHub. In particular I did not prove that the reusable-workflow call checks out the same commit or tag, that `needs: quality` blocks publication, or that the PostgreSQL service container, bun, Playwright install and the docker smoke steps work on a runner.
- `scripts/sealos/sealos-template.yaml`: YAML parse only. The new default `${{ random(48) }}` for the JWT key is assumed to produce a random string of 48 characters. Not verified against Sealos.
- The key-ring env var in the Sealos template is not exercised: the container smoke used the two compose files only.

## What changed (files)

- Tests: `tests/OpenDeepWiki.Tests/Integration/GitConnectionWorkflowTests.cs` (new), `SqlitePools.cs` (new), and the 7 test files that called `ClearAllPools` (`SqliteScratchDatabase.cs`, `Endpoints/GitConnectionEndpointsTests.cs`, `Infrastructure/DbInitializerGitConnectionTests.cs`, `Infrastructure/LegacyCredentialContractTests.cs`, `EFCore/GitConnectionModelTests.cs`, `Services/GitConnections/GitConnectionServiceTests.cs`). Test files outside the single file in the ownership list were edited because the task assigned the flaky-test fix to me.
- Deployment: `compose.yaml` (health check on the backend, `web` waits for it), `compose.pgsql.yaml` (`user: "0:0"`), `scripts/sealos/sealos-template.yaml` (`DataProtection__KeyRingPath=/data/dataprotection-keys`, random JWT default instead of the built-in key that Production rejects).
- CI: `.github/workflows/quality.yml` (new; jobs: workflow lint, backend with PostgreSQL service, frontend, i18n, E2E, docs, migration, Docker smoke), `docker-image.yml` and `release.yml` (publish jobs `needs: quality`, the quality job is called with the commit SHA or the release tag).
- Docs: `api-reference/repositories.mdx`, `architecture/frontend.mdx`, `backend.mdx`, `data-models.mdx`, `configuration/environment-variables.mdx`, `deployment/docker-compose.mdx`, `getting-started/docker-deployment.mdx`, `local-development.mdx`, `README.md`, `README.zh-CN.md`, and `docs/package.json` with `docs/bun.lock`.
- Phase file: three Todo boxes ticked with notes. Frontmatter status and `plan.md` untouched.

## Root cause of the intermittent test failures

Test `Dispose` methods called the process-wide `SqliteConnection.ClearAllPools()`. xUnit runs test classes in parallel, so one class disposed the pooled native handles that another class was opening at that moment. The stack of the captured failure is `SqliteConnection.Open` to `sqlite3_create_function` to `ObjectDisposedException: SQLitePCL.sqlite3` inside `SqliteScratchDatabase.CreateAsync`, in a test that does nothing with pools itself. The earlier one-off failures, including `Sqlite_RemoteIdentity_IgnoresSoftDeletedRepositories`, are most likely the same race: all of them use the same pooled SQLite files and I could not reproduce any of them after the fix, but I did not capture their messages. The fix is `SqlitePools.Release(path)`, which clears only the pool of one file (the pool key is the connection string, and all test code uses `Data Source={path}`). Every `ClearAllPools` call in tests was replaced. The product code has none.

## Deviations and decisions to confirm

1. `docs/package.json` and `docs/bun.lock` gained `tailwind-merge`. The docs build was already broken on the baseline: `docs/lib/cn.ts` imports it and it was in neither file (also with npm). The phase file's `cd docs && npm ci` cannot work either, because `docs` has `bun.lock` and no `package-lock.json`; the CI job uses bun.
2. `compose.pgsql.yaml` now runs the backend as root, as `compose.yaml` already does, so the key ring directory on a Linux bind mount is writable. This lowers the isolation of that stack. The alternative is to document `chown 1654 data`.
3. The compose port contracts are not aligned (SQLite 18081 and 8090, PostgreSQL 8080 and 3000). Changing published ports would break existing deployments. The docs state the actual ports.
4. `quality.yml` excludes the 7 baseline failures by exact test name and runs them in a separate non-blocking step. `npm run lint` on the whole web app has 80 baseline errors, so the blocking lint step covers only the workspace files, `e2e` and `playwright.config.ts`; the full lint is informational. Both should be tightened when the baselines are fixed.
5. New doc sections are in English inside pages that are in Chinese (the runbook from Phase 4 is English). The ASD-STE100 rule was applied. Say if you want them translated.
6. `README.zh-CN.md` was edited too, because it had the same wrong claims (a 28-byte JWT example that Production rejects, and ports that do not match `compose.yaml`).
7. The two publish workflows both call `quality`, so a push that changes `Directory.Packages.props` runs it twice. This costs time, not correctness.
8. `.env.example` was not edited. It already defines `JWT_SECRET_KEY`, and the key ring path is fixed in the compose files.

## Observation, not fixed

On the upgraded SQLite copy, the new binary's `TranslationWorker` logged `UNIQUE constraint failed: TranslationTasks.RepositoryBranchId, TargetLanguageCode` (a `DbUpdateException`, `TranslationWorker.cs:178` creates the task) about 80 ms after start. The copy holds one Completed translation task for the repository. Health stayed 200 and the schema upgrade was clean. The running Oct 2 container does not log it. The worker is not part of this feature and I did not change it. Please check whether a duplicate task is enqueued for an already translated branch.

## Gaps

- GitHub numeric-ID endpoints were not exercised with a real token (none available, and the phase says no real provider calls).
- The successful backfill path (`Migrated`) was not rehearsed end to end: no provider is reachable from the rehearsal. It is covered by `LegacyGitCredentialMigrationServiceTests` and `LegacyCredentialContractTests` on fakes. The contract step stays unapplied by design.
- Certificate or key-management protection of the key ring was not evaluated. The docs state that none is implemented and that the files are plain XML protected by file permissions.
- Structured observability (connection ID, provider, action, result, duration in logs) was not added; it was not in my deliverable list.
- The Sealos template and the Linux root-owned bind mount are not exercised (see above).

## Cleanup

Removed: the PostgreSQL container `odw-p6-pg`, the legacy-image container, both smoke stacks with their networks and volumes, the images `odw-smoke/*:test`, the scratchpad copies of the database. No `opendeepwiki-*` container, volume, image tag or `data/` file was touched. Ports 55440, 18181, 18182, 18190 and 18191 are free again, and no application process of the rehearsal runs. Docker pulls used an empty `DOCKER_CONFIG` because the default credential helper hung.

## Unresolved questions

- Accept root for the PostgreSQL backend (deviation 2), or document a `chown` instead?
- Translate the new English doc sections into Chinese, or keep them?
- Is the `TranslationWorker` duplicate insert known?
